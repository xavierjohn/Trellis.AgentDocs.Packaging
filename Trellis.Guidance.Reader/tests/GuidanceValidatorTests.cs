namespace Trellis.Guidance.Reader.Tests;

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;

public sealed class GuidanceValidatorTests
{
    private const string Start = "guide/start.md";
    private const string Http = "guide/http.md";

    [Fact]
    public void A_well_formed_package_has_no_findings()
    {
        var package = new Package()
            .Doc(Start, "# Start\n\nSee [http](http.md#calling-the-api).\n", "required", "Read before using X.")
            .Doc(Http, "# Http\n\n## Calling the API\n\nBack to [start](start.md).\n", "supporting");

        var result = package.Validate();

        result.Diagnostics.Should().BeEmpty();
        result.DocumentCount.Should().Be(2);
        result.RequiredBytes.Should().Be(package.Files[Start].Length);
    }

    [Fact]
    public void A_missing_manifest_is_AD001()
    {
        var result = GuidanceValidator.Validate(new Dictionary<string, byte[]>());
        result.Diagnostics.Should().ContainSingle().Which.Code.Should().Be("AD001");
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("{\"schemaVersion\":2,\"documents\":[]}")]
    [InlineData("{\"schemaVersion\":1}")]
    [InlineData("{\"schemaVersion\":1,\"documents\":[],\"entryPoints\":[]}")]
    [InlineData("{\"schemaVersion\":1,\"documents\":[],\"publisherMetadata\":{\"lockstep\":true}}")]
    [InlineData("{\"schemaVersion\":1,\"schemaVersion\":1,\"documents\":[]}")]
    [InlineData("[]")]
    public void Malformed_manifests_are_AD001(string manifest)
    {
        var files = new Dictionary<string, byte[]> { ["guidance/reference-manifest.json"] = Encoding.UTF8.GetBytes(manifest) };
        GuidanceValidator.Validate(files).Diagnostics.Should().Contain(d => d.Code == "AD001" && d.Severity == GuidanceSeverity.Error);
    }

    [Fact]
    public void An_empty_manifest_is_valid()
    {
        new Package().Validate().Diagnostics.Should().BeEmpty();
    }

    [Fact]
    public void A_document_field_the_contract_does_not_know_is_AD001()
    {
        var package = new Package().Doc(Start, "# S\n", "required", "Read first.", extra: "\"role\":\"entry\"");
        package.Validate().Diagnostics.Should().Contain(d => d.Code == "AD001" && d.Message.Contains("role"));
    }

    [Theory]
    [InlineData("../escape.md")]
    [InlineData("/rooted.md")]
    [InlineData("guide/notes.txt")]
    [InlineData("guide/con.md")]
    [InlineData("guide/trailing.md.")]
    public void Unsafe_paths_are_AD002(string path)
    {
        var package = new Package().Doc(path, "# S\n", "required", "Read first.", pack: false);
        package.Validate().Diagnostics.Should().Contain(d => d.Code == "AD002");
    }

    [Fact]
    public void A_listed_document_that_is_not_packed_is_AD002()
    {
        var package = new Package().Doc(Start, "# S\n", "required", "Read first.", pack: false);
        package.Validate().Diagnostics.Should().Contain(d => d.Code == "AD002" && d.Path == Start);
    }

    [Fact]
    public void A_hash_that_does_not_match_the_packed_bytes_is_AD003()
    {
        var package = new Package().Doc(Start, "# S\n", "required", "Read first.");
        package.Files[Start] = Encoding.UTF8.GetBytes("# Changed after the manifest was written\n");
        package.Validate().Diagnostics.Should().Contain(d => d.Code == "AD003" && d.Message.Contains("does not match"));
    }

    [Theory]
    [InlineData("NOTHEX")]
    [InlineData("ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789")]
    [InlineData("")]
    public void A_malformed_hash_is_AD003(string sha)
    {
        var package = new Package().Doc(Start, "# S\n", "required", "Read first.", sha: sha);
        package.Validate().Diagnostics.Should().Contain(d => d.Code == "AD003");
    }

    [Fact]
    public void An_unknown_usage_is_AD004()
    {
        var package = new Package().Doc(Start, "# S\n", "sometimes", "Read first.");
        package.Validate().Diagnostics.Should().Contain(d => d.Code == "AD004");
    }

