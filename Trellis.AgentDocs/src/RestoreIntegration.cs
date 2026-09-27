namespace Trellis.AgentDocs;

using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

public static partial class AgentDocsCommand
{
    private const string HookStart = "<!-- agentdocs:restore:start -->";
    private const string HookEnd = "<!-- agentdocs:restore:end -->";

    private static string RestoreTarget() =>
        """
        <Project>
          <Target Name="AgentDocsRefreshAfterRestore" AfterTargets="Restore"
                  Condition="'$(AgentDocsSkipRefresh)' != 'true'">
            <Exec Command="dotnet tool run agentdocs refresh"
                  WorkingDirectory="$(MSBuildThisFileDirectory).." />
          </Target>
        </Project>

        """.Replace("\r\n", "\n");

    private static string[] RestoreHookFiles(string root, string[] projects)
    {
        var paths = new HashSet<string>(Physical) { Path.Combine(root, "Directory.Solution.targets") };
        foreach (var project in projects)
        {
            var directory = Path.GetDirectoryName(project)!;
            while (Within(root, directory) && !Physical.Equals(root, directory))
            {
                var nested = Path.Combine(directory, "Directory.Build.targets");
                if (File.Exists(nested))
                {
                    paths.Add(nested);
                    break;
                }

                directory = Path.GetDirectoryName(directory)!;
            }

            if (Physical.Equals(root, directory))
                paths.Add(Path.Combine(root, "Directory.Build.targets"));
        }

        return paths.OrderBy(path => path, StringComparer.Ordinal).ToArray();
    }

    private static string? OriginalProjectTag(string content)
    {
        var first = content.IndexOf(HookStart, StringComparison.Ordinal);
        var last = content.IndexOf(HookEnd, StringComparison.Ordinal);
        if (first >= 0 && last > first)
        {
            var marker = Regex.Match(content[first..last], @"<!-- agentdocs:original-project-tag:([0-9A-F]+) -->");
            if (marker.Success)
            {
                var hex = marker.Groups[1].Value;
                if (hex.Length % 2 != 0)
                    throw new InvalidOperationException("Invalid original Project tag in restore opt-in.");
                return Strict.GetString(Convert.FromHexString(hex));
            }
        }

        return SelfClosingRoot(content)?.Tag;
    }

    private static (string Tag, int Index)? SelfClosingRoot(string content)
    {
        XDocument document;
        try { document = XDocument.Parse(content, LoadOptions.SetLineInfo); }
        catch (XmlException) { return null; }
        if (document.Root?.Name.LocalName != "Project")
            return null;
        var location = (IXmlLineInfo)document.Root;
        var index = 0;
        for (var line = 1; line < location.LineNumber; line++)
        {
            var end = content.IndexOf('\n', index);
            if (end < 0)
                throw new InvalidOperationException("Invalid Project location.");
            index = end + 1;
        }
        index += location.LinePosition - 2;

        var quote = '\0';
        for (var end = index; end < content.Length; end++)
        {
            var current = content[end];
            if (quote != '\0')
            {
                if (current == quote)
                    quote = '\0';
            }
            else if (current is '"' or '\'')
                quote = current;
            else if (current == '>')
            {
                var tag = content[index..(end + 1)];
                return tag.EndsWith("/>", StringComparison.Ordinal) ? (tag, index) : null;
            }
        }

        throw new InvalidOperationException("Invalid Project opening tag.");
    }

    private static string ExpandedProjectTag(string tag, string newline)
    {
        var nameEnd = tag.IndexOfAny([' ', '\t', '\r', '\n', '/'], 1);
        var name = tag[1..nameEnd];
        return tag[..^2] + ">" + newline + "</" + name + ">";
    }

    private static string RestoreHook(string file, string root)
    {
        var target = Path.GetRelativePath(Path.GetDirectoryName(file)!, Path.Combine(root, ".agentdocs", "restore.targets"))
            .Replace('\\', '/');
        var originalTag = File.Exists(file) ? OriginalProjectTag(DecodeInstruction(File.ReadAllBytes(file))) : null;
        var marker = originalTag is null ? "" : "\n  <!-- agentdocs:original-project-tag:" +
            Convert.ToHexString(Encoding.UTF8.GetBytes(originalTag)) + " -->";
        return HookStart + marker + "\n  <Import Project=\"$(MSBuildThisFileDirectory)" + target + "\" />\n" + HookEnd;
    }

    private static string? RestoreSourceText(string root, string file)
    {
        if (Physical.Equals(file, Path.Combine(root, ".agentdocs", "restore.targets")))
            return null;
        var content = Canonical(File.ReadAllBytes(file));
        if (!content.Contains(HookStart, StringComparison.Ordinal))
            return content;
        var first = content.IndexOf(HookStart, StringComparison.Ordinal);
        var last = content.IndexOf(HookEnd, StringComparison.Ordinal);
        if (last < first)
            throw new InvalidOperationException($"Incomplete restore opt-in in {file}.");
        var block = content[first..(last + HookEnd.Length)];
        var original = MergeRestoreHook(content, file, root,
            new InstructionEntry(Rel(root, file), HashText(block), true), false, false);
        return original == "<Project>\n  <!-- agentdocs:generated -->\n</Project>\n" ? null : original;
    }

