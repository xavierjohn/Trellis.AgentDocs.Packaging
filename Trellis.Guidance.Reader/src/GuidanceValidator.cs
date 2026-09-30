namespace Trellis.Guidance.Reader;

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

/// <summary>How serious a validation diagnostic is.</summary>
public enum GuidanceSeverity
{
    /// <summary>A contract violation: consumers would reject the manifest or misread the guidance.</summary>
    Error,
    /// <summary>Guidance that is valid but hard to discover or use.</summary>
    Warning
}

/// <summary>One finding from <see cref="GuidanceValidator"/>.</summary>
/// <param name="Code">Stable identifier: AD001-AD008 for errors, AD101 and up for warnings.</param>
/// <param name="Severity">Error or warning.</param>
/// <param name="Path">Package-relative path the finding concerns, when there is one.</param>
/// <param name="Line">One-based line in <paramref name="Path"/>, when known.</param>
/// <param name="Message">Human-readable explanation.</param>
public sealed record GuidanceDiagnostic(string Code, GuidanceSeverity Severity, string? Path, int? Line, string Message);

/// <summary>Tunable thresholds for <see cref="GuidanceValidator"/>.</summary>
public sealed record GuidanceValidationOptions
{
    /// <summary>Combined size of required documents above which AD101 is reported. Default 32 KiB.</summary>
    public long MaxRequiredBytes { get; init; } = 32 * 1024;
}

/// <summary>The outcome of validating one package's guidance.</summary>
/// <param name="Diagnostics">Every finding, in the order it was found.</param>
/// <param name="DocumentCount">Documents the manifest declares.</param>
/// <param name="RequiredBytes">Combined size of the documents declared required.</param>
public sealed record GuidanceValidation(IReadOnlyList<GuidanceDiagnostic> Diagnostics, int DocumentCount, long RequiredBytes)
{
    /// <summary>Number of error diagnostics.</summary>
    public int ErrorCount => Diagnostics.Count(d => d.Severity == GuidanceSeverity.Error);

    /// <summary>Number of warning diagnostics.</summary>
    public int WarningCount => Diagnostics.Count(d => d.Severity == GuidanceSeverity.Warning);

    /// <summary>True when any error was found.</summary>
    public bool HasErrors => ErrorCount > 0;
}

/// <summary>
/// Checks a package's guidance the way an author needs it checked before publishing: the manifest against the
/// contract (errors) and the documents against what makes guidance discoverable (warnings). The manifest rules are
/// the very code <see cref="GuidanceReader"/> applies, so a package this validator accepts is not rejected by the
/// reader for manifest reasons. It reads a package or an extracted directory only; it does not restore, run MSBuild
/// or write files.
/// </summary>
public static class GuidanceValidator
{
    private const string ManifestPath = "guidance/reference-manifest.json";
    private static readonly Regex InlineLink = new(
        @"\]\((?:<(?<target>[^>]+)>|(?<target>(?:[^()\s]|\([^()\s]*\))+))(?:\s+(?:""[^""]*""|'[^']*'))?\)", RegexOptions.Compiled);
    private static readonly Regex ReferenceLink = new(@"^\s{0,3}\[[^\]]+\]:\s*(?<target><[^>]+>|\S+)", RegexOptions.Compiled);
    private static readonly Regex InlineCode = new(@"(`+)[^`].*?\1", RegexOptions.Compiled);
    private static readonly Regex Heading = new(@"^\s{0,3}#{1,6}\s+(?<text>.*?)\s*#*\s*$", RegexOptions.Compiled);
    private static readonly Regex SetextUnderline = new(@"^\s{0,3}(=+|-+)\s*$", RegexOptions.Compiled);
    private static readonly Regex YamlLine = new(
        @"^(\s*$|\s|#|-(\s|$)|(""[^""]*""|'[^']*'|[^\s:#][^\s:]*)\s*:(\s|$))", RegexOptions.Compiled);
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>Validates a <c>.nupkg</c> file or a directory holding an extracted package.</summary>
    public static GuidanceValidation ValidatePackage(string path, GuidanceValidationOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (!Directory.Exists(path))
            return Validate(ReadPackageFile(path), options);

        var linked = new HashSet<string>(StringComparer.Ordinal);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            return new GuidanceValidation(
                [new GuidanceDiagnostic("AD001", GuidanceSeverity.Error, null, null, "The package directory is a link; validate the real directory.")], 0, 0);
        return Validate(ReadDirectory(Path.GetFullPath(path), linked), options, linked);
    }

