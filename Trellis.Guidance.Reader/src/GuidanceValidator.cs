namespace Trellis.Guidance.Reader;

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Markdig;
using Markdig.Extensions.AutoIdentifiers;
using Markdig.Extensions.Yaml;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

/// <summary>How serious a validation diagnostic is.</summary>
public enum GuidanceSeverity
{
    /// <summary>A contract violation: consumers would reject the manifest or misread the guidance.</summary>
    Error,
    /// <summary>Guidance that is valid but hard to discover or use.</summary>
    Warning
}

/// <summary>One finding from <see cref="GuidanceValidator"/>.</summary>
/// <param name="Code">Stable identifier: AD001-AD010 for errors, AD101 and up for warnings.</param>
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

    /// <summary>Combined size of every listed guidance document above which AD106 is reported. Default 4 MiB.</summary>
    public long MaxTotalGuidanceBytes { get; init; } = 4 * 1024 * 1024;

    /// <summary>Number of required and on-demand documents (index entries) above which AD107 is reported. Default 100.</summary>
    public int MaxIndexedDocuments { get; init; } = 100;
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
/// reader for manifest reasons. Documents are read with a Markdown parser (links and heading anchors, GitHub
/// style) and a YAML parser (front matter), not with pattern matching. It reads a package or an extracted
/// directory only; it does not restore, run MSBuild or write files.
/// </summary>
/// <remarks>
/// A directory is judged by its contents: links inside it are errors, because consumers reject them. Where the
/// directory lives is not: a package directory under a linked parent (for example macOS <c>/tmp</c>) is fine,
/// because the consumer's own copy lives in its NuGet cache, not at the author's path.
/// </remarks>
public static class GuidanceValidator
{
    private const string ManifestPath = "guidance/reference-manifest.json";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly AngleSharp.Html.Parser.HtmlParser HtmlParser = new();
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePreciseSourceLocation()
        .UseYamlFrontMatter()
        .UseAutoIdentifiers(AutoIdentifierOptions.GitHub)
        .Build();

    /// <summary>Validates a <c>.nupkg</c> file or a directory holding an extracted package.</summary>
    public static GuidanceValidation ValidatePackage(string path, GuidanceValidationOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var problems = new List<GuidanceDiagnostic>();
        if (!Directory.Exists(path))
        {
            using var archive = ZipFile.OpenRead(path);
            var package = ArchiveSource(archive, problems);
            return Validate(package.Names, package.Read, options, null, problems);
        }

        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            return new GuidanceValidation(
                [new GuidanceDiagnostic("AD001", GuidanceSeverity.Error, null, null, "The package directory is itself a link; validate the real directory.")], 0, 0);
        var linked = new HashSet<string>(StringComparer.Ordinal);
        var directory = DirectorySource(Path.GetFullPath(path), linked, problems);
        return Validate(directory.Names, directory.Read, options, linked, problems);
    }

    /// <summary>Validates package contents keyed by package-relative path with forward slashes.</summary>
    public static GuidanceValidation Validate(IReadOnlyDictionary<string, byte[]> files, GuidanceValidationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(files);
        var problems = new List<GuidanceDiagnostic>();
        return Validate(new HashSet<string>(files.Keys, StringComparer.Ordinal), path =>
        {
            var bytes = files[path];
            if (bytes.Length <= LimitFor(path))
                return bytes;
            problems.Add(TooLarge(path));
            return null;
        }, options, null, problems);
    }

