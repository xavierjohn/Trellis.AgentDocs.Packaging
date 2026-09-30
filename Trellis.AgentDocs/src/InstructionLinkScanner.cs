namespace Trellis.AgentDocs;

using System.Text.RegularExpressions;

public static partial class AgentDocsCommand
{
    private static readonly Regex InstructionLinks = new(
        @"\]\((?<link>[^)\s]+)(?:\s+[^)]*)?\)|`(?<code>[^`]+)`", RegexOptions.Compiled);

    private static int WarnAboutMissingInstructionLinks(string root, List<Change> changes, TextWriter output)
    {
        var planned = changes.ToDictionary(change => change.Path, Physical);
        var instructions = new HashSet<string>(Physical);
        Visit(root);
        foreach (var change in changes.Where(change => IsInstructionFile(change.Path)))
            instructions.Add(change.Path);

        var warnings = 0;
        foreach (var instruction in instructions.OrderBy(path => path, StringComparer.Ordinal))
        {
            var bytes = planned.TryGetValue(instruction, out var change)
                ? change.After
                : File.Exists(instruction) ? File.ReadAllBytes(instruction) : null;
            if (bytes is null || (File.Exists(instruction) &&
                File.GetAttributes(instruction).HasFlag(FileAttributes.ReparsePoint)))
                continue;
            using var reader = new StringReader(DecodeInstruction(bytes));
            var lineNumber = 0;
            var fenceChar = '\0';
            var fenceLength = 0;
            while (reader.ReadLine() is { } line)
            {
                lineNumber++;
                var trimmed = line.TrimStart();
                var run = FenceRun(trimmed);
                if (run.Length >= 3)
                {
                    if (fenceChar == '\0')
                    {
                        fenceChar = run.Marker;
                        fenceLength = run.Length;
                    }
                    else if (run.Marker == fenceChar && run.Length >= fenceLength && run.Closing)
                    {
                        fenceChar = '\0';
                    }
                    continue;
                }
                if (fenceChar != '\0')
                    continue;

                foreach (Match match in InstructionLinks.Matches(line))
                {
                    var link = match.Groups["link"].Success
                        ? match.Groups["link"].Value : match.Groups["code"].Value;
                    if (!MissingLocalLink(root, Path.GetDirectoryName(instruction)!, link, planned))
                        continue;
                    output.WriteLine($"agentdocs warning: {Rel(root, instruction)}:{lineNumber}: " +
                        $"local link `{link}` does not exist.");
                    warnings++;
                }
            }
        }

        return warnings;

        void Visit(string directory)
        {
            foreach (var name in new[] { "AGENTS.md", "CLAUDE.md" })
            {
                var path = Path.Combine(directory, name);
                if (File.Exists(path))
                    instructions.Add(path);
            }
            if (Path.GetFileName(directory).Equals(".github", StringComparison.OrdinalIgnoreCase))
            {
                var path = Path.Combine(directory, "copilot-instructions.md");
                if (File.Exists(path))
                    instructions.Add(path);
            }
            string[] children;
            try
            {
                children = Directory.EnumerateDirectories(directory).OrderBy(path => path, StringComparer.Ordinal).ToArray();
            }
            catch (Exception e) when (e is UnauthorizedAccessException or IOException)
            {
                // The scan only produces warnings, so a directory we cannot list must not abort the command.
                return;
            }
            foreach (var child in children)
            {
                var name = Path.GetFileName(child);
                if (name.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals(".agentdocs", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("node_modules", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals(".vs", StringComparison.OrdinalIgnoreCase) ||
                    File.GetAttributes(child).HasFlag(FileAttributes.ReparsePoint) ||
                    File.Exists(Path.Combine(child, ".git")) || Directory.Exists(Path.Combine(child, ".git")))
                    continue;
                Visit(child);
            }
        }
    }

    /// <summary>
    /// Reads a Markdown fence marker: a run of three or more backticks or tildes. A run can close a fence
    /// only when nothing but whitespace follows it, and only when it is at least as long as the opener.
    /// </summary>
    private static (char Marker, int Length, bool Closing) FenceRun(string trimmed)
    {
        if (trimmed.Length < 3 || (trimmed[0] != '`' && trimmed[0] != '~'))
            return ('\0', 0, false);
        var marker = trimmed[0];
        var length = 0;
        while (length < trimmed.Length && trimmed[length] == marker)
            length++;
        return (marker, length, string.IsNullOrWhiteSpace(trimmed[length..]));
    }

    private static bool IsInstructionFile(string path)
    {
        var name = Path.GetFileName(path);
        return name.Equals("AGENTS.md", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("CLAUDE.md", StringComparison.OrdinalIgnoreCase) ||
            (name.Equals("copilot-instructions.md", StringComparison.OrdinalIgnoreCase) &&
             Path.GetFileName(Path.GetDirectoryName(path))?.Equals(".github", StringComparison.OrdinalIgnoreCase) == true);
    }

    private static bool MissingLocalLink(string root, string directory, string link,
        Dictionary<string, Change> planned)
    {
        var target = Uri.UnescapeDataString(link.Split('#', '?')[0]).Replace('\\', '/');
        if (!target.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ||
            target.IndexOfAny([':', '*', '<', '>', '|']) >= 0 || target.Contains("://", StringComparison.Ordinal) ||
            (target.Any(char.IsWhiteSpace) && !link.Contains('%')))
            return false;

        // A malformed hand-written link (for example a decoded NUL) is not a path we can judge; ignore it
        // rather than let path construction fail the whole command.
        string[] candidates;
        try
        {
            candidates = target.Contains('\0') ? []
                : target.StartsWith('/') ? [Path.GetFullPath(target.TrimStart('/'), root)]
                : [Path.GetFullPath(target, directory), Path.GetFullPath(target, root)];
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        var inside = candidates.Where(candidate => Within(root, candidate)).ToArray();
        return inside.Length != 0 && inside.All(candidate => planned.TryGetValue(candidate, out var change)
                ? change.After is null : !File.Exists(candidate));
    }
}
