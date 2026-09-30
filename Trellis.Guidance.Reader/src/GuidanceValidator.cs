namespace Trellis.Guidance.Reader;

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
/// <param name="Code">Stable identifier: ADnnn for errors, ADnnn (100 and up) for warnings.</param>
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
/// contract (errors AD001-AD007) and the documents against what makes guidance discoverable (warnings AD101-AD105).
/// It reads a package or an extracted directory only; it does not restore, run MSBuild or write files.
/// </summary>
public static class GuidanceValidator
{
    private const string ManifestPath = "guidance/reference-manifest.json";
    private static readonly Regex InlineLink = new(@"\]\((?<target>[^)\s]+)(?:\s+(?:""[^""]*""|'[^']*'))?\)", RegexOptions.Compiled);
    private static readonly Regex ReferenceLink = new(@"^\s{0,3}\[[^\]]+\]:\s*(?<target>\S+)", RegexOptions.Compiled);
    private static readonly Regex Heading = new(@"^\s{0,3}#{1,6}\s+(?<text>.*?)\s*#*\s*$", RegexOptions.Compiled);

    /// <summary>Validates a <c>.nupkg</c> file or a directory holding an extracted package.</summary>
    public static GuidanceValidation ValidatePackage(string path, GuidanceValidationOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return Validate(Directory.Exists(path) ? ReadDirectory(path) : ReadPackageFile(path), options);
    }

    /// <summary>Validates package contents keyed by package-relative path with forward slashes.</summary>
    public static GuidanceValidation Validate(IReadOnlyDictionary<string, byte[]> files, GuidanceValidationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(files);
        options ??= new GuidanceValidationOptions();
        var diagnostics = new List<GuidanceDiagnostic>();
        void Error(string code, string? path, string message, int? line = null) =>
            diagnostics.Add(new GuidanceDiagnostic(code, GuidanceSeverity.Error, path, line, message));
        void Warn(string code, string? path, string message, int? line = null) =>
            diagnostics.Add(new GuidanceDiagnostic(code, GuidanceSeverity.Warning, path, line, message));

        if (!files.TryGetValue(ManifestPath, out var manifestBytes))
        {
            Error("AD001", ManifestPath, "The package has no guidance manifest.");
            return new GuidanceValidation(diagnostics, 0, 0);
        }

        var declared = ReadManifest(manifestBytes, Error);
        if (declared is null)
            return new GuidanceValidation(diagnostics, 0, 0);

        var documents = new List<Declared>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in declared)
        {
            if (entry.Path is null)
                continue;
            var alias = entry.Path.Replace('\\', '/').Normalize(NormalizationForm.FormC);
            if (!seen.Add(alias))
            {
                Error("AD007", entry.Path, "The document is listed more than once (compared after Unicode normalisation, ignoring case).");
                continue;
            }

            documents.Add(entry);
        }

        foreach (var entry in documents)
        {
            var path = entry.Path!;
            var pathOk = GuidanceReader.SafePath(path) && path.EndsWith(".md", StringComparison.OrdinalIgnoreCase);
            if (!pathOk)
                Error("AD002", path, "The path must be a portable relative Markdown path (no rooted, '..', device or trailing-dot segments).");
            else if (!files.ContainsKey(path))
                Error("AD002", path, "The document is listed in the manifest but is not in the package.");

            if (entry.Sha256 is null || entry.Sha256.Length != 64 || !entry.Sha256.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f')))
                Error("AD003", path, "sha256 must be 64 lowercase hexadecimal characters.");
            else if (pathOk && files.TryGetValue(path, out var bytes) &&
                !string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), entry.Sha256, StringComparison.OrdinalIgnoreCase))
                Error("AD003", path, "sha256 does not match the packed bytes; regenerate the manifest after changing the document.");

            if (entry.Usage is null || !GuidanceReader.TryUsage(entry.Usage, out _))
                Error("AD004", path, "usage must be required, onDemand or supporting.");