    /// <summary>
    /// Validates a package presented as the names it contains and a way to read a named file. Nothing is read until the
    /// manifest says it is guidance, so unrelated files never cost memory or time.
    /// </summary>
    private static GuidanceValidation Validate(IReadOnlySet<string> names, Func<string, byte[]?> read,
        GuidanceValidationOptions? options, IReadOnlySet<string>? linked, List<GuidanceDiagnostic> problems)
    {
        options ??= new GuidanceValidationOptions();
        var diagnostics = problems;
        void Error(string code, string? path, string message, int? line = null) =>
            diagnostics.Add(new GuidanceDiagnostic(code, GuidanceSeverity.Error, path, line, message));
        void Warn(string code, string? path, string message, int? line = null) =>
            diagnostics.Add(new GuidanceDiagnostic(code, GuidanceSeverity.Warning, path, line, message));
        bool IsLinked(string path) => linked is not null && PathChain(path).Any(linked.Contains);

        if (!names.Contains(ManifestPath))
        {
            Error("AD001", ManifestPath, "The package has no guidance manifest.");
            return new GuidanceValidation(diagnostics, 0, 0);
        }

        if (IsLinked(ManifestPath))
        {
            Error("AD001", ManifestPath, "The manifest path contains a link/reparse point, which consumers reject.");
            return new GuidanceValidation(diagnostics, 0, 0);
        }

        if (read(ManifestPath) is not { } manifestBytes)
            return new GuidanceValidation(diagnostics, 0, 0);
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

            if (!names.Contains(canonical))
            {
                Error("AD002", path, "The document is listed in the manifest but is not in the package.");
                continue;
            }

            if (read(canonical) is not { } bytes)
                continue;

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

            readable.Add(new Doc(canonical, document.Usage, text, document.Description, bytes.Length));
        }

