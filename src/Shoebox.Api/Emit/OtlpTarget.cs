using System.Diagnostics.CodeAnalysis;

namespace Shoebox.Api.Emit;

/// <summary>
/// Where this instance's telemetry goes, and what it carries on the way.
///
/// Operator configuration, resolved once at startup. The same two things Snowglobe
/// takes as -endpoint and -headers, in the same formats, set by whoever runs the
/// thing rather than by whoever is looking at it. A visitor has no say in this and
/// does not need one: they read their traces in whatever backend the deployment is
/// wired to.
/// </summary>
public sealed record OtlpTarget(Uri Endpoint, string Headers)
{
    /// <summary>
    /// Precedence: <c>Otlp:Endpoint</c> from configuration, which already merges
    /// appsettings.json with <c>Otlp__Endpoint</c> in the environment, then the
    /// standard <c>OTEL_EXPORTER_OTLP_TRACES_ENDPOINT</c> and
    /// <c>OTEL_EXPORTER_OTLP_ENDPOINT</c>, so an environment already configured the
    /// OpenTelemetry way needs no Shoebox-specific setup.
    ///
    /// Nothing configured resolves to null, and the exporter is then left off rather
    /// than defaulted to localhost, so an unconfigured instance emits nothing instead
    /// of retrying against a port nobody is listening on.
    /// </summary>
    public static OtlpTarget? FromConfiguration(IConfiguration config, out string? error)
    {
        error = null;

        var raw = FirstNonEmpty(
            config["Otlp:Endpoint"],
            config["OTEL_EXPORTER_OTLP_TRACES_ENDPOINT"],
            config["OTEL_EXPORTER_OTLP_ENDPOINT"]);

        if (raw is null) return null;

        if (!TryParseEndpoint(raw, out var uri, out error)) return null;

        var headers = NormalizeHeaders(FirstNonEmpty(
            config["Otlp:Headers"],
            config["OTEL_EXPORTER_OTLP_HEADERS"]));

        return new OtlpTarget(uri, headers);
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
            // First '=' only. Base64 and JWTs both end in padding, and cutting on the
            // last one truncates the credential in a way that presents as a bad key.
            var cut = part.IndexOf('=');
            if (cut <= 0) continue;

            var key = part[..cut].Trim();
            var value = part[(cut + 1)..].Trim();
            if (key.Length == 0) continue;

            pairs.Add($"{key}={value}");
        }

        return string.Join(",", pairs);
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