    /// <summary>Validates package contents keyed by package-relative path with forward slashes.</summary>
    public static GuidanceValidation Validate(IReadOnlyDictionary<string, byte[]> files, GuidanceValidationOptions? options = null) =>
        Validate(files, options, null);

    private static GuidanceValidation Validate(IReadOnlyDictionary<string, byte[]> files, GuidanceValidationOptions? options,
        IReadOnlySet<string>? linked)
    {
        ArgumentNullException.ThrowIfNull(files);
        options ??= new GuidanceValidationOptions();
        var diagnostics = new List<GuidanceDiagnostic>();
        void Error(string code, string? path, string message, int? line = null) =>
            diagnostics.Add(new GuidanceDiagnostic(code, GuidanceSeverity.Error, path, line, message));
        void Warn(string code, string? path, string message, int? line = null) =>
            diagnostics.Add(new GuidanceDiagnostic(code, GuidanceSeverity.Warning, path, line, message));
        bool IsLinked(string path) => linked is not null && PathChain(path).Any(linked.Contains);

        if (!files.TryGetValue(ManifestPath, out var manifestBytes))
        {
            Error("AD001", ManifestPath, "The package has no guidance manifest.");
            return new GuidanceValidation(diagnostics, 0, 0);
        }

        if (IsLinked(ManifestPath))
        {
            Error("AD001", ManifestPath, "The manifest path contains a link/reparse point, which consumers reject.");
            return new GuidanceValidation(diagnostics, 0, 0);
        }

        var parse = ManifestCheck.Parse(manifestBytes);
        foreach (var issue in parse.Issues)
            Error(issue.Code, issue.Path ?? ManifestPath, issue.Message);

        var readable = new List<Doc>();
        var requiredBytes = 0L;
        foreach (var document in parse.Documents)
        {
            var path = document.Path;
            var canonical = path.Replace('\\', '/');
            if (!canonical.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            {
                Error("AD002", path, "Guidance documents must be Markdown (.md); the tool installs Markdown only.");
                continue;
            }

            if (IsLinked(canonical))
            {
                Error("AD002", path, "The document path contains a link/reparse point, which consumers reject.");
                continue;
            }

            if (!files.TryGetValue(canonical, out var bytes))
            {
                Error("AD002", path, "The document is listed in the manifest but is not in the package.");
                continue;
            }

            if (document.Usage == GuidanceUsage.Required)
                requiredBytes += bytes.Length;
            if (!string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), document.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                Error("AD003", path, "sha256 does not match the packed bytes; regenerate the manifest after changing the document.");
                continue;
            }

            string text;
            try
            {
                text = Decode(bytes);
            }
            catch (DecoderFallbackException)
            {
                Error("AD008", path, "The document is not valid UTF-8, which the tool requires when installing it.");
                continue;
            }

            readable.Add(new Doc(canonical, document.Usage, text));
        }

        AnalyseDocuments(files, readable, requiredBytes, options, Warn);
        return new GuidanceValidation(diagnostics, parse.Documents.Count, requiredBytes);
    }

