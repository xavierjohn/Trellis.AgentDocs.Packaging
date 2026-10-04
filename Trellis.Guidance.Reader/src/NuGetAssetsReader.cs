namespace Trellis.Guidance.Reader;

using System.Security.Cryptography;
using System.Text.Json;

/// <summary>Interprets one restored NuGet assets graph and verifies guidance in its package-cache entries.</summary>
internal static class NuGetAssetsReader
{
    public static void Read(string assetsPath, JsonElement root, List<GuidancePackage> packages,
        List<string> diagnostics, Func<string, bool> shouldLoad)
    {
        if (!JsonElementRules.Object(root) || !JsonElementRules.TryObject(root, "targets", out var targets) ||
            !JsonElementRules.TryObject(root, "libraries", out var libraries) ||
            !JsonElementRules.TryObject(root, "packageFolders", out var folders) ||
            !JsonElementRules.TryObject(root, "project", out var project) ||
            !JsonElementRules.TryObject(project, "restore", out var restore) ||
            !JsonElementRules.TryString(restore, "projectPath", out var projectPath) ||
            !Path.IsPathFullyQualified(projectPath) || !targets.EnumerateObject().Any() ||
            !folders.EnumerateObject().Any())
        {
            diagnostics.Add($"Assets '{assetsPath}': missing or invalid NuGet graph fields.");
            return;
        }

        var packageFolders = folders.EnumerateObject().Select(p => p.Name).ToArray();
        if (packageFolders.Any(p => !Path.IsPathFullyQualified(p)))
        {
            diagnostics.Add($"Assets '{assetsPath}': package folder is not absolute.");
            return;
        }

        foreach (var target in targets.EnumerateObject())
        {
            if (!JsonElementRules.Object(target.Value))
            {
                diagnostics.Add($"Assets '{assetsPath}': invalid target '{target.Name}'.");
                continue;
            }

            var slash = target.Name.IndexOf('/');
            var scope = new GuidanceScope(assetsPath, projectPath, slash < 0 ? target.Name : target.Name[..slash],
                slash < 0 ? null : target.Name[(slash + 1)..]);
            ReadTarget(scope, target.Value, libraries, packageFolders, packages, diagnostics, shouldLoad);
        }
    }

    private static void ReadTarget(GuidanceScope scope, JsonElement target, JsonElement libraries,
        string[] packageFolders, List<GuidancePackage> packages, List<string> diagnostics,
        Func<string, bool> shouldLoad)
    {
        foreach (var item in target.EnumerateObject())
        {
            if (!JsonElementRules.Object(item.Value) || !JsonElementRules.TryString(item.Value, "type", out var type))
            {
                diagnostics.Add($"Assets '{scope.AssetsPath}': invalid target library '{item.Name}'.");
                continue;
            }

            if (type != "package")
                continue;
            if (!libraries.TryGetProperty(item.Name, out var library) || !JsonElementRules.Object(library) ||
                !JsonElementRules.TryString(library, "type", out var libraryType) || libraryType != "package" ||
                !JsonElementRules.TryString(library, "path", out var relative) || !PackagePath.SafeRelative(relative))
            {
                packages.Add(new GuidancePackage(scope, item.Name, "", null, GuidanceStatus.MissingAssets,
                    null, $"Package '{item.Name}': invalid or missing resolved library path."));
                continue;
            }

            var index = item.Name.LastIndexOf('/');
            if (index <= 0 || index == item.Name.Length - 1)
            {
                diagnostics.Add($"Assets '{scope.AssetsPath}': invalid package identity '{item.Name}'.");
                continue;
            }

            var id = item.Name[..index];
            var version = item.Name[(index + 1)..];
            var relativeParts = PackagePath.Separators(relative).Split('/');
            var roots = packageFolders.Select(folder => Path.Combine(folder, Path.Combine(relativeParts))).ToArray();
            var packageRoot = roots.FirstOrDefault(Directory.Exists);
            var manifestListed = library.TryGetProperty("files", out var files) &&
                files.ValueKind == JsonValueKind.Array &&
                files.EnumerateArray().Any(file => file.ValueKind == JsonValueKind.String &&
                    string.Equals(PackagePath.Separators(file.GetString()!), ManifestCheck.ManifestPath,
                        StringComparison.OrdinalIgnoreCase));
            var contentHash = JsonElementRules.TryString(library, "sha512", out var hash) ? hash : null;
            if (!shouldLoad(id))
            {
                packages.Add(new GuidancePackage(scope, id, version, packageRoot ?? roots.FirstOrDefault(),
                    GuidanceStatus.NotLoaded, null, null)
                {
                    NuGetContentHash = contentHash,
                    ManifestDeclared = manifestListed || (packageRoot is not null &&
                        File.Exists(PackageFile(packageRoot, ManifestCheck.ManifestPath)))
                });
                continue;
            }

            if (packageRoot is null)
            {
                packages.Add(new GuidancePackage(scope, id, version, roots.FirstOrDefault(),
                    GuidanceStatus.MissingAssets, null, $"Package '{id}/{version}': package cache entry is missing."));
                continue;
            }

            if (library.TryGetProperty("files", out var declaredFiles) && declaredFiles.ValueKind == JsonValueKind.Array &&
                declaredFiles.EnumerateArray().Any(file => file.ValueKind == JsonValueKind.String &&
                    file.GetString() == ".nupkg.metadata") &&
                !File.Exists(Path.Combine(packageRoot, ".nupkg.metadata")))
            {
                packages.Add(new GuidancePackage(scope, id, version, packageRoot,
                    GuidanceStatus.MissingAssets, null, $"Package '{id}/{version}': NuGet package cache metadata is missing."));
                continue;
            }

            var read = ReadPackage(scope, id, version, packageRoot, manifestListed);
            packages.Add(read with
            {
                NuGetContentHash = contentHash,
                ManifestDeclared = read.Status != GuidanceStatus.NoManifest
            });
        }
    }

