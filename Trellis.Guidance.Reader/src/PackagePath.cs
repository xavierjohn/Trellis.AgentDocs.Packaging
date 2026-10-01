namespace Trellis.Guidance.Reader;

using System.Text;

/// <summary>Shared package-relative path spelling, identity, and resolution rules.</summary>
internal static class PackagePath
{
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    public static string Separators(string path) => path.Replace('\\', '/');

    public static bool SafeRelative(string path)
    {
        if (string.IsNullOrEmpty(path) || path[0] is '/' or '\\' ||
            path.Any(c => c is < ' ' or '<' or '>' or ':' or '"' or '|' or '?' or '*'))
            return false;
        return Separators(path).Split('/').All(PortableSegment);
    }

    public static bool PortableSegment(string segment) =>
        segment.Length > 0 && segment is not ("." or "..") &&
        !segment.EndsWith(' ') && !segment.EndsWith('.') &&
        !segment.Any(c => c is < ' ' or '<' or '>' or ':' or '"' or '|' or '?' or '*' or '\\') &&
        !ReservedNames.Contains(segment.Split('.')[0]);

    public static bool TryCanonical(string path, out string canonical) =>
        TryCollapse([], path, out canonical);

    public static string? Canonical(string path)
    {
        return TryCanonical(path, out var canonical) && canonical.Length > 0 ? canonical : null;
    }

    public static string? Resolve(string document, string relative)
    {
        var parts = new List<string>(Directory(document).Split('/', StringSplitOptions.RemoveEmptyEntries));
        return TryCollapse(parts, Separators(Unescape(relative)), out var resolved) ? resolved : null;
    }

    private static bool TryCollapse(List<string> parts, string path, out string collapsed)
    {
        foreach (var segment in path.Split('/'))
        {
            if (segment.Length == 0 || segment == ".")
                continue;
            if (segment != "..")
                parts.Add(segment);
            else if (parts.Count == 0)
            {
                collapsed = "";
                return false;
            }
            else
                parts.RemoveAt(parts.Count - 1);
        }

        collapsed = string.Join('/', parts);
        return true;
    }

    public static IEnumerable<string> Chain(string path)
    {
        var parts = path.Split('/');
        for (var i = 1; i <= parts.Length; i++)
            yield return string.Join('/', parts.Take(i));
    }

    public static string Directory(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? "" : path[..slash];
    }

    public static string NormalizeIdentity(string path)
    {
        try { return path.Normalize(NormalizationForm.FormC); }
        catch (ArgumentException) { return path; }
    }

    public static string Unescape(string value)
    {
        try { return Uri.UnescapeDataString(value); }
        catch (UriFormatException) { return value; }
    }
}