    private static void AnalyseDocuments(IReadOnlyDictionary<string, byte[]> files, List<Doc> documents,
        long requiredBytes, GuidanceValidationOptions options, Action<string, string?, string, int?> warn)
    {
        if (requiredBytes > options.MaxRequiredBytes)
            warn("AD101", null,
                $"Required documents total {requiredBytes:N0} bytes (about {requiredBytes / 4:N0} tokens), above the {options.MaxRequiredBytes:N0}-byte threshold. " +
                "Every agent reads them before any work; move detail into onDemand documents.", null);

        var byPath = documents.ToDictionary(d => d.Path, StringComparer.Ordinal);
        var slugs = documents.ToDictionary(d => d.Path, d => Slugs(d.Text), StringComparer.Ordinal);
        var edges = documents.ToDictionary(d => d.Path, _ => new HashSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);

        foreach (var document in documents)
        {
            var path = document.Path;
            FrontMatter(path, document.Text, warn);
            foreach (var (target, line) in Links(document.Text))
            {
                var hash = target.IndexOf('#');
                var file = hash < 0 ? target : target[..hash];
                var fragment = hash < 0 ? "" : target[(hash + 1)..];
                string resolved;
                if (file.Length == 0)
                {
                    resolved = path;
                }
                else
                {
                    var query = file.IndexOf('?');
                    if (query >= 0)
                        file = file[..query];
                    if (!file.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                        continue;
                    var combined = Resolve(path, file);
                    if (combined is null)
                    {
                        warn("AD102", path, $"Link '{target}' leaves the package, so it will not resolve once the guidance is installed.", line);
                        continue;
                    }

                    resolved = combined;
                    if (!files.ContainsKey(resolved))
                    {
                        warn("AD102", path, $"Link '{target}' points to '{resolved}', which is not in the package.", line);
                        continue;
                    }

                    if (!byPath.ContainsKey(resolved))
                    {
                        warn("AD102", path, $"Link '{target}' points to '{resolved}', which is in the package but not an installed guidance document, so it will not exist after install.", line);
                        continue;
                    }
                }

                if (!string.Equals(resolved, path, StringComparison.Ordinal))
                    edges[path].Add(resolved);
                if (fragment.Length > 0 && !slugs[resolved].Contains(Unescape(fragment).ToLowerInvariant()))
                    warn("AD102", path, $"Link '{target}' points to a heading that does not exist in '{resolved}'.", line);
            }
        }

        // An agent is told about required and on-demand documents, and follows links from there.
        var reachable = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        foreach (var root in documents.Where(d => d.Usage != GuidanceUsage.Supporting))
        {
            reachable.Add(root.Path);
            queue.Enqueue(root.Path);
        }

        while (queue.Count > 0)
            foreach (var next in edges[queue.Dequeue()].Where(reachable.Add))
                queue.Enqueue(next);
        foreach (var document in documents.Where(d => d.Usage == GuidanceUsage.Supporting && !reachable.Contains(d.Path)))
            warn("AD103", document.Path,
                "No required or on-demand document links to this supporting document, directly or through other documents, and the index does not list supporting documents, so agents cannot discover it.", null);

        var listed = new HashSet<string>(documents.Select(d => d.Path), StringComparer.Ordinal);
        var directories = documents.Select(d => DirectoryOf(d.Path)).Where(d => d.Length > 0).ToHashSet(StringComparer.Ordinal);
        var seenContent = new HashSet<byte[]>(ReferenceEqualityComparer.Instance);
        foreach (var path in listed)
            seenContent.Add(files[path]);
        foreach (var (file, bytes) in files.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            // A package can be reachable under a raw and a decoded spelling; report the file once.
            if (!seenContent.Add(bytes) || !file.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ||
                listed.Contains(file) || !directories.Contains(DirectoryOf(file)))
                continue;
            warn("AD105", file, "This Markdown file sits beside listed guidance but is not in the manifest, so it is not installed. List it or move it.", null);
        }
    }

    private static void FrontMatter(string path, string text, Action<string, string?, string, int?> warn)
    {
        var lines = text.Split('\n');
        if (lines[0].TrimEnd('\r').Trim() != "---")
            return;
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (Regex.IsMatch(line, @"^---\s*$"))
                return;
            if (!YamlLine.IsMatch(line))
            {
                warn("AD104", path, $"The front matter block contains a line that is not YAML, so the closing '---' is probably misplaced: '{Truncate(line)}'.", i + 1);
                return;
            }
        }

        warn("AD104", path, "The front matter block is never closed with '---'.", 1);
    }

    private static IEnumerable<(string Target, int Line)> Links(string text)
    {
        var fence = new FenceTracker();
        var lineNumber = 0;
        foreach (var raw in text.Split('\n'))
        {
            lineNumber++;
            var line = raw.TrimEnd('\r');
            if (fence.InCode(line))
                continue;
            var visible = InlineCode.Replace(line, m => new string(' ', m.Length));
            foreach (Match match in InlineLink.Matches(visible))
            {
                var target = Filter(match.Groups["target"].Value);
                if (target is not null)
                    yield return (target, lineNumber);
            }

            var reference = ReferenceLink.Match(visible);
            if (reference.Success && Filter(reference.Groups["target"].Value) is { } referenceTarget)
                yield return (referenceTarget, lineNumber);
        }

        static string? Filter(string target)
        {
            target = target.Trim('<', '>');
            if (target.Length == 0 || target.StartsWith("//", StringComparison.Ordinal))
                return null;
            var colon = target.IndexOf(':');
            var slash = target.IndexOfAny(['/', '#', '?']);
            return colon >= 0 && (slash < 0 || colon < slash) ? null : target;
        }
    }

    /// <summary>Resolves a relative link against a package-relative document; null when it escapes the package.</summary>
    private static string? Resolve(string document, string relative)
    {
        var parts = new List<string>(DirectoryOf(document).Split('/', StringSplitOptions.RemoveEmptyEntries));
        foreach (var segment in Unescape(relative).Replace('\\', '/').Split('/'))
        {
            if (segment.Length == 0 || segment == ".")
                continue;
            if (segment == "..")
            {
                if (parts.Count == 0)
                    return null;
                parts.RemoveAt(parts.Count - 1);
            }
            else
            {
                parts.Add(segment);
            }
        }

        return string.Join('/', parts);
    }

