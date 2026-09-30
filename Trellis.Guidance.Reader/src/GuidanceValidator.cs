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
            return Validate(ReadPackageFile(path, problems), options, null, problems);

        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            return new GuidanceValidation(
                [new GuidanceDiagnostic("AD001", GuidanceSeverity.Error, null, null, "The package directory is itself a link; validate the real directory.")], 0, 0);
        var linked = new HashSet<string>(StringComparer.Ordinal);
        return Validate(ReadDirectory(Path.GetFullPath(path), linked, problems), options, linked, problems);
    }

    /// <summary>Validates package contents keyed by package-relative path with forward slashes.</summary>
    public static GuidanceValidation Validate(IReadOnlyDictionary<string, byte[]> files, GuidanceValidationOptions? options = null) =>
        Validate(files, options, null, []);

    private static GuidanceValidation Validate(IReadOnlyDictionary<string, byte[]> files, GuidanceValidationOptions? options,
        IReadOnlySet<string>? linked, List<GuidanceDiagnostic> problems)
    {
        ArgumentNullException.ThrowIfNull(files);
        options ??= new GuidanceValidationOptions();
        var diagnostics = new List<GuidanceDiagnostic>(problems);
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

            readable.Add(new Doc(canonical, document.Usage, text, document.Description, bytes.Length));
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

        var totalBytes = documents.Sum(d => (long)d.Length);
        if (totalBytes > options.MaxTotalGuidanceBytes)
            warn("AD106", null,
                $"The listed guidance totals {totalBytes:N0} bytes, above the {options.MaxTotalGuidanceBytes:N0}-byte budget. Every consumer installs all of it.", null);
        var indexed = documents.Where(d => d.Usage != GuidanceUsage.Supporting).ToArray();
        if (indexed.Length > options.MaxIndexedDocuments)
            warn("AD107", null,
                $"{indexed.Length:N0} documents are listed in consumers' indexes, above the budget of {options.MaxIndexedDocuments:N0}. Mark detail documents supporting and link to them.", null);
        foreach (var duplicate in indexed.Where(d => d.Description is not null).GroupBy(d => d.Description!, StringComparer.Ordinal)
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
        foreach (var file in files.Keys.Where(f => f.EndsWith(".md", StringComparison.OrdinalIgnoreCase) &&
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
    // Only the manifest and Markdown files are ever read, each bounded, and the total is bounded too.
    private const long MaxManifestBytes = 1024 * 1024;
    private const long MaxDocumentBytes = 8 * 1024 * 1024;
    private const long MaxTotalReadBytes = 64 * 1024 * 1024;
    private const int MaxReadFiles = 10_000;

    private static bool IsRead(string logicalPath) =>
        logicalPath == ManifestPath || logicalPath.EndsWith(".md", StringComparison.OrdinalIgnoreCase);

    private static long LimitFor(string logicalPath) => logicalPath == ManifestPath ? MaxManifestBytes : MaxDocumentBytes;

    private static Dictionary<string, byte[]> ReadDirectory(string root, HashSet<string> linked, List<GuidanceDiagnostic> problems)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var total = 0L;
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
                {
                    Walk(entry.FullName, relative);
                    continue;
                }

                if (!IsRead(relative))
                    continue;
                var length = ((FileInfo)entry).Length;
                if (length > LimitFor(relative) || total + length > MaxTotalReadBytes || files.Count >= MaxReadFiles)
                {
                    problems.Add(TooLarge(relative));
                    continue;
                }

                total += length;
                files[relative] = File.ReadAllBytes(entry.FullName);
            }
        }

        Walk(root, "");
        return files;
    }

    /// <summary>
    /// Reads a package the way NuGet exposes it: entries are addressed by their percent-decoded logical path. Two entries
    /// that decode to the same path are ambiguous, so they are an error rather than a silent choice of bytes.
    /// </summary>
    private static Dictionary<string, byte[]> ReadPackageFile(string path, List<GuidanceDiagnostic> problems)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var total = 0L;
        using var archive = ZipFile.OpenRead(path);
        foreach (var entry in archive.Entries.Where(e => !e.FullName.EndsWith('/')))
        {
            var logical = Unescape(entry.FullName).Replace('\\', '/');
            if (!seen.Add(logical))
            {
                problems.Add(new GuidanceDiagnostic("AD009", GuidanceSeverity.Error, logical, null,
                    "Two archive entries decode to this path, so NuGet's extracted copy is ambiguous. Remove the duplicate."));
                files.Remove(logical);
                continue;
            }

            if (!IsRead(logical))
                continue;
            if (entry.Length > LimitFor(logical) || total + entry.Length > MaxTotalReadBytes || files.Count >= MaxReadFiles)
            {
                problems.Add(TooLarge(logical));
                continue;
            }

            using var stream = entry.Open();
            using var buffer = new MemoryStream();
            // The declared length is not trusted: stop copying once the limit is passed.
            var chunk = new byte[81920];
            int read;
            while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
            {
                buffer.Write(chunk, 0, read);
                if (buffer.Length > LimitFor(logical))
                    break;
            }

            if (buffer.Length > LimitFor(logical))
            {
                problems.Add(TooLarge(logical));
                continue;
            }

            total += buffer.Length;
            files[logical] = buffer.ToArray();
        }

        return files;
    }

    private static GuidanceDiagnostic TooLarge(string path) => new("AD010", GuidanceSeverity.Error, path, null,
        "The file exceeds the validator's resource limits (manifest 1 MiB, document 8 MiB, 64 MiB and 10,000 files in total) and was not read.");

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
