using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;

namespace Shoebox.Api.Emit;

/// <summary>
/// Where one run's telemetry goes, and what it carries on the way.
///
/// This deliberately mirrors Snowglobe's cmd/snowglobe/main.go. Same precedence,
/// same header format, same TLS-unless-told-otherwise default, so that knowing one
/// tool means knowing the other. The difference is only in how the explicit value
/// arrives: Snowglobe takes -endpoint and -headers on the command line, and Shoebox
/// takes them from whoever is looking at the page, because a hosted sandbox has no
/// command line to pass flags on.
/// </summary>
public sealed record OtlpTarget(Uri Endpoint, string Headers)
{
    /// <summary>
    /// Distinguishes one target from another. Never logged and never exported: the
    /// headers can carry an API key, so this is for keying, not for display.
    /// </summary>
    public string Key => $"{Endpoint}|{Headers}";

    /// <summary>
    /// Precedence: the explicit value, then OTEL_EXPORTER_OTLP_TRACES_ENDPOINT, then
    /// OTEL_EXPORTER_OTLP_ENDPOINT, then nothing at all. Nothing means the exporter
    /// is left off rather than defaulted to localhost, so an unconfigured instance
    /// emits nothing instead of retrying against a port nobody is listening on.
    ///
    /// Headers follow the same shape: the explicit value, then
    /// OTEL_EXPORTER_OTLP_HEADERS.
    /// </summary>
    public static OtlpTarget? Resolve(string? endpoint, string? headers, out string? error)
    {
        error = null;

        var raw = FirstNonEmpty(
            endpoint,
            Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_TRACES_ENDPOINT"),
            Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT"));

        if (raw is null) return null;

        if (!TryParseEndpoint(raw, out var uri, out error)) return null;

        var headerString = NormalizeHeaders(FirstNonEmpty(
            headers,
            Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_HEADERS")));

        return new OtlpTarget(uri, headerString);
    }

    /// <summary>
    /// Accepts a URL, and a bare host:port for the sake of anyone arriving from
    /// Snowglobe, where host:port is the only spelling. A missing scheme means https,
    /// matching Snowglobe's TLS-unless--insecure default: guessing plaintext for
    /// somebody who typed a hostname would silently downgrade their traffic.
    /// </summary>
    public static bool TryParseEndpoint(string raw, [NotNullWhen(true)] out Uri? uri, out string? error)
    {
        uri = null;
        error = null;
        raw = raw.Trim();

        if (raw.Length == 0)
        {
            error = "endpoint is empty";
            return false;
        }

        if (!raw.Contains("://", StringComparison.Ordinal)) raw = "https://" + raw;

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var parsed))
        {
            error = "endpoint is not a valid URL";
            return false;
        }

        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
        {
            error = $"endpoint scheme must be http or https, not {parsed.Scheme}";
            return false;
        }

        uri = parsed;
        return true;
    }

    /// <summary>
    /// "key=value,key2=value2", the OTEL_EXPORTER_OTLP_HEADERS format, which is also
    /// what Snowglobe's -headers takes. Round-tripped rather than passed through so a
    /// malformed pair is dropped here instead of confusing the exporter.
    /// </summary>
    public static string NormalizeHeaders(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

        var pairs = new List<string>();
        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var cut = part.IndexOf('=');
            if (cut <= 0) continue;

            var key = part[..cut].Trim();
            var value = part[(cut + 1)..].Trim();
            if (key.Length == 0) continue;

            pairs.Add($"{key}={value}");
        }

        return string.Join(",", pairs);
    }

    /// <summary>
    /// Refuses the addresses that make SSRF worth attempting: loopback, link-local,
    /// unique-local and the private ranges, plus anything that resolves to them.
    ///
    /// This is the whole of the answer to a hosted instance accepting a destination
    /// from a stranger. There is no switch to turn the feature off, because a switch
    /// only the operator can reach is not a mitigation for a tool whose promise is no
    /// account and no install: refusing the addresses worth forging against is.
    ///
    /// Skipped in Development, where localhost is the whole point: a Collector on
    /// 4317 is the normal thing to be pointing at while working.
    ///
    /// Known residual: validation resolves the name, and the exporter resolves it
    /// again when it connects. A name that answers differently between those two
    /// moments would slip past this. Closing it means pinning the resolved address
    /// through to the socket, which the OTLP exporter does not expose.
    /// </summary>
    public static bool IsReachableTarget(Uri endpoint, bool isDevelopment, out string? error)
    {
        error = null;
        if (isDevelopment) return true;

        var host = endpoint.DnsSafeHost;

        IPAddress[] addresses;
        if (IPAddress.TryParse(host, out var literal))
        {
            addresses = new[] { literal };
        }
        else
        {
            try
            {
                addresses = Dns.GetHostAddresses(host);
            }
            catch (SocketException)
            {
                error = "endpoint host does not resolve";
                return false;
            }
        }

        if (addresses.Length == 0)
        {
            error = "endpoint host does not resolve";
            return false;
        }

        foreach (var address in addresses)
        {
            if (IsPrivate(address))
            {
                error = "endpoint resolves to a private or loopback address";
                return false;
            }
        }

        return true;
    }

    private static bool IsPrivate(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)) return true;

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return b[0] switch
            {
                10 => true,
                127 => true,
                169 when b[1] == 254 => true,  // link-local, and 169.254.169.254 is the cloud metadata service
                172 when b[1] >= 16 && b[1] <= 31 => true,
                192 when b[1] == 168 => true,
                0 => true,
                _ => false,
            };
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal) return true;
            if (address.IsIPv4MappedToIPv6) return IsPrivate(address.MapToIPv4());

            var b = address.GetAddressBytes();
            if ((b[0] & 0xFE) == 0xFC) return true; // fc00::/7 unique local
        }

        return false;
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
        }

        return null;
    }
}