    [Theory]
    [InlineData("required")]
    [InlineData("onDemand")]
    public void A_listed_document_without_a_description_is_AD005(string usage)
    {
        var package = new Package().Doc(Start, "# S\n", usage);
        package.Validate().Diagnostics.Should().Contain(d => d.Code == "AD005" && d.Path == Start);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("two\nlines")]
    [InlineData("tab\there")]
    [InlineData("zero\u200bwidth")]
    [InlineData("line\u2028separator")]
    public void A_description_that_breaks_the_rules_is_AD005(string description)
    {
        var package = new Package().Doc(Start, "# S\n", "required", description);
        package.Validate().Diagnostics.Should().Contain(d => d.Code == "AD005");
    }

    [Fact]
    public void A_description_over_200_characters_is_AD005_but_exactly_200_is_fine()
    {
        new Package().Doc(Start, "# S\n", "required", new string('a', 201)).Validate().Diagnostics
            .Should().Contain(d => d.Code == "AD005");
        new Package().Doc(Start, "# S\n", "required", new string('a', 200)).Validate().Diagnostics.Should().BeEmpty();
    }

    [Fact]
    public void Descriptions_count_unicode_scalars_after_normalisation()
    {
        // 100 astral characters are 200 UTF-16 units but only 100 scalars.
        new Package().Doc(Start, "# S\n", "required", string.Concat(Enumerable.Repeat("\U0001F600", 100)))
            .Validate().Diagnostics.Should().BeEmpty();
    }

    [Fact]
    public void A_supporting_document_may_omit_its_description()
    {
        var package = new Package()
            .Doc(Start, "# S\n\n[h](http.md)\n", "required", "Read first.")
            .Doc(Http, "# H\n", "supporting");
        package.Validate().Diagnostics.Should().BeEmpty();
    }

    [Fact]
    public void A_manifest_with_only_supporting_documents_is_AD006()
    {
        var package = new Package().Doc(Http, "# H\n", "supporting");
        package.Validate().Diagnostics.Should().Contain(d => d.Code == "AD006");
    }

    [Fact]
    public void Duplicate_paths_are_AD007_including_after_case_folding()
    {
        var package = new Package()
            .Doc("guide/Start.md", "# S\n", "required", "Read first.")
            .Doc("guide/start.md", "# S\n", "onDemand", "Open when duplicated.");
        package.Validate().Diagnostics.Should().Contain(d => d.Code == "AD007");
    }

    [Fact]
    public void A_document_used_as_a_directory_prefix_is_AD007()
    {
        var package = new Package()
            .Doc("guide.md", "# S\n", "required", "Read first.")
            .Doc("guide.md/inner.md", "# I\n", "onDemand", "Open when nested.");
        package.Validate().Diagnostics.Should().Contain(d => d.Code == "AD007");
    }

    [Fact]
    public void Required_documents_over_the_threshold_warn_AD101()
    {
        var package = new Package().Doc(Start, "# S\n" + new string('x', 5000), "required", "Read first.");
        var options = new GuidanceValidationOptions { MaxRequiredBytes = 1000 };
        var result = package.Validate(options);
        result.Diagnostics.Should().ContainSingle(d => d.Code == "AD101").Which.Severity.Should().Be(GuidanceSeverity.Warning);
        result.HasErrors.Should().BeFalse();
        package.Validate().Diagnostics.Should().BeEmpty("the default threshold is 32 KiB");
    }

    [Fact]
    public void Only_required_documents_count_towards_the_size_threshold()
    {
        var package = new Package()
            .Doc(Start, "# S\n", "required", "Read first.")
            .Doc(Http, "# H\n" + new string('x', 60000), "onDemand", "Open when calling X.");
        package.Validate().Diagnostics.Should().BeEmpty();
    }

    [Fact]
    public void Broken_links_warn_AD102()
    {
        var package = new Package()
            .Doc(Start, "# S\n\n[missing](gone.md)\n[out](../../elsewhere.md)\n[heading](http.md#nope)\n[ok](http.md#h)\n", "required", "Read first.")
            .Doc(Http, "# H\n", "supporting");
        var findings = package.Validate().Diagnostics.Where(d => d.Code == "AD102").ToArray();
        findings.Should().HaveCount(3);
        findings.Select(d => d.Line).Should().BeEquivalentTo([3, 4, 5]);
    }

    [Fact]
    public void A_link_to_a_packed_but_unlisted_document_warns_AD102()
    {
        var package = new Package().Doc(Start, "# S\n\n[x](extra.md)\n", "required", "Read first.");
        package.Files["guide/extra.md"] = Encoding.UTF8.GetBytes("# Extra\n");
        package.Validate().Diagnostics.Should().Contain(d => d.Code == "AD102" && d.Message.Contains("not an installed guidance document"));
    }

