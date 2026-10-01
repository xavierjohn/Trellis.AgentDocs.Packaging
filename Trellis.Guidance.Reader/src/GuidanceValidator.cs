namespace Trellis.Guidance.Reader;

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

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
/// Checks a package's guidance before publishing. Manifest errors come from the same <see cref="ManifestCheck"/>
/// implementation used by <see cref="GuidanceReader"/>, while document discoverability and authoring-quality checks
/// are warnings.
/// </summary>
/// <remarks>
/// A directory is judged by its contents: links inside it are errors, because consumers reject them. Where the
/// directory lives is not: a package directory under a linked parent is fine because consumers use their own copy.
/// </remarks>
public static class GuidanceValidator
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>Validates a <c>.nupkg</c> file or a directory holding an extracted package.</summary>
    public static GuidanceValidation ValidatePackage(string path, GuidanceValidationOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var problems = new List<GuidanceDiagnostic>();
        if (!Directory.Exists(path))
        {
            using var archive = ZipFile.OpenRead(path);
            return Validate(PackageSource.FromArchive(archive, problems), options, problems);
        }

        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            return new GuidanceValidation(
                [new GuidanceDiagnostic("AD001", GuidanceSeverity.Error, null, null,
                    "The package directory is itself a link; validate the real directory.")], 0, 0);
        return Validate(PackageSource.FromDirectory(Path.GetFullPath(path), problems), options, problems);
    }

    /// <summary>Validates package contents keyed by package-relative path with forward slashes.</summary>
    public static GuidanceValidation Validate(IReadOnlyDictionary<string, byte[]> files,
        GuidanceValidationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(files);
        var problems = new List<GuidanceDiagnostic>();
        return Validate(PackageSource.FromFiles(files, problems), options, problems);
    }

    private static GuidanceValidation Validate(PackageSource source,
        GuidanceValidationOptions? options, List<GuidanceDiagnostic> diagnostics)
    {
        options ??= new GuidanceValidationOptions();
        void Error(string code, string? path, string message, int? line = null) =>
            diagnostics.Add(new GuidanceDiagnostic(code, GuidanceSeverity.Error, path, line, message));
        void Warn(string code, string? path, string message, int? line = null) =>
            diagnostics.Add(new GuidanceDiagnostic(code, GuidanceSeverity.Warning, path, line, message));
        bool IsLinked(string path) =>
            source.LinkedPaths is { } linked && PackagePath.Chain(path).Any(linked.Contains);

        if (!source.Names.Contains(ManifestCheck.ManifestPath))
        {
            Error("AD001", ManifestCheck.ManifestPath, "The package has no guidance manifest.");
            return new GuidanceValidation(diagnostics, 0, 0);
        }

        if (IsLinked(ManifestCheck.ManifestPath))
        {
            Error("AD001", ManifestCheck.ManifestPath,
                "The manifest path contains a link/reparse point, which consumers reject.");
            return new GuidanceValidation(diagnostics, 0, 0);
        }

        if (source.Read(ManifestCheck.ManifestPath) is not { } manifestBytes)
            return new GuidanceValidation(diagnostics, 0, 0);
        var parse = ManifestCheck.Parse(manifestBytes);
        foreach (var issue in parse.Issues)
            Error(issue.Code, issue.Path ?? ManifestCheck.ManifestPath, issue.Message);

        var readable = new List<GuidanceText>();
        var requiredBytes = 0L;
        foreach (var document in parse.Documents)
        {
            var path = document.Path;
            var canonical = PackagePath.Separators(path);
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

            if (!source.Names.Contains(canonical))
            {
                Error("AD002", path, "The document is listed in the manifest but is not in the package.");
                continue;
            }

            if (source.Read(canonical) is not { } bytes)
                continue;
            if (document.Usage == GuidanceUsage.Required)
                requiredBytes += bytes.Length;
            if (!string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), document.Sha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                Error("AD003", path,
                    "sha256 does not match the packed bytes; regenerate the manifest after changing the document.");
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

            readable.Add(new GuidanceText(canonical, document.Usage, text, document.Description, bytes.Length));
        }

        DocumentAnalyzer.Analyse(source.Names, readable, requiredBytes, options, Warn);
        return new GuidanceValidation(diagnostics, parse.Documents.Count, requiredBytes);
    }

    private static string Decode(byte[] bytes) =>
        StrictUtf8.GetString(bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }) ? bytes[3..] : bytes);
}