    private static GuidancePackage ReadPackage(GuidanceScope scope, string id, string version, string packageRoot,
        bool manifestListed)
    {
        GuidancePackage Outcome(GuidanceStatus status, string? diagnostic = null, GuidanceContribution? contribution = null)
            => new(scope, id, version, packageRoot, status, contribution,
                diagnostic is null ? null : $"Package '{id}/{version}': {diagnostic}");

        try
        {
            if (HasLink(packageRoot))
                return Outcome(GuidanceStatus.InvalidManifest, "package root contains a link/reparse point.");
            var manifestFile = PackageFile(packageRoot, ManifestCheck.ManifestPath);
            if (HasLink(manifestFile))
                return Outcome(GuidanceStatus.InvalidManifest, "manifest path contains a link/reparse point.");
            if (Directory.Exists(manifestFile))
                return Outcome(GuidanceStatus.InvalidManifest, "manifest path is a directory.");
            if (!File.Exists(manifestFile))
                return manifestListed
                    ? Outcome(GuidanceStatus.MissingAssets, "manifest listed by NuGet is missing from the package cache.")
                    : Outcome(GuidanceStatus.NoManifest);

            var parse = ManifestCheck.Parse(File.ReadAllBytes(manifestFile));
            if (parse.Issues.Count > 0)
                return Outcome(parse.UnsupportedSchema ? GuidanceStatus.UnsupportedSchema : GuidanceStatus.InvalidManifest,
                    parse.Issues[0].Message);
            if (parse.DocumentReferences.Any(reference =>
                    string.Equals(reference.PackageId, id, StringComparison.OrdinalIgnoreCase)))
                return Outcome(GuidanceStatus.InvalidManifest,
                    $"document reference cannot target its own package ID '{id}'.");

            var documents = new List<GuidanceDocument>();
            foreach (var document in parse.Documents)
            {
                var location = PackageFile(packageRoot, document.Path);
                if (HasLink(location))
                    return Outcome(GuidanceStatus.InvalidManifest,
                        $"document '{document.Path}' contains a link/reparse point.");
                if (!File.Exists(location))
                    return Outcome(GuidanceStatus.MissingAssets, $"document '{document.Path}' is missing.");
                using var stream = File.OpenRead(location);
                var actual = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
                if (!string.Equals(document.Sha256, actual, StringComparison.Ordinal))
                    return Outcome(GuidanceStatus.InvalidManifest, $"SHA-256 mismatch for '{document.Path}'.");
                documents.Add(new GuidanceDocument(new GuidanceIdentity(id, version, document.Path), location,
                    document.Sha256, document.Usage, document.Description));
            }

            return Outcome(GuidanceStatus.Valid,
                contribution: new GuidanceContribution(documents, parse.PublisherMetadata)
                {
                    DocumentReferences = parse.DocumentReferences
                        .Select(reference => new GuidanceDocumentReference(
                            reference.Path, reference.PackageId, reference.DocumentPath))
                        .ToArray()
                });
        }
        catch (JsonException e)
        {
            return Outcome(GuidanceStatus.InvalidManifest, $"malformed manifest: {e.Message}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Outcome(GuidanceStatus.MissingAssets, $"required package asset cannot be read: {e.Message}");
        }
    }

    private static string PackageFile(string root, string packagePath) =>
        Path.Combine(root, Path.Combine(PackagePath.Separators(packagePath).Split('/')));

    private static bool HasLink(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    return true;
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }

            var parent = Path.GetDirectoryName(current);
            if (parent is null || parent == current)
                break;
        }

        return false;
    }

}