    private static void PlanRestoreHooks(string root, ContextState? old, ContextState? next,
        List<Change> changes, bool force)
    {
        foreach (var relative in (old?.RestoreEntries ?? []).Concat(next?.RestoreEntries ?? [])
            .Select(entry => entry.InstructionFile).Distinct(Portable).OrderBy(path => path, StringComparer.Ordinal))
        {
            var path = Full(root, relative);
            CheckFileDestination(root, root, path);
            var before = File.Exists(path) ? File.ReadAllBytes(path) : null;
            var text = before is null ? "" : DecodeInstruction(before);
            var previous = old?.RestoreEntries.SingleOrDefault(entry => Portable.Equals(entry.InstructionFile, relative));
            var desired = next?.RestoreEntries.Any(entry => Portable.Equals(entry.InstructionFile, relative)) == true;
            var updated = MergeRestoreHook(text, path, root, previous, desired, force);
            byte[]? after = updated is null ? null : before is null
                ? Bom.GetPreamble().Concat(Encoding.UTF8.GetBytes(updated)).ToArray()
                : EncodeInstruction(updated, before);
            Add(changes, path, before, after, desired ? "Update restore opt-in" : "Remove restore opt-in");
        }
    }

    private static string? MergeRestoreHook(string content, string file, string root,
        InstructionEntry? owned, bool include, bool force)
    {
        var first = content.IndexOf(HookStart, StringComparison.Ordinal);
        var last = content.IndexOf(HookEnd, StringComparison.Ordinal);
        if ((first < 0) != (last < 0) || first >= 0 &&
            (last < first || content.IndexOf(HookStart, first + HookStart.Length, StringComparison.Ordinal) >= 0 ||
             content.IndexOf(HookEnd, last + HookEnd.Length, StringComparison.Ordinal) >= 0))
            throw new InvalidOperationException($"Incomplete or duplicate restore marker in {file}.");

        if (first >= 0)
        {
            var block = content[first..(last + HookEnd.Length)];
            if (owned is null && !force)
                throw new InvalidOperationException($"Unowned restore opt-in in {file}; review --force.");
            if (owned is not null && HashText(block.Replace("\r\n", "\n")) != owned.CanonicalSha256 && !force)
                throw new InvalidOperationException($"Modified restore opt-in in {file}; review --force.");
            var start = first >= 2 && content[(first - 2)..first] == "  " ? first - 2 : first;
            var end = last + HookEnd.Length;
            if (end < content.Length && content[end] == '\r')
                end++;
            if (end < content.Length && content[end] == '\n')
                end++;
            content = content.Remove(start, end - start);
            if (OriginalProjectTag(block) is { } originalTag)
            {
                var originalNewline = content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
                var expanded = ExpandedProjectTag(originalTag, originalNewline);
                var expandedAt = content.IndexOf(expanded, StringComparison.Ordinal);
                if (expandedAt < 0)
                    throw new InvalidOperationException($"Modified self-closing Project in {file}; review --force.");
                content = content.Remove(expandedAt, expanded.Length).Insert(expandedAt, originalTag);
            }
        }
        else if (owned is not null && !force)
            throw new InvalidOperationException($"Owned restore opt-in missing from {file}; review --force.");

        if (!include)
            return owned?.ExistedBefore != true &&
                (content.Length == 0 ||
                 content.Replace("\r\n", "\n") == "<Project>\n  <!-- agentdocs:generated -->\n</Project>\n")
                ? null : content;

        if (content.Length == 0 && owned?.ExistedBefore != true)
            content = "<Project>\n  <!-- agentdocs:generated -->\n</Project>\n";
        XDocument project;
        try { project = XDocument.Parse(content, LoadOptions.PreserveWhitespace); }
        catch (XmlException e) { throw new InvalidOperationException($"Invalid MSBuild Project in {file}.", e); }
        if (project.Root?.Name.LocalName != "Project")
            throw new InvalidOperationException($"Expected an MSBuild Project in {file}.");
        var newline = content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var blockToInsert = "  " + RestoreHook(file, root).Replace("\n", newline) + newline;
        if (SelfClosingRoot(content) is { } selfClosing)
        {
            content = content.Remove(selfClosing.Index, selfClosing.Tag.Length)
                .Insert(selfClosing.Index, ExpandedProjectTag(selfClosing.Tag, newline));
        }
        foreach (Match closing in Regex.Matches(content, @"</Project\s*>", RegexOptions.IgnoreCase).Reverse())
        {
            var updated = content.Insert(closing.Index, blockToInsert);
            try
            {
                XDocument.Parse(updated, LoadOptions.PreserveWhitespace);
                return updated;
            }
            catch (XmlException) { }
        }

        throw new InvalidOperationException($"Cannot locate the closing Project tag in {file}.");
    }
}
