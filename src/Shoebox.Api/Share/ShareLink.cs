using System.IO.Compression;
using System.Text;
using Shoebox.Api.Session;

namespace Shoebox.Api.Share;

/// <summary>
/// Builds the link that carries a whole diagram, server-side, because the clients that most need
/// one cannot make it.
/// </summary>
/// <remarks>
/// The format is the SPA's own (<c>src/Shoebox.Spa/src/app/shoebox/diagram-url.ts</c>): deflate-raw,
/// then base64url, in the URL fragment. The fragment is a privacy decision — it never leaves the
/// browser, so a diagram full of real service names stays out of access logs, CDN logs and Referer
/// headers. The shoebox id goes in the query string, where the server needs it.
/// <para>
/// This exists because an agent cannot deflate. Until now the instruction set told a model that if
/// it could not build the link it should hand over the diagram text and let the human paste it,
/// which is honest and a poor substitute for a link somebody can click. Encoding is cheap and the
/// server can already do it, so it should.
/// </para>
/// </remarks>
public static class ShareLink
{
    /// <summary>Past this, links start failing silently in mail clients and chat apps.</summary>
    public const int LengthWarning = 8000;

    public static string Encode(string diagram)
    {
        var raw = Encoding.UTF8.GetBytes(diagram ?? string.Empty);
        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            deflate.Write(raw, 0, raw.Length);
        }

        return Convert.ToBase64String(output.ToArray())
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    public static string Decode(string encoded)
    {
        var padded = (encoded ?? string.Empty).Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - padded.Length % 4) % 4);
        using var input = new MemoryStream(Convert.FromBase64String(padded));
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);
        using var reader = new StreamReader(deflate, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// The whole link: the shoebox in the query so runs from it are grouped, the diagram in the
    /// fragment so it never reaches a log.
    /// </summary>
    public static string For(string origin, string diagram, string? shoeboxId)
    {
        var query = string.IsNullOrWhiteSpace(shoeboxId)
            ? string.Empty
            : $"?{ShoeboxConstants.QueryParamName}={Uri.EscapeDataString(shoeboxId)}";

        return $"{origin.TrimEnd('/')}/{query}#d={Encode(diagram)}";
    }
}
