namespace Trellis.Guidance.Reader;

using Markdig;
using Markdig.Extensions.AutoIdentifiers;
using Markdig.Extensions.Yaml;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

internal sealed record GuidanceText(string Path, GuidanceUsage Usage, string Text, string? Description, int Length);

/// <summary>Authoring-quality checks over verified guidance documents.</summary>
internal static class DocumentAnalyzer
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePreciseSourceLocation()
        .UseYamlFrontMatter()
        .UseAutoIdentifiers(AutoIdentifierOptions.GitHub)
        .Build();

    public static void Analyse(IReadOnlySet<string> names, List<GuidanceText> documents,
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
        foreach (var duplicate in indexed.Where(d => d.Usage == GuidanceUsage.OnDemand && d.Description is not null)
                     .GroupBy(d => d.Description!, StringComparer.Ordinal).Where(g => g.Count() > 1))
            warn("AD108", null,
                $"{string.Join(", ", duplicate.Select(d => d.Path))} share the description '{duplicate.Key}', so an agent cannot tell which to open.", null);

        var byPath = documents.ToDictionary(d => d.Path, StringComparer.Ordinal);
        var parsed = documents.ToDictionary(d => d.Path, d => Markdown.Parse(d.Text, Pipeline), StringComparer.Ordinal);
        var slugs = parsed.ToDictionary(pair => pair.Key, pair => Anchors(pair.Value), StringComparer.Ordinal);
        var edges = documents.ToDictionary(d => d.Path, _ => new HashSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);
        var htmlParser = new AngleSharp.Html.Parser.HtmlParser();

        foreach (var document in documents)
        {
            var path = document.Path;
            FrontMatter(path, document.Text, parsed[path], warn);
            foreach (var (target, line) in Links(parsed[path], htmlParser))
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

                    var combined = PackagePath.Resolve(path, file);
                    if (combined is null)
                    {
                        warn("AD102", path, $"Link '{target}' leaves the package, so it will not resolve once the guidance is installed.", line);
                        continue;
                    }

                    if (!combined.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                    {
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
                if (fragment.Length > 0 && !slugs[resolved].Contains(PackagePath.Unescape(fragment).ToLowerInvariant()))
                    warn("AD102", path, $"Link '{target}' points to a heading that does not exist in '{resolved}'.", line);
            }
        }

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
        var directories = documents.Select(d => PackagePath.Directory(d.Path)).Where(d => d.Length > 0).ToHashSet(StringComparer.Ordinal);
        foreach (var file in names.Where(f => f.EndsWith(".md", StringComparison.OrdinalIgnoreCase) &&
                     !listed.Contains(f) && directories.Contains(PackagePath.Directory(f))).Order(StringComparer.Ordinal))
            warn("AD105", file, "This Markdown file sits beside listed guidance but is not in the manifest, so it is not installed. List it or move it.", null);
    }

    private static void FrontMatter(string path, string text, MarkdownDocument document,
        Action<string, string?, string, int?> warn)
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

    private static IEnumerable<(string Target, int Line)> Links(
        MarkdownDocument document, AngleSharp.Html.Parser.HtmlParser htmlParser)
    {
        foreach (var link in document.Descendants<LinkInline>())
        {
            var target = Filter(link.Url);
            if (target is not null)
                yield return (target, link.Line + 1);
        }

        foreach (var html in document.Descendants<HtmlInline>())
            foreach (var target in HtmlTargets(html.Tag, htmlParser))
                if (Filter(target) is { } found)
                    yield return (found, html.Line + 1);
        foreach (var block in document.Descendants<HtmlBlock>())
        {
            var lines = Enumerable.Range(0, block.Lines.Count).Select(i => block.Lines.Lines[i].Slice.ToString()).ToArray();
            foreach (var target in HtmlTargets(string.Join("\n", lines), htmlParser))
                if (Filter(target) is { } found)
                {
                    var index = Array.FindIndex(lines, l => l.Contains(target, StringComparison.Ordinal));
                    yield return (found, block.Line + 1 + Math.Max(index, 0));
                }
        }

        static IEnumerable<string> HtmlTargets(string html, AngleSharp.Html.Parser.HtmlParser htmlParser)
        {
            foreach (var element in htmlParser.ParseDocument(html).All)
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

    private static HashSet<string> Anchors(MarkdownDocument document) =>
        document.Descendants<HeadingBlock>().Select(h => h.GetAttributes().Id).OfType<string>()
            .ToHashSet(StringComparer.Ordinal);
}
