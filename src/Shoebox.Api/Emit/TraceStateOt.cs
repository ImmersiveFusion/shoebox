namespace Shoebox.Api.Emit;

/// <summary>
/// Sets the OpenTelemetry sampling threshold in a W3C tracestate string.
///
/// The <c>ot</c> list-member is OpenTelemetry's own. Its value is a
/// <c>;</c>-separated list of <c>key:value</c> sub-keys, and <c>th</c> is the
/// sampling threshold: <c>th:0</c> means nothing was rejected, so the span stream
/// is complete. Other sub-keys (such as <c>rv</c>, the randomness value) are kept.
///
/// W3C rules that apply here: a modified member moves to the front of the list,
/// every other vendor's member is left alone and in order, and a list holds at
/// most 32 members, dropping from the right when it would overflow.
/// </summary>
public static class TraceStateOt
{
    public const string Key = "ot";
    public const string FullThreshold = "th:0";

    private const int MaxListMembers = 32;

    /// <summary>
    /// Returns <paramref name="traceState"/> with the <c>ot</c> member first and
    /// its <c>th</c> sub-key set to <c>0</c>. A null or empty input gives
    /// <c>ot=th:0</c>.
    /// </summary>
    public static string WithFullThreshold(string? traceState)
    {
        string? existingOt = null;
        var others = new List<string>();

        if (!string.IsNullOrWhiteSpace(traceState))
        {
            foreach (var raw in traceState.Split(','))
            {
                var member = raw.Trim();
                if (member.Length == 0) continue;

                var eq = member.IndexOf('=');
                var key = eq < 0 ? member : member[..eq];
                if (key == Key)
                {
                    // Keys are unique in a valid list. If one arrives with a
                    // duplicate anyway, the left-most (most recent) one wins.
                    existingOt ??= eq < 0 ? string.Empty : member[(eq + 1)..];
                    continue;
                }

                others.Add(member);
            }
        }

        var subKeys = new List<string> { FullThreshold };
        if (existingOt is not null)
        {
            foreach (var raw in existingOt.Split(';'))
            {
                var sub = raw.Trim();
                if (sub.Length == 0) continue;

                var colon = sub.IndexOf(':');
                var subKey = colon < 0 ? sub : sub[..colon];
                if (subKey == "th") continue;

                subKeys.Add(sub);
            }
        }

        var members = new List<string>(Math.Min(others.Count + 1, MaxListMembers))
        {
            $"{Key}={string.Join(';', subKeys)}",
        };
        members.AddRange(others.Take(MaxListMembers - 1));

        return string.Join(',', members);
    }
}