    [Fact]
    public void External_links_are_ignored()
    {
        var package = new Package().Doc(Start,
            "# S\n\n[web](https://example.com/a.md) [mail](mailto:a@b.c) [proto](//cdn/x.md) [tel](tel:+15550100)\n",
            "required", "Read first.");
        package.Validate().Diagnostics.Should().BeEmpty();
    }

    [Fact]
    public void Links_inside_fenced_code_are_not_checked_and_long_fences_are_honoured()
    {
        var package = new Package().Doc(Start,
            "# S\n\n````md\n```\n[a](nope.md)\n```\n[b](nope-too.md)\n````\n\n~~~\n[c](nope-three.md)\n~~~\n", "required", "Read first.");
        package.Validate().Diagnostics.Should().BeEmpty();
    }

    [Fact]
    public void Reference_style_links_and_encoded_paths_are_checked()
    {
        var package = new Package().Doc(Start, "# S\n\n[x][ref]\n\n[ref]: missing%20doc.md\n", "required", "Read first.");
        package.Validate().Diagnostics.Should().Contain(d => d.Code == "AD102" && d.Line == 3);
    }

    [Fact]
    public void Anchors_follow_github_slugs_including_repeated_headings_and_punctuation()
    {
        var package = new Package()
            .Doc(Start, "# S\n\n[a](http.md#recipe-1--crud-aggregate) [b](http.md#same-1) [c](http.md#code-thing)\n", "required", "Read first.")
            .Doc(Http, "# H\n\n## Recipe 1 — CRUD aggregate\n\n## Same\n\n## Same\n\n## `Code` thing\n", "supporting");
        package.Validate().Diagnostics.Should().BeEmpty();
    }

    [Fact]
    public void A_supporting_document_nothing_links_to_warns_AD103()
    {
        var package = new Package()
            .Doc(Start, "# S\n", "required", "Read first.")
            .Doc(Http, "# H\n\n[self](#h)\n", "supporting");
        package.Validate().Diagnostics.Should().ContainSingle(d => d.Code == "AD103").Which.Path.Should().Be(Http);
    }

    [Fact]
    public void An_on_demand_document_needs_no_inbound_link()
    {
        var package = new Package()
            .Doc(Start, "# S\n", "required", "Read first.")
            .Doc(Http, "# H\n", "onDemand", "Open when calling X.");
        package.Validate().Diagnostics.Should().BeEmpty();
    }

    [Fact]
    public void Front_matter_with_a_misplaced_or_missing_closing_fence_warns_AD104()
    {
        // The real failure: the closing fence landed after the body, so prose sits inside the YAML block.
        var misplaced = new Package().Doc(Start, "---\ntitle: x\n\n# Heading inside\n\nYou are looking at the guide.\n---\n", "required", "Read first.");
        misplaced.Validate().Diagnostics.Should().ContainSingle(d => d.Code == "AD104").Which.Line.Should().Be(6);

        var unclosed = new Package().Doc(Start, "---\ntitle: x\nagent_usage: required\n", "required", "Read first.");
        unclosed.Validate().Diagnostics.Should().ContainSingle(d => d.Code == "AD104");

        var fine = new Package().Doc(Start, "---\ntitle: x\n---\n\n# Heading\n", "required", "Read first.");
        fine.Validate().Diagnostics.Should().BeEmpty();

        var crlf = new Package().Doc(Start, "﻿---\r\ntitle: x\r\n---\r\n\r\n# Heading\r\n", "required", "Read first.");
        crlf.Validate().Diagnostics.Should().BeEmpty();
    }

    [Fact]
    public void Yaml_comments_lists_and_indented_values_in_front_matter_are_not_reported()
    {
        var package = new Package().Doc(Start,
            "---\n# a YAML comment\ntitle: x\ntags:\n  - a\n  - b\nrelated: [a, b]\nquoted: \"a: b\"\n\n---\n\n# Heading\n",
            "required", "Read first.");
        package.Validate().Diagnostics.Should().BeEmpty();
    }

    [Fact]
    public void An_unlisted_markdown_file_beside_listed_guidance_warns_AD105_but_root_files_do_not()
    {
        var package = new Package().Doc(Start, "# S\n", "required", "Read first.");
        package.Files["guide/forgotten.md"] = Encoding.UTF8.GetBytes("# Forgotten\n");
        package.Files["README.md"] = Encoding.UTF8.GetBytes("# Package readme\n");
        package.Validate().Diagnostics.Should().ContainSingle(d => d.Code == "AD105").Which.Path.Should().Be("guide/forgotten.md");
    }