        AnalyseDocuments(names, readable, requiredBytes, options, Warn);
        return new GuidanceValidation(diagnostics, parse.Documents.Count, requiredBytes);
    }

    private static void AnalyseDocuments(IReadOnlySet<string> names, List<Doc> documents,
        long requiredBytes, GuidanceValidationOptions options, Action<string, string?, string, int?> warn)
    {
        if (requiredBytes > options.MaxRequiredBytes)
            warn("AD101", null,
                $"Required documents total {requiredBytes:N0} bytes (about {requiredBytes / 4:N0} tokens), above the {options.MaxRequiredBytes:N0}-byte threshold. " +
                "Every agent reads them before any work; move detail into onDemand documents.", null);

        var totalBytes = documents.Sum(d => (long)d.Length);
        if (totalBytes > options.MaxTotalGuidanceBytes)
            warn("AD106", null,
                $"The listed guidance totals {totalBytes:N0} bytes, above the {options.MaxTotalGuidanceBytes:N0}-byte budget. Every consumer installs all of it.", null);
        var indexed = documents.Where(d => d.Usage != GuidanceUsage.Supporting).ToArray();
        if (indexed.Length > options.MaxIndexedDocuments)
            warn("AD107", null,
                $"{indexed.Length:N0} documents are listed in consumers' indexes, above the budget of {options.MaxIndexedDocuments:N0}. Mark detail documents supporting and link to them.", null);
        foreach (var duplicate in indexed.Where(d => d.Usage == GuidanceUsage.OnDemand && d.Description is not null).GroupBy(d => d.Description!, StringComparer.Ordinal)
                     .Where(g => g.Count() > 1))
            warn("AD108", null,
                $"{string.Join(", ", duplicate.Select(d => d.Path))} share the description '{duplicate.Key}', so an agent cannot tell which to open.", null);

        var byPath = documents.ToDictionary(d => d.Path, StringComparer.Ordinal);
        var parsed = documents.ToDictionary(d => d.Path, d => Markdown.Parse(d.Text, Pipeline), StringComparer.Ordinal);
        var slugs = parsed.ToDictionary(pair => pair.Key, pair => Anchors(pair.Value), StringComparer.Ordinal);
        var edges = documents.ToDictionary(d => d.Path, _ => new HashSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);

        foreach (var document in documents)
        {
            var path = document.Path;
            FrontMatter(path, document.Text, parsed[path], warn);
            foreach (var (target, line) in Links(parsed[path]))
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
                    if (file.StartsWith('/') || file.StartsWith('\\'))
                    {
                        warn("AD102", path, $"Link '{target}' is root-relative; it resolves against the reader's repository, not this package. Use a relative path.", line);
                        continue;
                    }

                    var combined = Resolve(path, file);
                    if (combined is null)
                    {
                        warn("AD102", path, $"Link '{target}' leaves the package, so it will not resolve once the guidance is installed.", line);
                        continue;
                    }

                    if (!combined.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                    {
                        // Only listed Markdown documents are installed: images, code samples and data files are not.
                        warn("AD102", path, $"Link '{target}' points to '{combined}', which is not a Markdown document, so it is not installed.", line);
                        continue;
                    }

                    resolved = combined;
                    if (!names.Contains(resolved))
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
        foreach (var file in names.Where(f => f.EndsWith(".md", StringComparison.OrdinalIgnoreCase) &&
                     !listed.Contains(f) && directories.Contains(DirectoryOf(f))).Order(StringComparer.Ordinal))
            warn("AD105", file, "This Markdown file sits beside listed guidance but is not in the manifest, so it is not installed. List it or move it.", null);
    }

    /// <summary>
    /// A block of front matter is YAML, so it is parsed as YAML. A closing fence that lands after the body leaves prose
    /// inside the block, which is not valid YAML; an unclosed block is reported as such.
    /// </summary>
    private static void FrontMatter(string path, string text, MarkdownDocument document, Action<string, string?, string, int?> warn)
    {
        var newline = text.IndexOf('\n');
        var first = (newline < 0 ? text : text[..newline]).Trim();
        if (first != "---")
            return;
        var block = document.OfType<YamlFrontMatterBlock>().FirstOrDefault();
        if (block is null)
        {
            warn("AD104", path, "The front matter block is never closed with '---'.", 1);
            return;
        }

        try
        {
            new YamlStream().Load(new StringReader(block.Lines.ToString()));
        }
        catch (YamlException e)
        {
            warn("AD104", path,
                $"The front matter is not valid YAML, so the closing '---' is probably misplaced: {e.Message.Split('\n')[0].Trim()}",
                (int)e.Start.Line + 1);
        }
    }

    private static IEnumerable<(string Target, int Line)> Links(MarkdownDocument document)
    {
        foreach (var link in document.Descendants<LinkInline>())
        {
            var target = Filter(link.Url);
            if (target is not null)
                yield return (target, link.Line + 1);
        }

        // Markdown allows raw HTML, and <img src> and <a href> are local links too. Each HTML node Markdown found (an
        // inline tag, or a whole HTML block) goes through a real HTML parser, so attribute boundaries, unquoted values,
        // comments and script/style content are handled as a browser would, and data-href is not href.
        foreach (var html in document.Descendants<HtmlInline>())
            foreach (var target in HtmlTargets(html.Tag))
                if (Filter(target) is { } found)
                    yield return (found, html.Line + 1);
        foreach (var block in document.Descendants<HtmlBlock>())
        {
            var lines = Enumerable.Range(0, block.Lines.Count).Select(i => block.Lines.Lines[i].Slice.ToString()).ToArray();
            foreach (var target in HtmlTargets(string.Join("\n", lines)))
                if (Filter(target) is { } found)
                {
                    // The parser does not report positions: point at the first line that mentions the target.
                    var index = Array.FindIndex(lines, l => l.Contains(target, StringComparison.Ordinal));
                    yield return (found, block.Line + 1 + Math.Max(index, 0));
                }
        }

        static IEnumerable<string> HtmlTargets(string html)
        {
            foreach (var element in HtmlParser.ParseDocument(html).All)
            {
                if (element.GetAttribute("href") is { Length: > 0 } href)
                    yield return href;
                if (element.GetAttribute("src") is { Length: > 0 } src)
                    yield return src;
            }
        }

        static string? Filter(string? target)
        {
            if (string.IsNullOrEmpty(target) || target.StartsWith("//", StringComparison.Ordinal))
                return null;
            var colon = target.IndexOf(':');
            var slash = target.IndexOfAny(['/', '#', '?']);
            return colon >= 0 && (slash < 0 || colon < slash) ? null : target;
        }
    }

    /// <summary>The heading anchors GitHub would generate, from the rendered heading text.</summary>
    private static HashSet<string> Anchors(MarkdownDocument document) =>
        document.Descendants<HeadingBlock>().Select(h => h.GetAttributes().Id).OfType<string>()
            .ToHashSet(StringComparer.Ordinal);

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

    // Resource limits: a package can be a large native library, and an archive can lie about how much it expands to.
    // Names are enumerated under a hard entry cap, and a file is read only when the manifest declares it, bounded per file
    // and in total against the bytes actually streamed, never against the size an archive claims.
    private const long MaxManifestBytes = 1024 * 1024;
    private const long MaxDocumentBytes = 8 * 1024 * 1024;
    private const long MaxTotalReadBytes = 64 * 1024 * 1024;
    private const int MaxEntries = 100_000;

    private static long LimitFor(string logicalPath) => logicalPath == ManifestPath ? MaxManifestBytes : MaxDocumentBytes;

    /// <summary>The names a package contains and a way to read one of them, which reports a problem and returns null when it cannot.</summary>
    private sealed record Source(HashSet<string> Names, Func<string, byte[]?> Read);

    private static Source DirectorySource(string root, HashSet<string> linked, List<GuidanceDiagnostic> problems)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var aliases = new PathAliases(problems);
        var tooMany = false;
        void Walk(string directory, string prefix)
        {
            foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos().OrderBy(e => e.Name, StringComparer.Ordinal))
            {
                if (names.Count >= MaxEntries)
                {
                    if (!tooMany)
                        problems.Add(TooMany());
                    tooMany = true;
                    return;
                }

                var relative = prefix.Length == 0 ? entry.Name : prefix + "/" + entry.Name;
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    // Never follow a link: its target may be outside the package, and consumers reject linked paths.
                    linked.Add(relative);
                    if (entry is FileInfo)
                        names.Add(relative);
                    continue;
                }

                if (entry is DirectoryInfo)
                {
                    Walk(entry.FullName, relative);
                    continue;
                }

                if (aliases.Add(relative))
                    names.Add(relative);
            }
        }

        Walk(root, "");
        var total = 0L;
        return new Source(names, path =>
        {
            var info = new FileInfo(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar)));
            if (!info.Exists)
                return null;
            if (info.Length > LimitFor(path) || total + info.Length > MaxTotalReadBytes)
            {
                problems.Add(TooLarge(path));
                return null;
            }

            var bytes = File.ReadAllBytes(info.FullName);
            total += bytes.Length;
            return bytes;
        });
    }

    /// <summary>
    /// Presents a package the way NuGet exposes it: entries are addressed by their percent-decoded logical path, and two
    /// entries that would extract to the same place (the same path, a case or Unicode-normalisation alias, or a file that
    /// is also a directory) are an error rather than a silent choice of bytes.
    /// </summary>
    private static Source ArchiveSource(ZipArchive archive, List<GuidanceDiagnostic> problems)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
        var aliases = new PathAliases(problems);
        var count = 0;
        foreach (var entry in archive.Entries.Where(e => !e.FullName.EndsWith('/')))
        {
            if (++count > MaxEntries)
            {
                problems.Add(TooMany());
                break;
            }

            // Extraction resolves '.', '..' and empty segments (Path.GetFullPath), so entries are tracked by the path
            // they land on; an entry that climbs out of the package root is rejected outright.
            var decoded = Unescape(entry.FullName).Replace('\\', '/');
            var logical = IsRooted(decoded) ? null : Canonical(decoded);
            if (logical is null)
            {
                problems.Add(new GuidanceDiagnostic("AD009", GuidanceSeverity.Error, decoded, null,
                    "This entry's path is rooted, escapes the package root or is empty, so NuGet cannot extract it."));
                continue;
            }

            if (logical.Split('/').FirstOrDefault(IsUnportableSegment) is { } unportable)
            {
                problems.Add(new GuidanceDiagnostic("AD009", GuidanceSeverity.Error, logical, null,
                    $"The segment '{unportable}' cannot be extracted portably: Windows drops trailing dots and spaces, so it lands on a different path, and rejects device names and characters such as : * ? \" < > |."));
                continue;
            }

            if (!aliases.Add(logical))
                continue;
            names.Add(logical);
            entries[logical] = entry;
        }

        var total = 0L;
        return new Source(names, path =>
        {
            if (!entries.TryGetValue(path, out var entry))
                return null;
            var limit = LimitFor(path);
            if (entry.Length > limit || total + entry.Length > MaxTotalReadBytes)
            {
                problems.Add(TooLarge(path));
                return null;
            }

            using var stream = entry.Open();
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
            {
                buffer.Write(chunk, 0, read);
                if (buffer.Length > limit || total + buffer.Length > MaxTotalReadBytes)
                {
                    problems.Add(TooLarge(path));
                    return null;
                }
            }

            total += buffer.Length;
            return buffer.ToArray();
        });
    }

    /// <summary>A leading separator (rooted or UNC) or a drive letter: NuGet refuses to extract these.</summary>
    private static bool IsRooted(string path) =>
        path.StartsWith('/') || (path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':');

    /// <summary>Windows trailing dots and spaces, device names and forbidden characters make a segment extract elsewhere or fail.</summary>
    private static bool IsUnportableSegment(string segment) =>
        segment.EndsWith(' ') || segment.EndsWith('.') ||
        segment.Any(c => c is < ' ' or '<' or '>' or ':' or '"' or '|' or '?' or '*') ||
        segment.Split('.')[0].ToUpperInvariant() is "CON" or "PRN" or "AUX" or "NUL" or "COM1" or "COM2" or "COM3" or "COM4"
            or "COM5" or "COM6" or "COM7" or "COM8" or "COM9" or "LPT1" or "LPT2" or "LPT3" or "LPT4" or "LPT5" or "LPT6"
            or "LPT7" or "LPT8" or "LPT9";

    /// <summary>Collapses empty, '.' and '..' segments; null when the path climbs out of the root or nothing is left.</summary>
    private static string? Canonical(string path)
    {
        var parts = new List<string>();
        foreach (var segment in path.Split('/'))
        {
            if (segment.Length == 0 || segment == ".")
                continue;
            if (segment != "..")
                parts.Add(segment);
            else if (parts.Count == 0)
                return null;
            else
                parts.RemoveAt(parts.Count - 1);
        }

        return parts.Count == 0 ? null : string.Join('/', parts);
    }

    /// <summary>Tracks normalised, case-insensitive paths so entries that extract onto one another are reported once.</summary>
    private sealed class PathAliases(List<GuidanceDiagnostic> problems)
    {
        private readonly Dictionary<string, bool> _seen = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Returns false, after reporting AD009, when the path collides with one already added.</summary>
        public bool Add(string logical)
        {
            var parts = logical.Split('/');
            for (var i = 1; i < parts.Length; i++)
            {
                var prefix = Normalise(string.Join('/', parts.Take(i)));
                if (!_seen.TryGetValue(prefix, out var isFile))
                    _seen[prefix] = false;
                else if (isFile)
                    return Collide(logical, prefix);
            }

            var full = Normalise(logical);
            if (_seen.ContainsKey(full))
                return Collide(logical, full);
            _seen[full] = true;
            return true;
        }

        private bool Collide(string logical, string other)
        {
            problems.Add(new GuidanceDiagnostic("AD009", GuidanceSeverity.Error, logical, null,
                $"This entry collides with '{other}' when the package is extracted: the same path after decoding, case or Unicode normalisation, or a file that is also a directory. NuGet's extracted copy would be ambiguous."));
            return false;
        }

        private static string Normalise(string path)
        {
            try { return path.Normalize(NormalizationForm.FormC); }
            catch (ArgumentException) { return path; }
        }
    }

    private static GuidanceDiagnostic TooLarge(string path) => new("AD010", GuidanceSeverity.Error, path, null,
        "The file exceeds the validator's resource limits (manifest 1 MiB, document 8 MiB, 64 MiB read in total) and was not read.");

    private static GuidanceDiagnostic TooMany() => new("AD010", GuidanceSeverity.Error, null, null,
        $"The package has more than {MaxEntries:N0} entries, which is over the validator's resource limit; the rest were not examined.");

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

    private static string Unescape(string value)
    {
        try { return Uri.UnescapeDataString(value); }
        catch (UriFormatException) { return value; }
    }

    private sealed record Doc(string Path, GuidanceUsage Usage, string Text, string? Description, int Length);
}