            var needsDescription = entry.Usage is "required" or "onDemand";
            if (entry.Description is null)
            {
                if (needsDescription)
                    Error("AD005", path, $"A {entry.Usage} document needs a description that says when to open it.");
            }
            else if (!GuidanceReader.ValidDescription(entry.Description, out _))
            {
                Error("AD005", path, "description must be one non-blank line of at most 200 characters, without control, format or line-separator characters.");
            }
        }

        if (documents.Count > 0 && !documents.Any(d => d.Usage is "required" or "onDemand"))
            Error("AD006", ManifestPath, "A non-empty manifest needs at least one required or onDemand document; agents are never told about supporting documents on their own.");

        var alias2 = documents.Where(d => d.Path is not null).Select(d => d.Path!.Replace('\\', '/').Normalize(NormalizationForm.FormC)).ToArray();
        foreach (var path in alias2.Where(p => alias2.Any(o => o.StartsWith(p + "/", StringComparison.OrdinalIgnoreCase))))
            Error("AD007", path, "A document path is also used as a directory prefix by another document.");

        var required = documents.Where(d => d.Usage == "required" && d.Path is not null && files.ContainsKey(d.Path)).ToArray();
        var requiredBytes = required.Sum(d => (long)files[d.Path!].Length);

        // Warnings only make sense for documents that exist.
        var readable = documents.Where(d => d.Path is not null && files.ContainsKey(d.Path) &&
            GuidanceReader.SafePath(d.Path)).ToList();
        AnalyseDocuments(files, readable, requiredBytes, options, Warn);

        return new GuidanceValidation(diagnostics, documents.Count, requiredBytes);
    }

    private static void AnalyseDocuments(IReadOnlyDictionary<string, byte[]> files, List<Declared> documents,
        long requiredBytes, GuidanceValidationOptions options, Action<string, string?, string, int?> warn)
    {
        if (requiredBytes > options.MaxRequiredBytes)
            warn("AD101", null,
                $"Required documents total {requiredBytes:N0} bytes (about {requiredBytes / 4:N0} tokens), above the {options.MaxRequiredBytes:N0}-byte threshold. " +
                "Every agent reads them before any work; move detail into onDemand documents.", null);

        var texts = documents.ToDictionary(d => d.Path!, d => Decode(files[d.Path!]), StringComparer.Ordinal);
        var headingSlugs = texts.ToDictionary(pair => pair.Key, pair => Slugs(pair.Value), StringComparer.Ordinal);
        var linkedFrom = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (path, text) in texts)
        {
            FrontMatter(path, text, warn);
            foreach (var (target, line) in Links(text))
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

                    if (!texts.ContainsKey(resolved))
                    {
                        warn("AD102", path, $"Link '{target}' points to '{resolved}', which is in the package but not listed in the manifest, so it is not installed.", line);
                        continue;
                    }
                }

                if (!string.Equals(resolved, path, StringComparison.Ordinal))
                    linkedFrom.Add(resolved);
                if (fragment.Length > 0 && headingSlugs.TryGetValue(resolved, out var slugs) &&
                    !slugs.Contains(Unescape(fragment).ToLowerInvariant()))
                    warn("AD102", path, $"Link '{target}' points to a heading that does not exist in '{resolved}'.", line);
            }
        }

        foreach (var document in documents.Where(d => d.Usage == "supporting" && !linkedFrom.Contains(d.Path!)))
            warn("AD103", document.Path, "No other document links to this supporting document, and the index does not list supporting documents, so agents cannot discover it.", null);

        var listed = new HashSet<string>(documents.Select(d => d.Path!), StringComparer.Ordinal);
        var directories = documents.Select(d => DirectoryOf(d.Path!)).Where(d => d.Length > 0).ToHashSet(StringComparer.Ordinal);
        foreach (var file in files.Keys.Where(f => f.EndsWith(".md", StringComparison.OrdinalIgnoreCase) &&
                     !listed.Contains(f) && directories.Contains(DirectoryOf(f))).Order(StringComparer.Ordinal))
            warn("AD105", file, "This Markdown file sits beside listed guidance but is not in the manifest, so it is not installed. List it or move it.", null);
    }

    private static void FrontMatter(string path, string text, Action<string, string?, string, int?> warn)
    {
        var lines = text.Split('\n');
        if (lines.Length == 0 || lines[0].TrimEnd('\r').Trim() != "---")
            return;
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (Regex.IsMatch(line, @"^---\s*$"))
                return;
            if (Regex.IsMatch(line, @"^#{1,6}\s"))
            {
                warn("AD104", path, "The front matter block contains what looks like a Markdown heading; the closing '---' is probably misplaced.", i + 1);
                return;
            }
        }

        warn("AD104", path, "The front matter block is never closed with '---'.", 1);
    }

    private static IEnumerable<(string Target, int Line)> Links(string text)
    {
        var fence = '\0';
        var fenceLength = 0;
        var lineNumber = 0;
        foreach (var raw in text.Split('\n'))
        {
            lineNumber++;
            var line = raw.TrimEnd('\r');
            var trimmed = line.TrimStart();
            if (trimmed.Length >= 3 && (trimmed[0] is '`' or '~'))
            {
                var marker = trimmed[0];
                var length = 0;
                while (length < trimmed.Length && trimmed[length] == marker)
                    length++;
                if (length >= 3)
                {
                    if (fence == '\0')
                    {
                        fence = marker;
                        fenceLength = length;
                    }
                    else if (marker == fence && length >= fenceLength && string.IsNullOrWhiteSpace(trimmed[length..]))
                    {
                        fence = '\0';
                    }

                    continue;
                }
            }

            if (fence != '\0')
                continue;
            foreach (Match match in InlineLink.Matches(line))
            {
                var target = Filter(match.Groups["target"].Value);
                if (target is not null)
                    yield return (target, lineNumber);
            }

            var reference = ReferenceLink.Match(line);
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

    /// <summary>GitHub-style heading anchors, including the -1, -2 suffixes for repeated headings.</summary>
    private static HashSet<string> Slugs(string text)
    {
        var slugs = new HashSet<string>(StringComparer.Ordinal);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var fence = '\0';
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                fence = fence == '\0' ? trimmed[0] : fence == trimmed[0] ? '\0' : fence;
                continue;
            }

            if (fence != '\0' || Heading.Match(line) is not { Success: true } match)
                continue;
            var slug = Regex.Replace(match.Groups["text"].Value.Replace("`", "").ToLowerInvariant(), @"[^\p{L}\p{N}\p{M}_\- ]", "")
                .Replace(' ', '-');
            counts.TryGetValue(slug, out var seen);
            counts[slug] = seen + 1;
            slugs.Add(seen == 0 ? slug : slug + "-" + seen);
        }

        return slugs;
    }

    private static List<Declared>? ReadManifest(byte[] bytes, Action<string, string?, string, int?> error)
    {
        void Fail(string message) => error("AD001", ManifestPath, message, null);
        try
        {
            using var parsed = JsonDocument.Parse(bytes.AsMemory(bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }) ? 3 : 0));
            var manifest = parsed.RootElement;
            if (!GuidanceReader.Object(manifest) || GuidanceReader.DuplicateProperties(manifest))
            {
                Fail("The manifest must be a JSON object with unique property names.");
                return null;
            }

            if (!manifest.TryGetProperty("schemaVersion", out var schema) || !schema.TryGetInt64(out var version))
            {
                Fail("schemaVersion is required and must be an integer.");
                return null;
            }

            if (version != 1)
            {
                Fail($"schemaVersion {version} is not supported; this validator reads schemaVersion 1.");
                return null;
            }

            var ok = true;
            foreach (var property in manifest.EnumerateObject().Where(p => p.Name is not ("schemaVersion" or "documents" or "publisherMetadata")))
            {
                Fail($"Unknown manifest field '{property.Name}'.");
                ok = false;
            }

            if (manifest.TryGetProperty("publisherMetadata", out var metadata))
            {
                if (!GuidanceReader.Object(metadata))
                {
                    Fail("publisherMetadata must be an object.");
                    ok = false;
                }
                else
                {
                    foreach (var key in metadata.EnumerateObject().Where(p => !GuidanceReader.Namespaced(p.Name)))
                    {
                        Fail($"publisherMetadata key '{key.Name}' must be namespaced, for example 'org.example'.");
                        ok = false;
                    }
                }
            }

            if (!GuidanceReader.TryArray(manifest, "documents", out var rawDocuments))
            {
                Fail("documents is required and must be an array.");
                return null;
            }

            var result = new List<Declared>();
            foreach (var raw in rawDocuments.EnumerateArray())
            {
                if (!GuidanceReader.Object(raw) || GuidanceReader.DuplicateProperties(raw))
                {
                    Fail("Every document must be an object with unique property names.");
                    ok = false;
                    continue;
                }

                foreach (var property in raw.EnumerateObject().Where(p => p.Name is not ("path" or "sha256" or "usage" or "description")))
                {
                    Fail($"Unknown document field '{property.Name}'.");
                    ok = false;
                }

                string? Text(string name) => raw.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                    ? value.GetString() : null;
                var path = Text("path");
                if (string.IsNullOrEmpty(path))
                {
                    error("AD002", ManifestPath, "A document has no path.", null);
                    ok = false;
                    continue;
                }

                result.Add(new Declared(path, Text("sha256"), Text("usage"), Text("description")));
            }

            return ok || result.Count > 0 ? result : null;
        }
        catch (JsonException e)
        {
            Fail("The manifest is not valid JSON: " + e.Message);
            return null;
        }
    }

    private static Dictionary<string, byte[]> ReadDirectory(string root)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var full = Path.GetFullPath(root);
        foreach (var file in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(full, file).Replace('\\', '/');
            files[relative] = File.ReadAllBytes(file);
        }

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
            files[Uri.UnescapeDataString(entry.FullName)] = buffer.ToArray();
        }

        return files;
    }

    private static string Decode(byte[] bytes) =>
        new UTF8Encoding(false).GetString(bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }) ? bytes[3..] : bytes);

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

    private sealed record Declared(string? Path, string? Sha256, string? Usage, string? Description);
}