    [Fact]
    public void Findings_are_never_reported_for_documents_that_cannot_be_read()
    {
        var package = new Package().Doc(Start, "# S\n\n[x](gone.md)\n", "required", "Read first.", pack: false);
        package.Validate().Diagnostics.Should().OnlyContain(d => d.Code == "AD002");
    }

    [Fact]
    public void A_directory_and_a_nupkg_are_validated_the_same_way()
    {
        var package = new Package()
            .Doc(Start, "# S\n\n[h](http.md)\n", "required", "Read first.")
            .Doc(Http, "# H\n", "supporting");
        package.Files["guidance/reference-manifest.json"] = package.Manifest();
        var root = Path.Combine(Path.GetTempPath(), "agentdocs-validator-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var (path, bytes) in package.Files)
            {
                var target = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.WriteAllBytes(target, bytes);
            }

            var nupkg = Path.Combine(root, "sample.nupkg");
            using (var archive = ZipFile.Open(nupkg, ZipArchiveMode.Create))
            {
                foreach (var (path, bytes) in package.Files)
                {
                    using var stream = archive.CreateEntry(path).Open();
                    stream.Write(bytes);
                }
            }

            var fromNupkg = GuidanceValidator.ValidatePackage(nupkg);
            File.Delete(nupkg);
            var fromDirectory = GuidanceValidator.ValidatePackage(root);
            fromNupkg.Diagnostics.Should().BeEmpty();
            fromDirectory.Diagnostics.Should().BeEmpty();
            fromNupkg.DocumentCount.Should().Be(fromDirectory.DocumentCount);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void A_cycle_of_supporting_documents_that_no_discoverable_document_reaches_warns_AD103()
    {
        var package = new Package()
            .Doc(Start, "# S\n", "required", "Read first.")
            .Doc("guide/a.md", "# A\n\n[b](b.md)\n", "supporting")
            .Doc("guide/b.md", "# B\n\n[a](a.md)\n", "supporting");
        package.Validate().Diagnostics.Where(d => d.Code == "AD103").Select(d => d.Path)
            .Should().BeEquivalentTo("guide/a.md", "guide/b.md");
    }

    [Fact]
    public void A_supporting_document_reached_through_another_supporting_document_is_discoverable()
    {
        var package = new Package()
            .Doc(Start, "# S\n\n[a](a.md)\n", "required", "Read first.")
            .Doc("guide/a.md", "# A\n\n[b](b.md)\n", "supporting")
            .Doc("guide/b.md", "# B\n", "supporting");
        package.Validate().Diagnostics.Should().BeEmpty();
    }

    [Fact]
    public void A_document_that_is_not_valid_utf8_is_AD008()
    {
        var package = new Package().DocBytes(Start, [0x23, 0x20, 0xC3, 0x28, 0x0A], "required", "Read first.");
        package.Validate().Diagnostics.Should().ContainSingle(d => d.Code == "AD008").Which.Path.Should().Be(Start);
    }

    [Fact]
    public void Backslash_separated_manifest_paths_are_found_and_hash_checked()
    {
        var package = new Package().Doc("guide\\start.md", "# S\n", "required", "Read first.", pack: false);
        package.Files["guide/start.md"] = Encoding.UTF8.GetBytes("# S\n");
        package.Validate().Diagnostics.Should().BeEmpty();

        package.Files["guide/start.md"] = Encoding.UTF8.GetBytes("# Tampered\n");
        package.Validate().Diagnostics.Should().ContainSingle(d => d.Code == "AD003");
    }

    [Fact]
    public void A_supporting_description_that_is_not_a_string_is_AD005()
    {
        var package = new Package()
            .Doc(Start, "# S\n\n[h](http.md)\n", "required", "Read first.")
            .Doc(Http, "# H\n", "supporting", extra: "\"description\":42");
        package.Validate().Diagnostics.Should().Contain(d => d.Code == "AD005" && d.Path == Http);
    }

    [Fact]
    public void Directory_aliases_and_documents_reused_as_directories_are_AD007()
    {
        new Package()
            .Doc("Docs/one.md", "# One\n", "required", "Read first.")
            .Doc("docs/two.md", "# Two\n", "onDemand", "Open when two.")
            .Validate().Diagnostics.Should().Contain(d => d.Code == "AD007");
    }

    [Fact]
    public void Headings_inside_a_longer_fence_are_not_anchors_and_setext_headings_are()
    {
        var package = new Package()
            .Doc(Start, "# S\n\n[fake](http.md#fake-heading) [real](http.md#setext-title) [sub](http.md#second-level)\n", "required", "Read first.")
            .Doc(Http, "# H\n\n````md\n```\n## Fake heading\n```\n## Also fake\n````\n\nSetext title\n============\n\nSecond level\n------------\n", "supporting");
        package.Validate().Diagnostics.Where(d => d.Code == "AD102").Should().ContainSingle()
            .Which.Message.Should().Contain("fake-heading");
    }

    [Fact]
    public void Links_in_inline_code_are_ignored_and_parentheses_and_angle_brackets_are_understood()
    {
        var package = new Package()
            .Doc(Start, "# S\n\nUse `[x](missing.md)` syntax, see [h](http.md) and [p](paren_(1).md) and [a](<spaced name.md>).\n", "required", "Read first.")
            .Doc(Http, "# H\n", "supporting")
            .Doc("guide/paren_(1).md", "# P\n", "onDemand", "Open when parens.")
            .Doc("guide/spaced name.md", "# S\n", "onDemand", "Open when spaces.");
        package.Validate().Diagnostics.Should().BeEmpty();
    }

    [Fact]
    public void Zip_entries_are_addressed_by_their_percent_decoded_logical_path_as_NuGet_extracts_them()
    {
        // NuGet extracts the entry named guide/100%25.md as guide/100%.md, so that is the path a manifest must declare.
        var entries = new[] { ("guide/100%25.md", Encoding.UTF8.GetBytes("# S\n")) };

        var decoded = new Package().Doc("guide/100%.md", "# S\n", "required", "Read first.", pack: false);
        Nupkg(entries, decoded).Diagnostics.Should().BeEmpty();

        var literal = new Package().Doc("guide/100%25.md", "# S\n", "required", "Read first.", pack: false);
        Nupkg(entries, literal).Diagnostics.Should().Contain(d => d.Code == "AD002" && d.Message.Contains("not in the package"));
    }

    [Fact]
    public void Two_zip_entries_that_decode_to_the_same_path_are_an_error_rather_than_a_silent_choice()
    {
        var first = Encoding.UTF8.GetBytes("# First\n");
        var second = Encoding.UTF8.GetBytes("# Second\n");

        var encoded = new[] { ("guide/start%2Emd", first), ("guide/start.md", second) };
        Nupkg(encoded, new Package().DocBytes("guide/start.md", first, "required", "Read first.", pack: false))
            .Diagnostics.Should().Contain(d => d.Code == "AD009" && d.Path == "guide/start.md");

        var exact = new[] { ("guide/a.md", first), ("guide/a.md", second) };
        Nupkg(exact, new Package().DocBytes("guide/a.md", first, "required", "Read first.", pack: false))
            .Diagnostics.Should().Contain(d => d.Code == "AD009");

        var slash = new[] { ("guide%2Fb.md", first), ("guide/b.md", second) };
        Nupkg(slash, new Package().DocBytes("guide/b.md", first, "required", "Read first.", pack: false))
            .Diagnostics.Should().Contain(d => d.Code == "AD009");
    }

    [Fact]
    public void Links_to_files_that_are_not_installed_markdown_warn_AD102()
    {
        var package = new Package().Doc(Start,
            "# S\n\n![diagram](img/flow.png) [sample](../src/Example.cs) [root](/guide/http.md) [ok](http.md)\n",
            "required", "Read first.")
            .Doc(Http, "# H\n", "supporting");
        var messages = package.Validate().Diagnostics.Where(d => d.Code == "AD102").Select(d => d.Message).ToArray();
        messages.Should().HaveCount(3);
        messages.Should().Contain(m => m.Contains("flow.png") && m.Contains("not a Markdown document"));
        messages.Should().Contain(m => m.Contains("Example.cs"));
        messages.Should().Contain(m => m.Contains("root-relative"));
    }

    [Fact]
    public void Large_unrelated_entries_are_never_read_and_oversized_guidance_is_AD010()
    {
        // guide/huge.md is declared, so it is read and refused; CHANGELOG.md and the native library are not declared, so
        // they are never read however large they are.
        var package = new Package().Doc(Start, "# S\n", "required", "Read first.")
            .Doc("guide/huge.md", "x", "onDemand", "Open when huge.", sha: new string('0', 64), pack: false);
        package.Files["guidance/reference-manifest.json"] = package.Manifest();
        var nupkg = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".nupkg");
        try
        {
            using (var archive = ZipFile.Open(nupkg, ZipArchiveMode.Create))
            {
                foreach (var (name, content) in package.Files)
                {
                    using var entry = archive.CreateEntry(name).Open();
                    entry.Write(content);
                }

                using (var native = archive.CreateEntry("runtimes/native.dll", CompressionLevel.NoCompression).Open())
                    native.Write(new byte[20 * 1024 * 1024]);
                using (var huge = archive.CreateEntry("guide/huge.md").Open())
                    huge.Write(new byte[9 * 1024 * 1024]);
                using (var changelog = archive.CreateEntry("CHANGELOG.md").Open())
                    changelog.Write(new byte[9 * 1024 * 1024]);
            }

            var diagnostics = GuidanceValidator.ValidatePackage(nupkg).Diagnostics;
            diagnostics.Should().ContainSingle(d => d.Code == "AD010").Which.Path.Should().Be("guide/huge.md");
            diagnostics.Should().NotContain(d => d.Path == "runtimes/native.dll" || d.Path == "CHANGELOG.md");
        }
        finally
        {
            File.Delete(nupkg);
        }
    }

    [Fact]
    public void Archive_entries_that_extract_onto_each_other_are_AD009()
    {
        var bytes = Encoding.UTF8.GetBytes("# S\n");
        var manifest = new Package().DocBytes("guide/start.md", bytes, "required", "Read first.", pack: false);
        var cafeDecomposed = string.Concat("cafe", (char)0x301);
        var cafeComposed = string.Concat("caf", (char)0xE9);

        Nupkg([("guide/start.md", bytes), ("Guide/Start.md", bytes)], manifest).Diagnostics
            .Should().Contain(d => d.Code == "AD009", "a case alias overwrites the first document on Windows");
        Nupkg([("guide/a", bytes), ("guide/a/b.md", bytes)], manifest).Diagnostics
            .Should().Contain(d => d.Code == "AD009", "a file cannot also be a directory");
        Nupkg([("guide/a/b.md", bytes), ("guide/a", bytes)], manifest).Diagnostics
            .Should().Contain(d => d.Code == "AD009", "the order of the conflicting entries does not matter");
        Nupkg([(cafeDecomposed + "/x.md", bytes), (cafeComposed + "/x.md", bytes)], manifest).Diagnostics
            .Should().Contain(d => d.Code == "AD009", "Unicode-equivalent names are one path");
        Nupkg([("guide/start.md", bytes), ("guide/other.md", bytes), ("Guide/sub/deep.md", bytes)], manifest).Diagnostics
            .Should().NotContain(d => d.Code == "AD009", "different files in directories that differ only by case do not collide");
    }

    [Fact]
    public void Entries_whose_paths_resolve_onto_the_same_file_are_AD009_and_escapes_are_rejected()
    {
        var bytes = Encoding.UTF8.GetBytes("# S\n");
        var manifest = new Package().DocBytes("guide/start.md", bytes, "required", "Read first.", pack: false);

        foreach (var alias in new[] { "guide/x/../start.md", "guide/./start.md", "guide//start.md", "./guide/start.md" })
            Nupkg([("guide/start.md", bytes), (alias, bytes)], manifest).Diagnostics
                .Should().Contain(d => d.Code == "AD009", $"'{alias}' extracts onto guide/start.md");

        Nupkg([("guide/start.md", bytes), ("../outside.md", bytes)], manifest).Diagnostics
            .Should().Contain(d => d.Code == "AD009" && d.Message.Contains("escapes"));
        Nupkg([("guide/start.md", bytes), ("guide/../../outside.md", bytes)], manifest).Diagnostics
            .Should().Contain(d => d.Code == "AD009" && d.Message.Contains("escapes"));

        // A lone non-canonical spelling lands where the manifest expects, so it is not an error by itself.
        Nupkg([("guide/./start.md", bytes)], manifest).Diagnostics.Should().BeEmpty();
    }

    [Fact]
    public void Rooted_entries_and_windows_aliases_are_AD009()
    {
        var bytes = Encoding.UTF8.GetBytes("# S\n");
        var manifest = new Package().DocBytes("guide/start.md", bytes, "required", "Read first.", pack: false);

        foreach (var rooted in new[] { "/guide/start.md", "%2Fguide/start.md", "\\guide\\start.md", "C:/guide/start.md", "//server/share/start.md" })
            Nupkg([("guide/start.md", bytes), (rooted, bytes)], manifest).Diagnostics
                .Should().Contain(d => d.Code == "AD009", $"'{rooted}' is rooted");

        foreach (var alias in new[] { "guide/start.md.", "guide/start.md ", "guide./start.md", "guide/CON.md", "guide/a:b.md", "guide/what?.md" })
            Nupkg([("guide/start.md", bytes), (alias, bytes)], manifest).Diagnostics
                .Should().Contain(d => d.Code == "AD009" && d.Message.Contains("portably"), $"'{alias}' does not extract where it claims");

        Nupkg([("guide/start.md", bytes), ("lib/net8.0/My.Package.dll", bytes), ("[Content_Types].xml", bytes)], manifest).Diagnostics
            .Should().NotContain(d => d.Code == "AD009", "ordinary package entries are fine");
    }

    [Fact]
    public void Html_links_are_read_with_an_html_parser_not_pattern_matching()
    {
        var package = new Package().Doc(Start,
            "# S\n\n<img src=missing.png> <span data-href=\"not-a-link.md\" data-src='also-not.md'>x</span>\n\n" +
            "<div>\n<script>var a = '<img src=\"in-script.md\">';</script>\n<!-- <a href=\"in-comment.md\">c</a> -->\n" +
            "<a class=x href=unquoted.md>u</a>\n</div>\n",
            "required", "Read first.");
        var messages = package.Validate().Diagnostics.Where(d => d.Code == "AD102").Select(d => d.Message).ToArray();
        messages.Should().HaveCount(2);
        messages.Should().Contain(m => m.Contains("missing.png"));
        messages.Should().Contain(m => m.Contains("unquoted.md"));
    }

    [Fact]
    public void Identical_descriptions_on_required_documents_are_not_a_routing_ambiguity()
    {
        var package = new Package()
            .Doc(Start, "# S\n\n[b](b.md)\n", "required", "Read before using X.")
            .Doc("guide/b.md", "# B\n", "required", "Read before using X.");
        package.Validate().Diagnostics.Should().BeEmpty();
    }

    [Fact]
    public void Raw_html_links_are_checked_like_markdown_links()
    {
        var package = new Package().Doc(Start,
            "# S\n\nInline <img src=\"missing.png\"> and <a href='gone.md'>gone</a>.\n\n<div>\n  <a href=\"../up.md\">up</a>\n</div>\n\n<a href=\"https://example.com/a.md\">external</a>\n",
            "required", "Read first.");
        var findings = package.Validate().Diagnostics.Where(d => d.Code == "AD102").ToArray();
        findings.Should().HaveCount(3);
        findings.Select(d => d.Line).Should().BeEquivalentTo([3, 3, 6]);
    }

    [Fact]
    public void The_total_size_index_size_and_duplicate_description_budgets_warn()
    {
        var package = new Package()
            .Doc(Start, "# S\n", "required", "Read first.")
            .Doc("guide/a.md", "# A\n" + new string('x', 3000), "onDemand", "Open when using A.")
            .Doc("guide/b.md", "# B\n", "onDemand", "Open when using A.");
        var diagnostics = package.Validate(new GuidanceValidationOptions { MaxTotalGuidanceBytes = 1000, MaxIndexedDocuments = 2 }).Diagnostics;
        diagnostics.Should().Contain(d => d.Code == "AD106");
        diagnostics.Should().Contain(d => d.Code == "AD107");
        diagnostics.Should().ContainSingle(d => d.Code == "AD108").Which.Message.Should().Contain("guide/a.md").And.Contain("guide/b.md");
        package.Validate().Diagnostics.Where(d => d.Code is "AD106" or "AD107").Should().BeEmpty("the defaults are generous");
    }

    [Fact]
    public void Plain_yaml_keys_with_spaces_in_front_matter_are_not_reported()
    {
        var package = new Package().Doc(Start, "---\ndisplay name: Example\nlast verified: 2026-09-29\n---\n\n# Heading\n", "required", "Read first.");
        package.Validate().Diagnostics.Should().BeEmpty();
    }

    [Fact]
    public void Heading_anchors_come_from_the_rendered_text_not_the_markdown_source()
    {
        var package = new Package()
            .Doc(Start, "# S\n\n[a](http.md#hello) [b](http.md#emphasised-title) [c](http.md#with-code)\n", "required", "Read first.")
            .Doc(Http, "# H\n\n## [Hello](https://example.com)\n\n## _Emphasised_ **title**\n\n## With `code`\n", "supporting");
        package.Validate().Diagnostics.Should().BeEmpty();
    }

    private static GuidanceValidation Nupkg(IEnumerable<(string Name, byte[] Bytes)> entries, Package package)
    {
        var nupkg = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".nupkg");
        try
        {
            using (var archive = ZipFile.Open(nupkg, ZipArchiveMode.Create))
            {
                foreach (var (name, content) in entries.Append(("guidance/reference-manifest.json", package.Manifest())))
                {
                    using var entry = archive.CreateEntry(name).Open();
                    entry.Write(content);
                }
            }

            return GuidanceValidator.ValidatePackage(nupkg);
        }
        finally
        {
            File.Delete(nupkg);
        }
    }