    /// <summary>GitHub-style heading anchors (ATX and setext), including the -1, -2 suffixes for repeated headings.</summary>
    private static HashSet<string> Slugs(string text)
    {
        var slugs = new HashSet<string>(StringComparer.Ordinal);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        var start = 0;
        if (lines.Length > 0 && lines[0].Trim() == "---")
        {
            var close = Array.FindIndex(lines, 1, l => Regex.IsMatch(l, @"^---\s*$"));
            start = close < 0 ? 0 : close + 1;
        }

        void Add(string heading)
        {
            var slug = Regex.Replace(heading.Replace("`", "").ToLowerInvariant(), @"[^\p{L}\p{N}\p{M}_\- ]", "").Replace(' ', '-');
            counts.TryGetValue(slug, out var seen);
            counts[slug] = seen + 1;
            slugs.Add(seen == 0 ? slug : slug + "-" + seen);
        }

        var fence = new FenceTracker();
        for (var i = start; i < lines.Length; i++)
        {
            var line = lines[i];
            if (fence.InCode(line))
                continue;
            if (Heading.Match(line) is { Success: true } atx)
            {
                Add(atx.Groups["text"].Value);
            }
            else if (i + 1 < lines.Length && line.Trim().Length > 0 && SetextUnderline.IsMatch(lines[i + 1]) &&
                !Regex.IsMatch(line, @"^\s{0,3}([-*+>|]|\d+[.)])\s"))
            {
                Add(line.Trim());
            }
        }

        return slugs;
    }

    private static Dictionary<string, byte[]> ReadDirectory(string root, HashSet<string> linked)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        void Walk(string directory, string prefix)
        {
            foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos().OrderBy(e => e.Name, StringComparer.Ordinal))
            {
                var relative = prefix.Length == 0 ? entry.Name : prefix + "/" + entry.Name;
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    // Never follow a link: its target may be outside the package, and consumers reject linked paths.
                    linked.Add(relative);
                    if (entry is FileInfo)
                        files[relative] = [];
                    continue;
                }

                if (entry is DirectoryInfo)
                    Walk(entry.FullName, relative);
                else
                    files[relative] = File.ReadAllBytes(entry.FullName);
            }
        }

        Walk(root, "");
        return files;
    }

    private static Dictionary<string, byte[]> ReadPackageFile(string path)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        using var archive = ZipFile.OpenRead(path);
        foreach (var entry in archive.Entries.Where(e => !e.FullName.EndsWith('/')))
        {
            using var stream = entry.Open();
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            var bytes = buffer.ToArray();
            // Entry names are kept as written. NuGet percent-decodes names when it extracts a package, so a decoded
            // spelling is also made available, pointing at the same bytes.
            files[entry.FullName] = bytes;
            if (entry.FullName.Contains('%') && Unescape(entry.FullName) is var decoded && decoded != entry.FullName)
                files.TryAdd(decoded, bytes);
        }

        return files;
    }

    private static string Decode(byte[] bytes) =>
        StrictUtf8.GetString(bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }) ? bytes[3..] : bytes);

    private static IEnumerable<string> PathChain(string path)
    {
        var parts = path.Split('/');
        for (var i = 1; i <= parts.Length; i++)
            yield return string.Join('/', parts.Take(i));
    }

    private static string DirectoryOf(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? "" : path[..slash];
    }

    private static string Truncate(string value) => value.Length <= 60 ? value : value[..57] + "...";

    private static string Unescape(string value)
    {
        try { return Uri.UnescapeDataString(value); }
        catch (UriFormatException) { return value; }
    }

    /// <summary>Tracks Markdown fenced code so links and headings inside it are ignored, honouring fence length.</summary>
    private sealed class FenceTracker
    {
        private char _marker;
        private int _length;

        /// <summary>True when the line is a fence marker or inside a fenced block.</summary>
        public bool InCode(string line)
        {
            var trimmed = line.TrimStart();
            if (trimmed.Length >= 3 && (trimmed[0] is '`' or '~'))
            {
                var marker = trimmed[0];
                var length = 0;
                while (length < trimmed.Length && trimmed[length] == marker)
                    length++;
                if (length >= 3)
                {
                    if (_marker == '\0')
                    {
                        _marker = marker;
                        _length = length;
                    }
                    else if (marker == _marker && length >= _length && string.IsNullOrWhiteSpace(trimmed[length..]))
                    {
                        _marker = '\0';
                    }

                    return true;
                }
            }

            return _marker != '\0';
        }
    }

    private sealed record Doc(string Path, GuidanceUsage Usage, string Text);
}
