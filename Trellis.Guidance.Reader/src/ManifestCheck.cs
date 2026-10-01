namespace Trellis.Guidance.Reader;

using System.Text;
using System.Text.Json;

/// <summary>A declared document that passed the manifest's own rules.</summary>
internal sealed record ManifestDocument(string Path, string Sha256, GuidanceUsage Usage, string? Description);

/// <summary>One manifest rule violation. <paramref name="Code"/> is the validator's ADnnn identifier.</summary>
internal sealed record ManifestIssue(string Code, string? Path, string Message);

/// <summary>The outcome of checking a manifest's own content, before any package file is touched.</summary>
internal sealed record ManifestParse(IReadOnlyList<ManifestDocument> Documents,
    IReadOnlyDictionary<string, JsonElement> PublisherMetadata, IReadOnlyList<ManifestIssue> Issues, bool UnsupportedSchema);

/// <summary>
/// The single implementation of the manifest contract. <see cref="GuidanceReader"/> uses the first issue to reject a
/// package; <see cref="GuidanceValidator"/> reports every issue to the author. Keeping them on one code path is what
/// stops the validator from approving a package the consumer-side reader rejects.
/// </summary>
internal static class ManifestCheck
{
    public const string ManifestPath = "guidance/reference-manifest.json";
    private const int MaxDescriptionScalars = 200;

    public static ManifestParse Parse(byte[] bytes)
    {
        var issues = new List<ManifestIssue>();
        var documents = new List<ManifestDocument>();
        var metadata = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        ManifestParse Done(bool unsupported = false) => new(documents, metadata, issues, unsupported);
        void Issue(string code, string? path, string message) => issues.Add(new ManifestIssue(code, path, message));

        try
        {
            using var parsed = JsonDocument.Parse(bytes.AsMemory(bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }) ? 3 : 0));
            var manifest = parsed.RootElement;
            if (!JsonElementRules.Object(manifest) || JsonElementRules.DuplicateProperties(manifest))
            {
                Issue("AD001", null, "manifest must be an object with unique properties.");
                return Done();
            }

            if (!manifest.TryGetProperty("schemaVersion", out var schema) || schema.ValueKind != JsonValueKind.Number ||
                !schema.TryGetInt64(out var versionNumber) || versionNumber < 1)
            {
                Issue("AD001", null, "schemaVersion must be an integer.");
                return Done();
            }

            if (versionNumber != 1)
            {
                Issue("AD001", null, $"unsupported schemaVersion {versionNumber}.");
                return Done(unsupported: true);
            }

            foreach (var property in manifest.EnumerateObject().Where(p => p.Name is not ("schemaVersion" or "documents" or "publisherMetadata")))
                Issue("AD001", null, $"unknown field '{property.Name}'.");
            if (!JsonElementRules.TryArray(manifest, "documents", out var rawDocuments))
            {
                Issue("AD001", null, "missing documents array.");
                return Done();
            }

            if (manifest.TryGetProperty("publisherMetadata", out var extensions))
            {
                if (!JsonElementRules.Object(extensions) || JsonElementRules.DuplicateProperties(extensions))
                {
                    Issue("AD001", null, "publisherMetadata must be an object with unique keys.");
                }
                else
                {
                    foreach (var extension in extensions.EnumerateObject())
                    {
                        if (Namespaced(extension.Name))
                            metadata[extension.Name] = extension.Value.Clone();
                        else
                            Issue("AD001", null, $"publisher metadata key '{extension.Name}' is not namespaced.");
                    }
                }
            }