    [Fact]
    public void Linked_documents_and_linked_manifests_in_a_directory_are_rejected_without_being_followed()
    {
        var root = Path.Combine(Path.GetTempPath(), "agentdocs-links-" + Guid.NewGuid().ToString("N"));
        var outside = Path.Combine(Path.GetTempPath(), "agentdocs-outside-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "guidance"));
            Directory.CreateDirectory(outside);
            File.WriteAllText(Path.Combine(outside, "start.md"), "# Outside\n");
            var package = new Package().Doc("guide/start.md", "# Outside\n", "required", "Read first.", pack: false);
            File.WriteAllBytes(Path.Combine(root, "guidance", "reference-manifest.json"), package.Manifest());
            Assert.SkipUnless(TryCreateDirectoryLink(Path.Combine(root, "guide"), outside), "Directory links are unavailable here.");

            var result = GuidanceValidator.ValidatePackage(root);

            result.Diagnostics.Should().Contain(d => d.Code == "AD002" && d.Message.Contains("link"));
        }
        finally
        {
            foreach (var link in new[] { Path.Combine(root, "guide") })
                if (Directory.Exists(link) && (File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0)
                    Directory.Delete(link);
            if (Directory.Exists(root))
                Directory.Delete(root, true);
            Directory.Delete(outside, true);
        }
    }

    [Fact]
    public void A_package_directory_under_a_linked_parent_is_judged_by_its_contents()
    {
        // The author's path is not the consumer's: their copy lives in the NuGet cache. (macOS /tmp is itself a link.)
        var real = Path.Combine(Path.GetTempPath(), "agentdocs-real-" + Guid.NewGuid().ToString("N"));
        var parent = Path.Combine(Path.GetTempPath(), "agentdocs-parent-" + Guid.NewGuid().ToString("N"));
        try
        {
            var package = new Package().Doc("guide/start.md", "# S\n", "required", "Read first.");
            Directory.CreateDirectory(Path.Combine(real, "pkg", "guide"));
            Directory.CreateDirectory(Path.Combine(real, "pkg", "guidance"));
            File.WriteAllBytes(Path.Combine(real, "pkg", "guide", "start.md"), package.Files["guide/start.md"]);
            File.WriteAllBytes(Path.Combine(real, "pkg", "guidance", "reference-manifest.json"), package.Manifest());
            Assert.SkipUnless(TryCreateDirectoryLink(parent, real), "Directory links are unavailable here.");

            GuidanceValidator.ValidatePackage(Path.Combine(parent, "pkg")).Diagnostics.Should().BeEmpty();
        }
        finally
        {
            if (Directory.Exists(parent) && (File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
                Directory.Delete(parent);
            Directory.Delete(real, true);
        }
    }

    private static bool TryCreateDirectoryLink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            try
            {
                using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe",
                    $"/c mklink /J \"{link}\" \"{target}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true });
                process?.WaitForExit();
                return Directory.Exists(link);
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    private sealed class Package
    {
        private readonly List<string> _documents = [];

        public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);

        public Package Doc(string path, string text, string usage, string? description = null,
            string? sha = null, string? extra = null, bool pack = true) =>
            DocBytes(path, Encoding.UTF8.GetBytes(text), usage, description, sha, extra, pack);

        public Package DocBytes(string path, byte[] bytes, string usage, string? description = null,
            string? sha = null, string? extra = null, bool pack = true)
        {
            if (pack)
                Files[path] = bytes;
            var hash = sha ?? Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var entry = new StringBuilder("{\"path\":").Append(JsonSerializer.Serialize(path))
                .Append(",\"sha256\":").Append(JsonSerializer.Serialize(hash))
                .Append(",\"usage\":").Append(JsonSerializer.Serialize(usage));
            if (description is not null)
                entry.Append(",\"description\":").Append(JsonSerializer.Serialize(description));
            if (extra is not null)
                entry.Append(',').Append(extra);
            _documents.Add(entry.Append('}').ToString());
            return this;
        }

        public byte[] Manifest() => Encoding.UTF8.GetBytes(
            "{\"schemaVersion\":1,\"documents\":[" + string.Join(',', _documents) + "]}");

        public GuidanceValidation Validate(GuidanceValidationOptions? options = null)
        {
            Files["guidance/reference-manifest.json"] = Manifest();
            return GuidanceValidator.Validate(Files, options);
        }
    }
}
