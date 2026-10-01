namespace Trellis.Guidance.Reader;

using System.Text.Json;

/// <summary>The result of inspecting one resolved NuGet package in one project/target scope.</summary>
public enum GuidanceStatus
{
    /// <summary>All declared guidance was verified.</summary>
    Valid,
    /// <summary>The package exists and does not declare guidance.</summary>
    NoManifest,
    /// <summary>The manifest declares a schema version the reader cannot interpret.</summary>
    UnsupportedSchema,
    /// <summary>The manifest, paths, links, or document hashes failed validation.</summary>
    InvalidManifest,
    /// <summary>A required restored package or declared document was not available.</summary>
    MissingAssets,
    /// <summary>The caller chose not to read this package's manifest; nothing about it was parsed or verified.</summary>
    NotLoaded
}

/// <summary>How the publisher intends a document to be used; the consumer decides whether to honor it.</summary>
public enum GuidanceUsage
{
    /// <summary>Recommended reading before writing or changing code that uses the package.</summary>
    Required,
    /// <summary>Independently discoverable by topic through its description.</summary>
    OnDemand,
    /// <summary>Not independently advertised; intended to be reached from another document.</summary>
    Supporting
}

/// <summary>The selected assets file, project, target framework, and runtime identifier.</summary>
public sealed record GuidanceScope(string AssetsPath, string ProjectPath, string TargetFramework, string? RuntimeIdentifier);

/// <summary>A portable logical identity; LocalPath is deliberately not part of this key.</summary>
public sealed record GuidanceIdentity(string PackageId, string Version, string PackagePath);

/// <summary>A validated document and its exact package-byte SHA-256. Description is present for Required and OnDemand documents.</summary>
public sealed record GuidanceDocument(GuidanceIdentity Identity, string LocalPath, string Sha256, GuidanceUsage Usage,
    string? Description = null);

/// <summary>One validated manifest.</summary>
public sealed record GuidanceContribution(IReadOnlyList<GuidanceDocument> Documents,
    IReadOnlyDictionary<string, JsonElement> PublisherMetadata);

/// <summary>A package's discovery outcome, retaining machine-local package provenance separately from logical identity.</summary>
public sealed record GuidancePackage(GuidanceScope Scope, string PackageId, string Version, string? PackageRoot,
    GuidanceStatus Status, GuidanceContribution? Contribution, string? Diagnostic)
{
    /// <summary>NuGet's graph-supplied package SHA-512 content hash, when present; not a document hash.</summary>
    public string? NuGetContentHash { get; init; }

    /// <summary>True when the package ships a guidance manifest, whether or not it was read.</summary>
    public bool ManifestDeclared { get; init; }
}

/// <summary>Graph-wide discovery; consumers must refuse strict operations unless IsSuccessful is true.</summary>
public sealed record GuidanceDiscovery(IReadOnlyList<GuidancePackage> Packages, IReadOnlyList<string> Diagnostics)
{
    /// <summary>True only if assets were readable and every resolved package was valid, had no manifest, or was deliberately not loaded.</summary>
    public bool IsSuccessful => Diagnostics.Count == 0 &&
        Packages.All(p => p.Status is GuidanceStatus.Valid or GuidanceStatus.NoManifest or GuidanceStatus.NotLoaded);
}

/// <summary>Reads only already-restored NuGet assets and verified package payloads; does not run MSBuild or write files.</summary>
public static class GuidanceReader
{
    /// <summary>Discovers every resolved package in each target of the selected assets files, without restore or repository writes.</summary>
    public static GuidanceDiscovery Discover(IEnumerable<string> assetsPaths) => Discover(assetsPaths, _ => true);

    /// <summary>
    /// Discovers every resolved package, reading a package's manifest only when <paramref name="shouldLoad"/>
    /// accepts its package ID. Other packages are reported as <see cref="GuidanceStatus.NotLoaded"/>.
    /// </summary>
    public static GuidanceDiscovery Discover(IEnumerable<string> assetsPaths, Func<string, bool> shouldLoad)
    {
        ArgumentNullException.ThrowIfNull(assetsPaths);
        ArgumentNullException.ThrowIfNull(shouldLoad);
        var packages = new List<GuidancePackage>();
        var diagnostics = new List<string>();
        var selected = false;
        foreach (var assetsPath in assetsPaths)
        {
            selected = true;
            try
            {
                var bytes = File.ReadAllBytes(assetsPath);
                using var assets = JsonDocument.Parse(bytes.AsMemory(
                    bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }) ? 3 : 0));
                NuGetAssetsReader.Read(Path.GetFullPath(assetsPath), assets.RootElement, packages, diagnostics, shouldLoad);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
            {
                diagnostics.Add($"Assets '{assetsPath}': {e.Message}");
            }
        }

        if (!selected)
            diagnostics.Add("No restored NuGet assets files were selected.");
        return new GuidanceDiscovery(packages, diagnostics);
    }
}
