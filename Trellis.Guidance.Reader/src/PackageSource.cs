namespace Trellis.Guidance.Reader;

using System.IO.Compression;

/// <summary>Normalized package entries and bounded, on-demand file access for every validator input form.</summary>
internal sealed record PackageSource(
    IReadOnlySet<string> Names,
    Func<string, byte[]?> Read,
    IReadOnlySet<string>? LinkedPaths = null)
{
    private const long MaxManifestBytes = 1024 * 1024;
    private const long MaxDocumentBytes = 8 * 1024 * 1024;
    private const long MaxTotalReadBytes = 64 * 1024 * 1024;
    private const int MaxEntries = 100_000;

    public static PackageSource FromFiles(IReadOnlyDictionary<string, byte[]> files, List<GuidanceDiagnostic> problems)
    {
        var index = new EntryIndex(problems);
        var names = new HashSet<string>(StringComparer.Ordinal);
        var count = 0;
        foreach (var path in files.Keys)
        {
            if (++count > MaxEntries)
            {
                problems.Add(TooMany());
                break;
            }

            if (index.AddFile(path))
                names.Add(path);
        }

        return new PackageSource(names, BoundedReader(path =>
            files.TryGetValue(path, out var bytes) ? new Content(bytes.Length, () => new MemoryStream(bytes, false)) : null,
            problems));
    }

    public static PackageSource FromDirectory(string root, List<GuidanceDiagnostic> problems)
    {
        var index = new EntryIndex(problems);
        var names = new HashSet<string>(StringComparer.Ordinal);
        var linked = new HashSet<string>(StringComparer.Ordinal);
        var count = 0;
        var tooMany = false;

        void Walk(string directory, string prefix)
        {
            foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos().OrderBy(e => e.Name, StringComparer.Ordinal))
            {
                if (++count > MaxEntries)
                {
                    if (!tooMany)
                        problems.Add(TooMany());
                    tooMany = true;
                    return;
                }

                var relative = prefix.Length == 0 ? entry.Name : prefix + "/" + entry.Name;
                var directoryEntry = entry is DirectoryInfo;
                var accepted = directoryEntry ? index.AddDirectory(relative) : index.AddFile(relative);
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    linked.Add(relative);
                    if (!directoryEntry && accepted)
                        names.Add(relative);
                    continue;
                }

                if (directoryEntry)
                {
                    if (accepted)
                        Walk(entry.FullName, relative);
                    if (tooMany)
                        return;
                    continue;
                }

                if (accepted)
                    names.Add(relative);
            }
        }

        Walk(root, "");
        return new PackageSource(names, BoundedReader(path =>
        {
            var info = new FileInfo(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar)));
            return info.Exists ? new Content(info.Length, info.OpenRead) : null;
        }, problems), linked);
    }

    public static PackageSource FromArchive(ZipArchive archive, List<GuidanceDiagnostic> problems)
    {
        var index = new EntryIndex(problems);
        var names = new HashSet<string>(StringComparer.Ordinal);
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
        var count = 0;
        foreach (var entry in archive.Entries)
        {
            if (++count > MaxEntries)
            {
                problems.Add(TooMany());
                break;
            }

            // NuGet strips exactly one raw leading '/', then percent-decodes, and treats only '/' as a separator.
            // Backslashes therefore produce different paths across operating systems and are rejected as non-portable.
            var raw = entry.FullName.StartsWith('/') ? entry.FullName[1..] : entry.FullName;
            var decoded = PackagePath.Unescape(raw);
            var directory = entry.FullName.EndsWith('/');
            if (decoded.Contains('\\'))
            {
                problems.Add(new GuidanceDiagnostic("AD009", GuidanceSeverity.Error, decoded, null,
                    "This entry's path contains a backslash, which is a separator on Windows but an ordinary character on other systems, so it extracts to different paths."));
                continue;
            }

            if (IsRooted(decoded) || !PackagePath.TryCanonical(decoded, out var logical) ||
                (!directory && logical.Length == 0))
            {
                problems.Add(new GuidanceDiagnostic("AD009", GuidanceSeverity.Error, decoded, null,
                    "This entry's path is rooted, escapes the package root or is empty, so NuGet cannot extract it."));
                continue;
            }

            if (directory)
            {
                if (logical.Length > 0)
                    index.AddDirectory(logical);
                continue;
            }

            if (!index.AddFile(logical))
                continue;
            names.Add(logical);
            entries[logical] = entry;
        }

        return new PackageSource(names, BoundedReader(path =>
            entries.TryGetValue(path, out var entry) ? new Content(entry.Length, entry.Open) : null, problems));
    }

    private static Func<string, byte[]?> BoundedReader(Func<string, Content?> locate, List<GuidanceDiagnostic> problems)
    {
        var total = 0L;
        return path =>
        {
            if (locate(path) is not { } content)
                return null;
            var limit = path == ManifestCheck.ManifestPath ? MaxManifestBytes : MaxDocumentBytes;
            if (content.Length > limit || content.Length > MaxTotalReadBytes - total)
            {
                problems.Add(TooLarge(path));
                return null;
            }

            using var stream = content.Open();
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
            {
                buffer.Write(chunk, 0, read);
                if (buffer.Length > limit || buffer.Length > MaxTotalReadBytes - total)
                {
                    problems.Add(TooLarge(path));
                    return null;
                }
            }

            total += buffer.Length;
            return buffer.ToArray();
        };
    }

    private static bool IsRooted(string path) =>
        path.StartsWith('/') || (path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':');

    private static GuidanceDiagnostic TooLarge(string path) => new("AD010", GuidanceSeverity.Error, path, null,
        "The file exceeds the validator's resource limits (manifest 1 MiB, document 8 MiB, 64 MiB read in total) and was not read.");

    private static GuidanceDiagnostic TooMany() => new("AD010", GuidanceSeverity.Error, null, null,
        $"The package has more than {MaxEntries:N0} entries, which is over the validator's resource limit; the rest were not examined.");

    private sealed record Content(long Length, Func<Stream> Open);

    /// <summary>Tracks portable identities, including implicit and explicit directories.</summary>
    private sealed class EntryIndex(List<GuidanceDiagnostic> problems)
    {
        private readonly Dictionary<string, SeenEntry> _seen = new(StringComparer.OrdinalIgnoreCase);

        public bool AddFile(string logical) => Add(logical, isFile: true);

        public bool AddDirectory(string logical) => Add(logical, isFile: false);

        private bool Add(string logical, bool isFile)
        {
            var parts = logical.Split('/');
            if (parts.FirstOrDefault(segment => !PackagePath.PortableSegment(segment)) is { } unportable)
            {
                problems.Add(new GuidanceDiagnostic("AD009", GuidanceSeverity.Error, logical, null,
                    $"The segment '{unportable}' cannot be extracted portably: Windows drops trailing dots and spaces, so it lands on a different path, and rejects backslashes, device names and characters such as : * ? \" < > |."));
                return false;
            }

            for (var i = 1; i < parts.Length; i++)
            {
                var prefix = string.Join('/', parts.Take(i));
                var key = PackagePath.NormalizeIdentity(prefix);
                if (!_seen.TryGetValue(key, out var seen))
                    _seen[key] = new SeenEntry(prefix, false);
                else if (seen.IsFile)
                    return Collide(logical, seen.Path);
            }

            var full = PackagePath.NormalizeIdentity(logical);
            if (_seen.TryGetValue(full, out var existing))
            {
                if (!isFile && !existing.IsFile)
                    return true;
                return Collide(logical, existing.Path);
            }

            _seen[full] = new SeenEntry(logical, isFile);
            return true;
        }

        private bool Collide(string logical, string other)
        {
            problems.Add(new GuidanceDiagnostic("AD009", GuidanceSeverity.Error, logical, null,
                $"This entry collides with '{other}' when the package is extracted: the same path after decoding, case or Unicode normalisation, or a file that is also a directory. NuGet's extracted copy would be ambiguous."));
            return false;
        }

        private sealed record SeenEntry(string Path, bool IsFile);
    }
}