            var prefixes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var declared = new HashSet<string>(StringComparer.Ordinal);
            var normalizedDocuments = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in rawDocuments.EnumerateArray())
            {
                if (!JsonElementRules.Object(raw) || JsonElementRules.DuplicateProperties(raw))
                {
                    Issue("AD001", null, "each document must be an object with unique properties.");
                    continue;
                }

                var ok = true;
                foreach (var property in raw.EnumerateObject().Where(p => p.Name is not ("path" or "sha256" or "usage" or "description")))
                {
                    Issue("AD001", null, $"unknown document field '{property.Name}'.");
                    ok = false;
                }

                if (!JsonElementRules.TryString(raw, "path", out var path) || !PackagePath.SafeRelative(path))
                {
                    Issue("AD002", path, "invalid document path: it must be a portable relative path without rooted, '..', device or trailing-dot segments.");
                    continue;
                }

                if (!JsonElementRules.TryString(raw, "sha256", out var hash) || hash.Length != 64 ||
                    !hash.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f')))
                {
                    Issue("AD003", path, $"invalid sha256 for '{path}': it must be 64 lowercase hexadecimal characters.");
                    ok = false;
                }

                GuidanceUsage usage = default;
                if (!JsonElementRules.TryString(raw, "usage", out var usageText) || !TryUsage(usageText, out usage))
                {
                    Issue("AD004", path, $"usage for '{path}' must be required, onDemand, or supporting.");
                    ok = false;
                }

                string? description = null;
                if (raw.TryGetProperty("description", out var descriptionValue))
                {
                    if (descriptionValue.ValueKind != JsonValueKind.String ||
                        !ValidDescription(descriptionValue.GetString()!, out description))
                    {
                        Issue("AD005", path,
                            $"invalid description for '{path}': one line of at most 200 characters without control or format characters.");
                        ok = false;
                    }
                }

                if (ok && usage != GuidanceUsage.Supporting && description is null)
                {
                    Issue("AD005", path, $"'{path}' is {usageText} and requires a description.");
                    ok = false;
                }

                var parts = PackagePath.Separators(path).Split('/');
                var alias = false;
                for (var i = 1; i <= parts.Length; i++)
                {
                    var original = string.Join('/', parts.Take(i));
                    var normalized = PackagePath.NormalizeIdentity(original);
                    if (prefixes.TryGetValue(normalized, out var previous) && previous != original)
                    {
                        Issue("AD007", path, $"portable directory or document alias: '{previous}' and '{original}'.");
                        alias = true;
                        break;
                    }

                    prefixes[normalized] = original;
                }

                if (!alias && (!declared.Add(path) || !normalizedDocuments.Add(PackagePath.NormalizeIdentity(PackagePath.Separators(path)))))
                {
                    Issue("AD007", path, $"portable document alias: '{path}'.");
                    alias = true;
                }

                if (ok && !alias)
                    documents.Add(new ManifestDocument(path, hash, usage, description));
            }

            foreach (var document in documents.Where(doc => prefixes.Keys.Any(prefix =>
                         prefix.StartsWith(PackagePath.NormalizeIdentity(PackagePath.Separators(doc.Path)) + "/", StringComparison.OrdinalIgnoreCase))))
                Issue("AD007", document.Path, "a document is also used as a directory prefix.");

            // Only meaningful when every entry parsed: a rejected entry could have been the required one.
            if (issues.Count == 0 && documents.Count > 0 && documents.All(doc => doc.Usage == GuidanceUsage.Supporting))
                Issue("AD006", null, "non-empty documents require at least one required or onDemand document.");

            return Done();
        }
        catch (JsonException e)
        {
            Issue("AD001", null, $"malformed manifest: {e.Message}");
            return Done();
        }
    }

    private static bool TryUsage(string text, out GuidanceUsage usage)
    {
        switch (text)
        {
            case "required": usage = GuidanceUsage.Required; return true;
            case "onDemand": usage = GuidanceUsage.OnDemand; return true;
            case "supporting": usage = GuidanceUsage.Supporting; return true;
            default: usage = default; return false;
        }
    }

    private static bool ValidDescription(string value, out string? description)
    {
        description = null;
        if (string.IsNullOrWhiteSpace(value))
            return false;
        string normalized;
        try { normalized = value.Trim().Normalize(NormalizationForm.FormC); }
        catch (ArgumentException) { return false; }
        var scalars = 0;
        foreach (var rune in normalized.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (category is System.Globalization.UnicodeCategory.Control or System.Globalization.UnicodeCategory.Format
                    or System.Globalization.UnicodeCategory.LineSeparator
                    or System.Globalization.UnicodeCategory.ParagraphSeparator ||
                ++scalars > MaxDescriptionScalars)
                return false;
        }

        description = normalized;
        return true;
    }

    private static bool Namespaced(string key) =>
        key.Length > 2 && key.Contains('.') &&
        key.Split('.').All(p => p.Length > 0 && p.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'));

}
