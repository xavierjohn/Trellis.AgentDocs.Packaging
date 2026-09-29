namespace Trellis.AgentDocs.Tests;

using System.Reflection;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Trellis.Guidance.Reader;
using Xunit;

public sealed class AgentDocsTests
{
    private static readonly string[] ExampleMetadata = ["Example.Library"];

    [Fact]
    public void ToolVersion_uses_embedded_NuGet_package_version()
    {
        var packageVersion = typeof(Program).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .SingleOrDefault(attribute => attribute.Key == "AgentDocsToolPackageVersion")?.Value;
        packageVersion.Should().NotBeNullOrWhiteSpace();
        var method = typeof(AgentDocsCommand).GetMethod("ToolVersion",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        ((string)method.Invoke(null, null)!).Should().Be(packageVersion);
    }

    [Fact]
    public void Init_creates_context_and_preserves_customer_bytes()
    {
        using var fixture = new Fixture();
        var original = "\uFEFF---\r\ntitle: App\r\n---\r\n# App\r\n\r\nCustomer instructions.\r\n";
        fixture.Write("AGENTS.md", original);
        var originalBytes = File.ReadAllBytes(fixture.Path("AGENTS.md"));
        fixture.Run("init", "App.csproj").Should().Be(0);
        File.ReadAllText(fixture.Path("AGENTS.md")).Should().Contain("Customer instructions.\r\n")
            .And.Contain("<!-- agentdocs:start -->");
        File.Exists(fixture.Path(".agentdocs", "README.md")).Should().BeTrue();
        File.Exists(fixture.Path(".agentdocs", "packages", "example.library", "guides", "overview.md")).Should().BeTrue();
        using (var state = JsonDocument.Parse(File.ReadAllText(fixture.Path(".agentdocs", "agent-context.json"))))
            state.RootElement.TryGetProperty("Scope", out _).Should().BeFalse();
        fixture.Run("check").Should().Be(0);
        fixture.Run("sync").Should().Be(0);
        fixture.Run("remove").Should().Be(0);
        File.ReadAllBytes(fixture.Path("AGENTS.md")).Should().Equal(originalBytes);
    }

    [Fact]
    public void Init_installs_root_guidance_and_preserves_Visual_Studio_instructions()
    {
        using var fixture = new Fixture();
        fixture.Write(".github/copilot-instructions.md", "# Customer\r\n");
        var original = File.ReadAllBytes(fixture.Path(".github", "copilot-instructions.md"));
        fixture.Run("init", "App.csproj").Should().Be(0);
        File.Exists(fixture.Path(".agentdocs", "packages", "example.library", "guides", "overview.md")).Should().BeTrue();
        File.ReadAllText(fixture.Path(".github", "copilot-instructions.md"))
            .Should().Contain("Customer\r\n").And.Contain(".agentdocs/README.md");
        fixture.Run("check").Should().Be(0);
        fixture.Run("remove").Should().Be(0);
        File.ReadAllBytes(fixture.Path(".github", "copilot-instructions.md")).Should().Equal(original);
    }

    [Fact]
    public void Init_from_nested_solution_requires_root_tool_and_creates_solution_pointer()
    {
        using var fixture = new Fixture();
        fixture.Scope("backend");
        File.Delete(fixture.Path("backend", ".config", "dotnet-tools.json"));
        fixture.Run("init", "App.csproj").Should().Be(0);
        File.ReadAllText(fixture.Path("backend", "AGENTS.md")).Should().Contain("../.agentdocs/README.md");
        File.Exists(fixture.Path(".agentdocs", "agent-context.json")).Should().BeTrue();
        fixture.Run("check").Should().Be(0);
        fixture.Run("remove").Should().Be(0);
        File.Exists(fixture.Path("backend", "AGENTS.md")).Should().BeFalse();
        File.Exists(fixture.Path(".github", "copilot-instructions.md")).Should().BeFalse();
        File.Exists(fixture.Path("AGENTS.md")).Should().BeFalse();
    }

    [Fact]
    public void Init_places_pointers_at_git_root_and_selected_solution_roots_only()
    {
        using var fixture = new Fixture();
        fixture.Scope("backend/Api/src");
        File.Delete(fixture.Path("backend", "Api", "src", ".config", "dotnet-tools.json"));
        fixture.EnterRoot();
        fixture.Write("backend/Domain/tests/Tests.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        fixture.Write("backend/Domain/tests/obj/project.assets.json",
            File.ReadAllText(fixture.Path("backend", "Api", "src", "obj", "project.assets.json")));
        fixture.Write("backend/Workspace.slnx", """
            <Solution>
              <Project Path="Api/src/App.csproj" />
              <Project Path="Domain/tests/Tests.csproj" />
            </Solution>
            """);

        fixture.Run("init", "backend/Workspace.slnx").Should().Be(0, fixture.LastOutput);
        File.ReadAllText(fixture.Path("AGENTS.md")).Should().Contain(".agentdocs/README.md");
        File.ReadAllText(fixture.Path("backend", "AGENTS.md")).Should().Contain("../.agentdocs/README.md");
        File.Exists(fixture.Path("backend", "Api", "src", "AGENTS.md")).Should().BeFalse();
        File.Exists(fixture.Path("backend", "Domain", "tests", "AGENTS.md")).Should().BeFalse();
        File.ReadAllText(fixture.Path(".github", "copilot-instructions.md"))
            .Should().Contain(".agentdocs/README.md");
        fixture.Run("check").Should().Be(0);
    }

    [Fact]
    public void Init_places_one_pointer_in_each_selected_entry_directory()
    {
        using var fixture = new Fixture();
        fixture.Write("backend/Api/App.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        fixture.Write("backend/Api/obj/project.assets.json",
            File.ReadAllText(fixture.Path("obj", "project.assets.json")));
        fixture.Write("backend/App.slnx", "<Solution><Project Path=\"Api/App.csproj\" /></Solution>");
        fixture.Write("tools/Tool.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        fixture.Write("tools/obj/project.assets.json",
            File.ReadAllText(fixture.Path("obj", "project.assets.json")));
        fixture.Write("tools/Tools.slnx", "<Solution><Project Path=\"Tool.csproj\" /></Solution>");

        fixture.Run("init", "App.csproj", "backend/App.slnx", "tools/Tools.slnx")
            .Should().Be(0, fixture.LastOutput);
        File.ReadAllText(fixture.Path("AGENTS.md")).Should().Contain(".agentdocs/README.md");
        File.ReadAllText(fixture.Path("backend", "AGENTS.md")).Should().Contain("../.agentdocs/README.md");
        File.ReadAllText(fixture.Path("tools", "AGENTS.md")).Should().Contain("../.agentdocs/README.md");
        File.Exists(fixture.Path("backend", "Api", "AGENTS.md")).Should().BeFalse();
        using var manifest = JsonDocument.Parse(File.ReadAllText(fixture.Path(".agentdocs", "agent-context.json")));
        manifest.RootElement.GetProperty("InstructionEntries").GetArrayLength().Should().Be(4);
        fixture.Run("check").Should().Be(0);
    }

    [Fact]
    public void Init_places_solution_pointer_even_when_projects_are_in_sibling_directories()
    {
        using var fixture = new Fixture();
        fixture.Scope("service");
        File.Delete(fixture.Path("service", ".config", "dotnet-tools.json"));
        fixture.EnterRoot();
        fixture.Write("backend/App.slnx", "<Solution><Project Path=\"../service/App.csproj\" /></Solution>");

        fixture.Run("init", "backend/App.slnx").Should().Be(0, fixture.LastOutput);
        File.ReadAllText(fixture.Path("backend", "AGENTS.md")).Should().Contain("../.agentdocs/README.md");
        File.Exists(fixture.Path("service", "AGENTS.md")).Should().BeFalse();
        fixture.Run("check").Should().Be(0);
    }

    [Fact]
    public void Init_uses_one_imperative_template_for_root_Copilot_and_nested_pointers()
    {
        using var fixture = new Fixture();
        fixture.Scope("backend");
        File.Delete(fixture.Path("backend", ".config", "dotnet-tools.json"));
        fixture.EnterRoot();

        fixture.Run("init", "backend/App.csproj").Should().Be(0, fixture.LastOutput);
        var rootPointer = Pointer(".agentdocs/README.md");
        var nestedPointer = Pointer("../.agentdocs/README.md");
        File.ReadAllText(fixture.Path("AGENTS.md")).Should().Be(rootPointer);
        File.ReadAllText(fixture.Path(".github", "copilot-instructions.md")).Should().Be(nestedPointer);
        File.ReadAllText(fixture.Path("backend", "AGENTS.md")).Should().Be(nestedPointer);
        fixture.Run("sync").Should().Be(0);
        File.ReadAllText(fixture.Path("backend", "AGENTS.md")).Should().Be(nestedPointer);
        fixture.Run("check").Should().Be(0);

        static string Pointer(string guide) => "<!-- agentdocs:start -->\n" +
            $"**Read `{guide}` now.** " +
            "The path is relative to this instruction file (repository-root path: `.agentdocs/README.md`).\n" +
            "<!-- agentdocs:end -->\n";
    }

    [Fact]
    public void Init_separates_managed_block_from_existing_text_and_restores_it_on_remove()
    {
        using var fixture = new Fixture();
        fixture.Write("AGENTS.md", "# Instructions\nCustomer text.\n");
        var original = File.ReadAllBytes(fixture.Path("AGENTS.md"));
        fixture.Run("init", "App.csproj").Should().Be(0);
        File.ReadAllText(fixture.Path("AGENTS.md")).Should().Be(
            "# Instructions\n<!-- agentdocs:start -->\n" +
            "**Read `.agentdocs/README.md` now.** " +
            "The path is relative to this instruction file (repository-root path: `.agentdocs/README.md`).\n" +
            "<!-- agentdocs:end -->\n\nCustomer text.\n");
        fixture.Run("remove").Should().Be(0);
        File.ReadAllBytes(fixture.Path("AGENTS.md")).Should().Equal(original);
    }

    [Fact]
    public void Sync_lists_guides_by_usage_and_warns_about_stale_instruction_links()
    {
        using var fixture = new Fixture { OverviewDescription = "Choose the right guide." };
        fixture.Scope("backend");
        File.Delete(fixture.Path("backend", ".config", "dotnet-tools.json"));
        fixture.EnterRoot();
        fixture.Write("AGENTS.md", "# Instructions\nRead `backend/.github/missing.md`.\n");
        fixture.Write("backend/CLAUDE.md", "# Instructions\nRead [old](../missing.md).\n");

        fixture.Run("init", "backend/App.csproj").Should().Be(0, fixture.LastOutput);
        var index = File.ReadAllText(fixture.Path(".agentdocs", "README.md"));
        index.Should().Contain("## Required reading by project")
            .And.Contain("`.agentdocs/packages/example.library/guides/start-here.md`")
            .And.Contain("## On-demand documents")
            .And.Contain("`Choose the right guide.`");
        fixture.Run("sync").Should().Be(0, fixture.LastOutput);
        fixture.LastOutput.Should().Contain("AGENTS.md:6").And.Contain("backend/.github/missing.md")
            .And.Contain("backend/CLAUDE.md:2").And.Contain("../missing.md");
        var rootPointer = File.ReadAllBytes(fixture.Path("AGENTS.md"));
        var copilotPointer = File.ReadAllBytes(fixture.Path(".github", "copilot-instructions.md"));
        var nestedPointer = File.ReadAllBytes(fixture.Path("backend", "AGENTS.md"));
        var indexBytes = File.ReadAllBytes(fixture.Path(".agentdocs", "README.md"));
        fixture.Run("sync").Should().Be(0, fixture.LastOutput);
        File.ReadAllBytes(fixture.Path("AGENTS.md")).Should().Equal(rootPointer);
        File.ReadAllBytes(fixture.Path(".github", "copilot-instructions.md")).Should().Equal(copilotPointer);
        File.ReadAllBytes(fixture.Path("backend", "AGENTS.md")).Should().Equal(nestedPointer);
        File.ReadAllBytes(fixture.Path(".agentdocs", "README.md")).Should().Equal(indexBytes);
        fixture.Run("sync", "--strict").Should().NotBe(0);
        fixture.LastOutput.Should().Contain("AGENTS.md:6");
    }

    [Fact]
    public void Init_lists_required_documents_per_group_and_on_demand_documents_separately()
    {
        using var fixture = new Fixture { OverviewDescription = "Choose the overview." };
        fixture.Run("init", "App.csproj").Should().Be(0, fixture.LastOutput);
        var index = File.ReadAllText(fixture.Path(".agentdocs", "README.md"));
        var required = index[index.IndexOf("Required documents:", StringComparison.Ordinal)..
            index.IndexOf("## On-demand documents", StringComparison.Ordinal)];
        required.Should().Contain("start-here.md").And.Contain("`Start here.`")
            .And.NotContain("overview.md").And.NotContain("details.md");
        var onDemand = index[index.IndexOf("## On-demand documents", StringComparison.Ordinal)..];
        onDemand.Should().Contain("overview.md").And.Contain("`Choose the overview.`")
            .And.NotContain("start-here.md").And.NotContain("details.md");
        fixture.Run("check").Should().Be(0);
    }

    [Fact]
    public void Sync_warns_when_it_removes_a_linked_owned_guide()
    {
        using var fixture = new Fixture();
        fixture.Write("AGENTS.md", "# Instructions\nSee [the guide](.agentdocs/packages/example.library/guides/overview.md).\n");
        fixture.Run("init", "App.csproj").Should().Be(0);
        fixture.DefaultHasManifest = false;

        fixture.Run("sync").Should().Be(0, fixture.LastOutput);
        fixture.LastOutput.Should().Contain("AGENTS.md:6")
            .And.Contain(".agentdocs/packages/example.library/guides/overview.md");
        File.Exists(fixture.Path(".agentdocs", "packages", "example.library", "guides", "overview.md"))
            .Should().BeFalse();
    }

    [Fact]
    public void Strict_init_rejects_missing_links_before_creating_owned_files()
    {
        using var fixture = new Fixture();
        fixture.Write("CLAUDE.md", "See [missing](missing.md).\n");
        fixture.Run("init", "--strict", "App.csproj").Should().NotBe(0);
        fixture.LastOutput.Should().Contain("CLAUDE.md:1").And.Contain("missing.md");
        File.Exists(fixture.Path(".agentdocs", "agent-context.json")).Should().BeFalse();
        File.Exists(fixture.Path("AGENTS.md")).Should().BeFalse();
    }

    [Fact]
    public void Strict_init_accepts_existing_relative_root_and_encoded_links()
    {
        using var fixture = new Fixture();
        fixture.Write("docs/local guide.md", "# Local\n");
        fixture.Write("nested/CLAUDE.md", """
            [relative](../docs/local%20guide.md)
            [root](docs/local%20guide.md)
            [remote](https://example.com/missing.md)
            ```md
            [example](missing.md)
            ```
            """);

        fixture.Run("init", "--strict", "App.csproj").Should().Be(0, fixture.LastOutput);
        fixture.LastOutput.Should().NotContain("agentdocs warning:");
    }

    [Fact]
    public void Strict_sync_rejects_links_to_guides_planned_for_removal()
    {
        using var fixture = new Fixture();
        fixture.Run("init", "App.csproj").Should().Be(0);
        var guide = fixture.Path(".agentdocs", "packages", "example.library", "guides", "overview.md");
        fixture.Write("CLAUDE.md", "Read [.NET guide](.agentdocs/packages/example.library/guides/overview.md).\n");
        fixture.DefaultHasManifest = false;
        fixture.DefaultPackageVersion = "2.0.0";

        fixture.Run("sync", "--strict").Should().NotBe(0);
        fixture.LastOutput.Should().Contain("CLAUDE.md:1").And.Contain("overview.md");
        File.Exists(guide).Should().BeTrue();
        fixture.Run("sync").Should().Be(0);
        File.Exists(guide).Should().BeFalse();
    }

    [Fact]
    public void Init_rejects_solution_in_nested_worktree_with_project_outside_it()
    {
        using var fixture = new Fixture();
        fixture.Write("nested/.git", "gitdir: elsewhere");
        fixture.Write("nested/App.slnx", "<Solution><Project Path=\"../App.csproj\" /></Solution>");

        fixture.Run("init", "nested/App.slnx").Should().NotBe(0);
        fixture.LastOutput.Should().Contain("Git working tree boundary");
        File.Exists(fixture.Path("nested", "AGENTS.md")).Should().BeFalse();
        File.Exists(fixture.Path(".agentdocs", "agent-context.json")).Should().BeFalse();
    }

    [Fact]
    public void Init_rejects_solution_in_nested_tool_scope_with_project_outside_it()
    {
        using var fixture = new Fixture();
        fixture.Write("nested/.config/dotnet-tools.json",
            File.ReadAllText(fixture.Path(".config", "dotnet-tools.json")));
        fixture.Write("nested/App.slnx", "<Solution><Project Path=\"../App.csproj\" /></Solution>");

        fixture.Run("init", "nested/App.slnx").Should().NotBe(0);
        fixture.LastOutput.Should().Contain("nested tool manifest");
        File.Exists(fixture.Path("nested", "AGENTS.md")).Should().BeFalse();
        File.Exists(fixture.Path(".agentdocs", "agent-context.json")).Should().BeFalse();
    }

    [Fact]
    public void Remove_preserves_existing_empty_Visual_Studio_instruction_file()
    {
        using var fixture = new Fixture();
        fixture.Write(".github/copilot-instructions.md", "");
        var original = File.ReadAllBytes(fixture.Path(".github", "copilot-instructions.md"));
        fixture.Run("init", "App.csproj").Should().Be(0);
        fixture.Run("remove").Should().Be(0);
        File.ReadAllBytes(fixture.Path(".github", "copilot-instructions.md")).Should().Equal(original);
    }

    [Fact]
    public void Check_and_remove_reject_modified_Visual_Studio_pointer_without_force()
    {
        using var fixture = new Fixture();
        fixture.Write(".github/copilot-instructions.md", "# Customer\n");
        fixture.Run("init", "App.csproj").Should().Be(0);
        var path = fixture.Path(".github", "copilot-instructions.md");
        var modified = File.ReadAllText(path).Replace("Read `../.agentdocs/README.md` now", "Skip package guidance");
        File.WriteAllText(path, modified, new UTF8Encoding(true));
        fixture.Run("check").Should().NotBe(0);
        fixture.Run("remove").Should().NotBe(0);
        File.ReadAllText(path).Should().Be(modified);
        fixture.Run("remove", "--force").Should().Be(0);
        File.ReadAllText(path).Should().Be("# Customer\n");
    }

    [Fact]
    public void Init_rejects_unowned_context_manifest_without_modifying_it()
    {
        using var fixture = new Fixture();
        fixture.Write(".agentdocs/agent-context.json", "unowned");
        fixture.Run("init", "App.csproj").Should().NotBe(0);
        File.ReadAllText(fixture.Path(".agentdocs", "agent-context.json")).Should().Be("unowned");
    }

    [Fact]
    public void Init_places_block_after_minimal_LF_frontmatter_without_heading()
    {
        using var fixture = new Fixture();
        fixture.Write("AGENTS.md", "---\n---\nCustomer instructions.\n");
        fixture.Run("init", "App.csproj").Should().Be(0);
        File.ReadAllText(fixture.Path("AGENTS.md"))
            .Should().StartWith("---\n---\n<!-- agentdocs:start -->");
        fixture.Run("remove").Should().Be(0);
        File.ReadAllText(fixture.Path("AGENTS.md")).Should().Be("---\n---\nCustomer instructions.\n");
    }

    [Fact]
    public void Init_ignores_headings_inside_fenced_code_blocks()
    {
        using var fixture = new Fixture();
        fixture.Write("AGENTS.md", "Intro\n```md\n# Example\n```\n# App\nCustomer instructions.\n");
        fixture.Run("init", "App.csproj").Should().Be(0);
        File.ReadAllText(fixture.Path("AGENTS.md"))
            .Should().Contain("```\n# App\n<!-- agentdocs:start -->");
    }

    [Fact]
    public void Sync_and_remove_require_force_when_entire_owned_block_is_missing()
    {
        using var fixture = new Fixture();
        fixture.Run("init", "App.csproj").Should().Be(0);
        fixture.Write("AGENTS.md", "# Customer\n");
        fixture.Run("sync").Should().NotBe(0);
        fixture.Run("remove").Should().NotBe(0);
        File.ReadAllText(fixture.Path("AGENTS.md")).Should().Be("# Customer\n");
        fixture.Run("sync", "--force").Should().Be(0);
        fixture.Run("check").Should().Be(0);
    }

    [Fact]
    public void Init_accepts_case_variant_of_existing_graph_entry_on_Windows()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var fixture = new Fixture();
        fixture.Run("init", "App.csproj").Should().Be(0);
        fixture.Run("init", "app.csproj").Should().Be(0, fixture.LastOutput);
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("NUL")]
    public void Init_rejects_reserved_Windows_package_path_components_before_writing(string id)
    {
        using var fixture = new Fixture();
        fixture.Write("package-two/guide.md", "# Guide\n");
        fixture.AddPackage(id, "package-two", "guide.md");
        fixture.Run("init", "App.csproj").Should().NotBe(0);
        File.Exists(fixture.Path("AGENTS.md")).Should().BeFalse();
        File.Exists(fixture.Path(".agentdocs", "agent-context.json")).Should().BeFalse();
    }

    [Fact]
    public void Directory_safety_rejects_linked_repository_root()
    {
        using var fixture = new Fixture();
        var linked = fixture.Path("linked");
        try
        {
            Directory.CreateSymbolicLink(linked, fixture.Path(".git"));
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return;
        }

        var method = typeof(AgentDocsCommand).GetMethod("EnsureDirectoriesSafe",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        Action check = () => method.Invoke(null, [linked, linked]);
        check.Should().Throw<TargetInvocationException>().WithInnerException<InvalidOperationException>();
    }

    [Fact]
    public void Directory_safety_rejects_dangling_directory_link()
    {
        using var fixture = new Fixture();
        var linked = fixture.Path("dangling");
        var missing = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "missing-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateSymbolicLink(linked, missing);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return;
        }

        var method = typeof(AgentDocsCommand).GetMethod("EnsureDirectoriesSafe",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        Action check = () => method.Invoke(null, [fixture.Path(), fixture.Path("dangling", "AGENTS.md")]);
        check.Should().Throw<TargetInvocationException>().WithInnerException<InvalidOperationException>();
    }

    [Fact]
    public void Init_rejects_another_tool_advertising_the_agentdocs_command()
    {
        using var fixture = new Fixture();
        fixture.Write(".config/dotnet-tools.json", JsonSerializer.Serialize(new
        {
            version = 1,
            isRoot = true,
            tools = new Dictionary<string, object>
            {
                ["other.tool"] = new { version = ToolVersion(), commands = new List<string> { "agentdocs" } }
            }
        }));

        fixture.Run("init", "App.csproj").Should().NotBe(0);
        File.Exists(fixture.Path(".agentdocs", "agent-context.json")).Should().BeFalse();
    }

    [Fact]
    public void Init_does_not_select_nested_manifest_with_a_different_agentdocs_command_owner()
    {
        using var fixture = new Fixture();
        fixture.Scope("service");
        fixture.Write("service/.config/dotnet-tools.json", JsonSerializer.Serialize(new
        {
            version = 1,
            isRoot = true,
            tools = new Dictionary<string, object>
            {
                ["other.tool"] = new { version = ToolVersion(), commands = new List<string> { "agentdocs" } }
            }
        }));

        fixture.Run("init", "App.csproj").Should().NotBe(0);
        File.Exists(fixture.Path("service", ".agentdocs", "agent-context.json")).Should().BeFalse();
    }

    [Fact]
    public void Child_process_drains_stdout_and_large_stderr_concurrently()
    {
        var start = new ProcessStartInfo("pwsh") { UseShellExecute = false };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add("[Console]::Error.Write('e' * 200000); [Console]::Out.Write('ok')");
        var method = typeof(AgentDocsCommand).GetMethod("RunProcess",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        var result = ((string Output, string Error, int ExitCode))method.Invoke(null, [start, 10000])!;
        result.Output.Should().Be("ok");
        result.Error.Should().HaveLength(200000);
        result.ExitCode.Should().Be(0);
    }

    [Fact]
    public void Timed_out_child_process_is_terminated()
    {
        using var fixture = new Fixture();
        var pidFile = fixture.Path("child.pid");
        var start = new ProcessStartInfo("pwsh") { UseShellExecute = false };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add($"[IO.File]::WriteAllText('{pidFile}', $PID.ToString()); Start-Sleep -Seconds 30");
        var method = typeof(AgentDocsCommand).GetMethod("RunProcess",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        Action run = () => method.Invoke(null, [start, 10000]);
        run.Should().Throw<TargetInvocationException>().WithInnerException<InvalidOperationException>()
            .WithMessage("*timed out*");
        var pid = int.Parse(File.ReadAllText(pidFile), System.Globalization.CultureInfo.InvariantCulture);
        Action inspect = () => Process.GetProcessById(pid);
        inspect.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Check_is_read_only_and_modified_content_requires_force()
    {
        using var fixture = new Fixture();
        fixture.Run("init", "App.csproj").Should().Be(0);
        var doc = fixture.Path(".agentdocs", "packages", "example.library", "guides", "overview.md");
        File.WriteAllText(doc, "changed");
        fixture.Run("check").Should().NotBe(0);
        fixture.Run("sync").Should().NotBe(0);
        File.ReadAllText(doc).Should().Be("changed");
        fixture.Run("sync", "--dry-run", "--force").Should().Be(0);
        File.ReadAllText(doc).Should().Be("changed");
        fixture.Run("sync", "--force").Should().Be(0);
        fixture.Run("check").Should().Be(0);
    }

    [Fact]
    public void Canonical_check_tolerates_checkout_newline_and_BOM_conversion()
    {
        using var fixture = new Fixture();
        fixture.Run("init", "App.csproj").Should().Be(0);
        var reference = fixture.Path(".agentdocs", "packages", "example.library", "guides", "overview.md");
        File.WriteAllText(reference, "# Guide\r\n", new UTF8Encoding(false));
        var converted = File.ReadAllBytes(reference);
        fixture.Run("check").Should().Be(0);
        fixture.Run("sync").Should().Be(0);
        File.ReadAllBytes(reference).Should().Equal(converted);
        fixture.Run("remove").Should().Be(0);
    }

    [Fact]
    public void Generated_files_are_line_feed_only_and_check_tolerates_a_windows_checkout()
    {
        using var fixture = new Fixture();
        fixture.Run("init", "App.csproj").Should().Be(0, fixture.LastOutput);
        var files = new[]
        {
            fixture.Path(".agentdocs", "README.md"),
            fixture.Path(".agentdocs", "agent-context.json"),
            fixture.Path(".agentdocs", "policy.json"),
            fixture.Path("AGENTS.md")
        };
        foreach (var file in files)
            File.ReadAllText(file).Should().NotContain("\r", "generated content is line-feed only: " + file);

        // A Windows checkout with autocrlf turns every text file into CRLF.
        foreach (var file in files)
            File.WriteAllText(file, File.ReadAllText(file).Replace("\n", "\r\n"), new UTF8Encoding(true));
        var converted = files.Select(File.ReadAllBytes).ToArray();
        fixture.Run("check").Should().Be(0, fixture.LastOutput);
        fixture.Run("sync").Should().Be(0, fixture.LastOutput);
        files.Select(File.ReadAllBytes).Should().BeEquivalentTo(converted, options => options.WithStrictOrdering());
    }

    [Fact]
    public void Description_line_separators_cannot_break_the_index_line()
    {
        using var fixture = new Fixture();
        fixture.Write("package-two/guide/README.md", "# Other\n");
        fixture.AddPackage("Other.Publisher", "package-two", "guide/README.md",
            description: "one\u2028two\u2029three\u0085four");
        fixture.Run("init", "App.csproj").Should().Be(0, fixture.LastOutput);
        var index = File.ReadAllText(fixture.Path(".agentdocs", "README.md"));
        index.Should().Contain("`one two three four`")
            .And.NotContain("\u2028").And.NotContain("\u2029").And.NotContain("\u0085");
    }

    [Fact]
    public void Init_does_not_claim_unowned_identical_document()
    {
        using var fixture = new Fixture();
        fixture.Write(".agentdocs/packages/example.library/guides/overview.md", "# Guide\n");
        fixture.Run("init", "App.csproj").Should().NotBe(0);
        File.Exists(fixture.Path(".agentdocs", "agent-context.json")).Should().BeFalse();
        File.Exists(fixture.Path("AGENTS.md")).Should().BeFalse();
    }

    [Fact]
    public void Invalid_graph_fails_before_writes()
    {
        using var fixture = new Fixture { ReaderDiagnostic = "Invalid restored graph." };
        fixture.Run("init", "App.csproj").Should().NotBe(0);
        fixture.LastOutput.Should().Contain("Invalid restored graph.");
        File.Exists(fixture.Path("AGENTS.md")).Should().BeFalse();
        fixture.Run("init", "--force", "App.csproj").Should().NotBe(0);
    }

    [Fact]
    public void Init_does_not_impose_publisher_specific_target_rules()
    {
        using var fixture = new Fixture();
        fixture.Write("package/build/Example.Library.targets", "<Target Name=\"PublisherTarget\" />");
        fixture.Run("init", "App.csproj").Should().Be(0);
    }

    [Fact]
    public void Init_allows_compatible_bootstrap_target()
    {
        using var fixture = new Fixture();
        fixture.Write("package/build/Example.Library.targets", "<Target Name=\"PublisherBootstrap\" />");
        fixture.Run("init", "App.csproj").Should().Be(0);
    }

    [Fact]
    public void Init_accepts_one_guide_as_its_only_entry_point()
    {
        using var fixture = new Fixture { ShortGuideOnly = true };
        fixture.Run("init", "App.csproj").Should().Be(0);
        File.ReadAllText(fixture.Path(".agentdocs", "README.md")).Should()
            .Contain("packages/example.library/guides/overview.md");
    }

    [Fact]
    public void Init_does_not_require_publisher_metadata()
    {
        using var fixture = new Fixture { OmitPublisherMetadata = true };
        fixture.Run("init", "App.csproj").Should().Be(0);
    }

    [Fact]
    public void Init_ignores_unrelated_packages_without_manifests()
    {
        using var fixture = new Fixture();
        fixture.AddWithoutManifest("Other.Dependency", "package-old");
        fixture.Run("init", "App.csproj").Should().Be(0);
    }

    [Fact]
    public void Sync_removes_guidance_when_a_package_stops_publishing_it()
    {
        using var fixture = new Fixture();
        fixture.Run("init", "App.csproj").Should().Be(0);
        fixture.DefaultHasManifest = false;
        fixture.DefaultPackageVersion = "2.0.0";
        fixture.Run("sync").Should().Be(0);
        fixture.LastOutput.Should().Contain("no longer publishes guidance");
        File.Exists(fixture.Path(".agentdocs", "packages", "example.library", "guides",
            "overview.md")).Should().BeFalse();
        fixture.Run("check").Should().Be(0);
    }

    [Fact]
    public void Init_skips_github_descendant_and_updates_existing_policy()
    {
        using var fixture = new Fixture();
        fixture.Write(".github/AGENTS.md", "untouched");
        File.WriteAllBytes(fixture.Path(".github", "legacy.md"), [0xFF, 0xFE, 0x00]);
        fixture.Write("src/Features/AGENTS.md", "# Feature\n");
        fixture.Run("init", "App.csproj").Should().Be(0);
        File.ReadAllText(fixture.Path(".github", "AGENTS.md")).Should().Be("untouched");
        File.ReadAllBytes(fixture.Path(".github", "legacy.md")).Should().Equal([0xFF, 0xFE, 0x00]);
        File.ReadAllText(fixture.Path("src", "Features", "AGENTS.md"))
            .Should().Be("# Feature\n");
        fixture.Run("remove").Should().Be(0);
        File.ReadAllText(fixture.Path("src", "Features", "AGENTS.md")).Should().Be("# Feature\n");
    }

    [Fact]
    public void Init_and_sync_skip_evaluated_generated_directories()
    {
        using var fixture = new Fixture();
        fixture.Write("App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <BaseOutputPath>artifacts/</BaseOutputPath>
                <BaseIntermediateOutputPath>scratch/</BaseIntermediateOutputPath>
                <EmitCompilerGeneratedFiles>true</EmitCompilerGeneratedFiles>
                <CompilerGeneratedFilesOutputPath>sourcegen/</CompilerGeneratedFilesOutputPath>
              </PropertyGroup>
            </Project>
            """);
        foreach (var directory in new[] { "artifacts", "scratch", "sourcegen" })
            fixture.Write(directory + "/AGENTS.md", "# Generated\n");
        fixture.Write("src/Features/AGENTS.md", "# Source\n");

        fixture.Run("init", "App.csproj").Should().Be(0);
        fixture.Run("sync").Should().Be(0);
        foreach (var directory in new[] { "artifacts", "scratch", "sourcegen" })
            File.ReadAllText(fixture.Path(directory, "AGENTS.md")).Should().Be("# Generated\n",
                "evaluated output directory {0} must be excluded", directory);
        File.ReadAllText(fixture.Path("src", "Features", "AGENTS.md"))
            .Should().Be("# Source\n");
        fixture.Run("check").Should().Be(0);
    }

    [Fact]
    public void Init_does_not_cross_nested_git_worktree_boundary()
    {
        using var fixture = new Fixture();
        fixture.Write("src/External/.git", "gitdir: elsewhere");
        fixture.Write("src/External/AGENTS.md", "# Nested\n");
        fixture.Run("init", "App.csproj").Should().Be(0);
        File.ReadAllText(fixture.Path("src", "External", "AGENTS.md")).Should().Be("# Nested\n");
    }

    [Fact]
    public void Sync_leaves_added_descendant_instructions_unmanaged()
    {
        using var fixture = new Fixture();
        fixture.Run("init", "App.csproj").Should().Be(0);
        fixture.Write("src/Features/AGENTS.md", "# Feature\n");
        fixture.Run("sync").Should().Be(0);
        File.ReadAllText(fixture.Path("src", "Features", "AGENTS.md")).Should().Be("# Feature\n");
        File.Delete(fixture.Path("src", "Features", "AGENTS.md"));
        fixture.Run("sync").Should().Be(0);
        using var manifest = JsonDocument.Parse(File.ReadAllText(fixture.Path(".agentdocs", "agent-context.json")));
        manifest.RootElement.GetProperty("InstructionEntries").GetArrayLength().Should().Be(2);
        fixture.Run("check").Should().Be(0);
    }

    [Fact]
    public void Init_explicit_source_root_covers_linked_source_outside_project_directory()
    {
        using var fixture = new Fixture();
        fixture.Scope("service");
        fixture.EnterRoot();
        File.Delete(fixture.Path("service", ".config", "dotnet-tools.json"));
        fixture.Write("shared/AGENTS.md", "# Shared\n");
        fixture.Write("shared/Linked.cs", "class Linked {}");
        fixture.Run("init", "--source-root", "shared", "service/App.csproj").Should().Be(0);
        File.ReadAllText(fixture.Path("shared", "AGENTS.md")).Should().Be("# Shared\n");
        fixture.Write("shared/Feature/AGENTS.md", "# Feature\n");
        fixture.Run("sync").Should().Be(0);
        File.ReadAllText(fixture.Path("shared", "Feature", "AGENTS.md")).Should().Be("# Feature\n");
        fixture.Run("check").Should().Be(0);
        fixture.Run("remove").Should().Be(0);
        File.ReadAllText(fixture.Path("shared", "AGENTS.md")).Should().Be("# Shared\n");
        File.ReadAllText(fixture.Path("shared", "Feature", "AGENTS.md")).Should().Be("# Feature\n");
    }

    [Fact]
    public void Init_rejects_explicit_source_root_outside_repository()
    {
        using var fixture = new Fixture();
        fixture.Scope("service");
        fixture.Write("shared/AGENTS.md", "# Shared\n");
        fixture.Run("init", "--source-root", "../../outside", "App.csproj").Should().NotBe(0);
        File.ReadAllText(fixture.Path("shared", "AGENTS.md")).Should().Be("# Shared\n");
        File.Exists(fixture.Path("service", ".agentdocs", "agent-context.json")).Should().BeFalse();
    }

    [Fact]
    public void Init_rejects_github_explicit_source_root_without_reading_its_policy()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(fixture.Path(".github"));
        File.WriteAllBytes(fixture.Path(".github", "AGENTS.md"), [0xFF, 0xFE]);
        fixture.Run("init", "--source-root", ".github", "App.csproj").Should().NotBe(0);
        File.ReadAllBytes(fixture.Path(".github", "AGENTS.md")).Should().Equal([0xFF, 0xFE]);
        File.Exists(fixture.Path(".agentdocs", "agent-context.json")).Should().BeFalse();
    }

    [Fact]
    public void Init_rejects_nested_git_explicit_source_root()
    {
        using var fixture = new Fixture();
        fixture.Write("shared/.git", "gitdir: elsewhere");
        fixture.Write("shared/AGENTS.md", "# Shared\n");
        fixture.Run("init", "--source-root", "shared", "App.csproj").Should().NotBe(0);
        File.ReadAllText(fixture.Path("shared", "AGENTS.md")).Should().Be("# Shared\n");
        File.Exists(fixture.Path(".agentdocs", "agent-context.json")).Should().BeFalse();
    }

    [Fact]
    public void Init_rejects_project_inside_nested_git_worktree()
    {
        using var fixture = new Fixture();
        fixture.Scope("external");
        fixture.EnterRoot();
        fixture.Write("external/.git", "gitdir: elsewhere");
        fixture.Run("init", "external/App.csproj").Should().NotBe(0);
        File.Exists(fixture.Path("AGENTS.md")).Should().BeFalse();
        File.Exists(fixture.Path(".agentdocs", "agent-context.json")).Should().BeFalse();
    }

    [Fact]
    public void Boundary_check_distinguishes_case_distinct_unix_trees()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var fixture = new Fixture();
        var root = fixture.Path();
        var sibling = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(root)!,
            System.IO.Path.GetFileName(root).ToUpperInvariant(), "AGENTS.md");
        var within = typeof(AgentDocsCommand).GetMethod("Within",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        ((bool)within.Invoke(null, [root, sibling])!).Should().BeFalse();
    }

    [Fact]
    public void Root_invocation_rejects_project_under_nested_tool_manifest()
    {
        using var fixture = new Fixture();
        fixture.Scope("service");
        fixture.EnterRoot();
        fixture.Run("init", "service/App.csproj").Should().NotBe(0);
        File.Exists(fixture.Path("AGENTS.md")).Should().BeFalse();
        File.Exists(fixture.Path(".agentdocs", "agent-context.json")).Should().BeFalse();
    }

    [Fact]
    public void Init_rejects_linked_explicit_source_root()
    {
        using var fixture = new Fixture();
        fixture.Write("elsewhere/AGENTS.md", "# Elsewhere\n");
        try
        {
            Directory.CreateSymbolicLink(fixture.Path("shared"), fixture.Path("elsewhere"));
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return;
        }

        fixture.Run("init", "--source-root", "shared", "App.csproj").Should().NotBe(0);
        File.ReadAllText(fixture.Path("elsewhere", "AGENTS.md")).Should().Be("# Elsewhere\n");
        File.Exists(fixture.Path(".agentdocs", "agent-context.json")).Should().BeFalse();
    }

    [Fact]
    public void Sync_rejects_source_root_option_outside_init()
    {
        using var fixture = new Fixture();
        fixture.Run("init", "App.csproj").Should().Be(0);
        fixture.Run("sync", "--source-root", "src").Should().NotBe(0);
        fixture.Run("check").Should().Be(0);
    }

    [Fact]
    public void Init_reports_missing_source_root_argument_without_throwing()
    {
        using var fixture = new Fixture();
        fixture.Run("init", "App.csproj", "--source-root").Should().NotBe(0);
        File.Exists(fixture.Path(".agentdocs", "agent-context.json")).Should().BeFalse();
    }

    [Fact]
    public void Check_rejects_modified_managed_block_but_force_resolves_it()
    {
        using var fixture = new Fixture();
        fixture.Run("init", "App.csproj").Should().Be(0);
        var file = fixture.Path("AGENTS.md");
        var text = File.ReadAllText(file);
        var end = text.IndexOf("<!-- agentdocs:end -->", StringComparison.Ordinal);
        File.WriteAllText(file, text.Insert(end, "tampered line\n"), new UTF8Encoding(true));
        fixture.Run("check").Should().NotBe(0);
        fixture.Run("sync", "--force").Should().Be(0);
        File.ReadAllText(file).Should().NotContain("tampered line");
    }

    [Fact]
    public void Init_rejects_linked_context_before_following_repository_manifest()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(fixture.Path("elsewhere"));
        fixture.Write("elsewhere/agent-context.json", "not a context");
        try
        {
            Directory.CreateSymbolicLink(fixture.Path(".agentdocs"), fixture.Path("elsewhere"));
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return;
        }

        fixture.Run("init", "App.csproj").Should().NotBe(0);
        File.Exists(fixture.Path("AGENTS.md")).Should().BeFalse();
    }

    [Fact]
    public void Remove_cannot_follow_forged_instruction_path_outside_recorded_source()
    {
        using var fixture = new Fixture();
        fixture.Scope("service");
        File.Delete(fixture.Path("service", ".config", "dotnet-tools.json"));
        fixture.EnterRoot();
        fixture.Run("init", "service/App.csproj").Should().Be(0);
        fixture.Write("private/AGENTS.md", "# Private\n");
        var manifestPath = fixture.Path(".agentdocs", "agent-context.json");
        var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(manifestPath))!;
        node["InstructionEntries"]![0]!["InstructionFile"] = "private/AGENTS.md";
        File.WriteAllText(manifestPath, node.ToJsonString());
        fixture.Run("remove", "--force").Should().NotBe(0);
        File.ReadAllText(fixture.Path("private", "AGENTS.md")).Should().Be("# Private\n");
        File.Exists(fixture.Path(".agentdocs", "agent-context.json")).Should().BeTrue();
    }

    [Theory]
    [InlineData("agent-context.json")]
    [InlineData("customer.md")]
    public void Remove_refuses_forged_reference_ownership_even_with_force(string forgedPath)
    {
        using var fixture = new Fixture();
        fixture.Run("init", "App.csproj").Should().Be(0);
        fixture.Write(".agentdocs/customer.md", "# Customer\n");
        var manifestPath = fixture.Path(".agentdocs", "agent-context.json");
        var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(manifestPath))!;
        var forged = node["References"]![0]!.DeepClone();
        forged["Path"] = forgedPath;
        node["References"]!.AsArray().Add(forged);
        File.WriteAllText(manifestPath, node.ToJsonString());
        var manifestBytes = File.ReadAllBytes(manifestPath);
        var originalInstructions = File.ReadAllBytes(fixture.Path("AGENTS.md"));
        fixture.RunWithoutReader("remove", "--force").Should().NotBe(0);
        File.ReadAllBytes(manifestPath).Should().Equal(manifestBytes);
        File.ReadAllBytes(fixture.Path("AGENTS.md")).Should().Equal(originalInstructions);
        File.ReadAllText(fixture.Path(".agentdocs", "customer.md")).Should().Be("# Customer\n");
    }

    [Fact]
    public void Atomic_preflight_includes_each_existing_destination_parent()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(fixture.Path(".agentdocs", "packages", "example.library", "guides"));
        var probeDirectories = typeof(AgentDocsCommand).GetMethod("AtomicProbeDirectories",
            BindingFlags.Static | BindingFlags.NonPublic);
        probeDirectories.Should().NotBeNull();
        var destinations = new[] { fixture.Path("AGENTS.md"),
            fixture.Path(".agentdocs", "agent-context.json"),
            fixture.Path(".agentdocs", "packages", "example.library", "guides", "core.md"),
            fixture.Path(".agentdocs", "packages", "other", "guide.md") };
        var directories = ((IEnumerable<string>)probeDirectories!.Invoke(null, [destinations])!).ToArray();
        directories.Should().BeEquivalentTo([
            fixture.Path(), fixture.Path(".agentdocs"), fixture.Path(".agentdocs", "packages"),
            fixture.Path(".agentdocs", "packages", "example.library", "guides")]);
    }

    [Fact]
    public void Init_namespaces_identical_document_paths_by_package()
    {
        using var fixture = new Fixture();
        fixture.Write("package-two/guides/overview.md", "\uFEFF# Guide\r\n");
        fixture.AddPackage("Other.Publisher", "package-two", "guides/overview.md");
        fixture.Run("init", "App.csproj").Should().Be(0);
        using var manifest = JsonDocument.Parse(File.ReadAllText(fixture.Path(".agentdocs", "agent-context.json")));
        var references = manifest.RootElement.GetProperty("References").EnumerateArray().ToArray();
        references.Should().HaveCount(4);
        references.Select(r => r.GetProperty("Path").GetString()).Should()
            .Contain(["packages/example.library/guides/overview.md",
                "packages/other.publisher/guides/overview.md"]);
        fixture.Run("check").Should().Be(0);
    }

    [Fact]
    public void Init_unrelated_guidance_retains_nested_namespace()
    {
        using var fixture = new Fixture();
        fixture.Write("package-two/guide/README.md", "# Other\n");
        fixture.AddPackage("Other.Publisher", "package-two", "guide/README.md");
        fixture.Run("init", "App.csproj").Should().Be(0);
        File.Exists(fixture.Path(".agentdocs", "packages", "other.publisher", "guide", "README.md"))
            .Should().BeTrue();
        File.ReadAllText(fixture.Path(".agentdocs", "README.md")).Should().Contain("## On-demand documents")
            .And.Contain("packages/other.publisher/guide/README.md");
        fixture.Run("check").Should().Be(0);
    }

    [Fact]
    public void Generated_index_escapes_package_supplied_markdown_label_characters()
    {
        using var fixture = new Fixture();
        fixture.Write("package-two/guide/R]eadme.md", "# R]eadme\n");
        fixture.AddPackage("Other.Publisher", "package-two", "guide/R]eadme.md");
        fixture.Run("init", "App.csproj").Should().Be(0);
        File.ReadAllText(fixture.Path(".agentdocs", "README.md")).Should().Contain("guide/R]eadme.md`](")
            .And.Contain("guide/R%5Deadme.md");
    }

    [Fact]
    public void Init_rejects_mixed_versions_of_one_package()
    {
        using var fixture = new Fixture();
        fixture.Write("package-two/guide/README.md", "# Other\n");
        fixture.Write("package-three/guide/README.md", "# Other\n");
        fixture.AddPackage("Other.Publisher", "package-two", "guide/README.md");
        fixture.AddPackage("Other.Publisher", "package-three", "guide/README.md", "3.0.0");
        fixture.Run("init", "App.csproj").Should().NotBe(0);
        fixture.LastOutput.Should().Contain("Mixed versions of Other.Publisher");
        File.Exists(fixture.Path("AGENTS.md")).Should().BeFalse();
    }

    [Fact]
    public void Init_allows_mixed_versions_of_packages_without_guidance()
    {
        using var fixture = new Fixture();
        fixture.Write("Other/Other.csproj", File.ReadAllText(fixture.Path("App.csproj")));
        fixture.Write("Other/obj/project.assets.json", File.ReadAllText(fixture.Path("obj", "project.assets.json")));
        fixture.Write("Apps.slnx", """
            <Solution>
              <Project Path="App.csproj" />
              <Project Path="Other/Other.csproj" />
            </Solution>
            """);
        fixture.AddWithoutManifest("Azure.Core", "package-two", "1.53.0");
        fixture.AddWithoutManifest("Azure.Core", "package-three", "1.62.0",
            new GuidanceScope(fixture.Path("Other", "obj", "project.assets.json"),
                fixture.Path("Other", "Other.csproj"), "net10.0", null));

        fixture.Run("init", "Apps.slnx").Should().Be(0, fixture.LastOutput);
        using var state = JsonDocument.Parse(File.ReadAllText(fixture.Path(".agentdocs", "agent-context.json")));
        var projects = state.RootElement.GetProperty("Graph").GetProperty("Projects").EnumerateArray().ToArray();
        projects.Should().HaveCount(2);
        foreach (var (project, version) in new[] { ("App.csproj", "1.53.0"), ("Other/Other.csproj", "1.62.0") })
            projects.Single(p => p.GetProperty("Project").GetString() == project)
                .GetProperty("Packages").EnumerateArray().Single(p => p.GetProperty("Id").GetString() == "Azure.Core")
                .GetProperty("Version").GetString().Should().Be(version);
        fixture.Run("check").Should().Be(0);
    }

    [Fact]
    public void Init_rejects_mixed_versions_when_only_one_publishes_guidance()
    {
        using var fixture = new Fixture();
        fixture.Write("package-two/guide/README.md", "# Other\n");
        fixture.AddWithoutManifest("Other.Publisher", "package-three", "3.0.0");
        fixture.AddPackage("Other.Publisher", "package-two", "guide/README.md");

        fixture.Run("init", "App.csproj").Should().NotBe(0);
        fixture.LastOutput.Should().Contain("Mixed versions of Other.Publisher");
        File.Exists(fixture.Path("AGENTS.md")).Should().BeFalse();
    }

    [Fact]
    public void Sync_prunes_only_removed_package_documents()
    {
        using var fixture = new Fixture();
        fixture.Write("package-two/guide/README.md", "# Other\n");
        fixture.AddPackage("Other.Publisher", "package-two", "guide/README.md");
        fixture.Run("init", "App.csproj").Should().Be(0);
        fixture.Write(".agentdocs/customer-notes.md", "# Mine\n");
        fixture.RemovePackage("Other.Publisher");
        fixture.Run("sync").Should().Be(0);
        File.Exists(fixture.Path(".agentdocs", "packages", "other.publisher", "guide", "README.md"))
            .Should().BeFalse();
        File.ReadAllText(fixture.Path(".agentdocs", "customer-notes.md")).Should().Be("# Mine\n");
        fixture.Run("check").Should().Be(0);
    }

    [Fact]
    public void Init_rejects_existing_case_alias_of_destination()
    {
        using var fixture = new Fixture();
        fixture.Write(".agentdocs/PACKAGES/example.library/guides/overview.md", "# Guide\n");
        fixture.Run("init", "--force", "App.csproj").Should().NotBe(0);
        File.Exists(fixture.Path("AGENTS.md")).Should().BeFalse();
    }

    [Theory]
    [InlineData("packages/example.library/guides/overview.md")]
    [InlineData("README.md")]
    [InlineData("agent-context.json")]
    public void Init_rejects_directory_at_managed_file_before_any_writes(string destination)
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(fixture.Path([".agentdocs", .. destination.Split('/')]));

        fixture.Run("init", "--force", "App.csproj").Should().NotBe(0);
        File.Exists(fixture.Path("AGENTS.md")).Should().BeFalse();
        File.Exists(fixture.Path(".agentdocs", "packages", "example.library", "guides", "details.md")).Should().BeFalse();
        File.Exists(fixture.Path(".agentdocs", "agent-context.json")).Should().BeFalse();
    }

    [Fact]
    public void Init_rejects_file_at_managed_parent_before_any_writes()
    {
        using var fixture = new Fixture();
        fixture.Write(".agentdocs/packages/example.library/guides", "# Unowned\n");
        var original = File.ReadAllBytes(fixture.Path(".agentdocs", "packages", "example.library", "guides"));

        fixture.Run("init", "--force", "App.csproj").Should().NotBe(0);
        File.ReadAllBytes(fixture.Path(".agentdocs", "packages", "example.library", "guides")).Should().Equal(original);
        File.Exists(fixture.Path("AGENTS.md")).Should().BeFalse();
        File.Exists(fixture.Path(".agentdocs", "agent-context.json")).Should().BeFalse();
    }

    [Fact]
    public void Init_rejects_nested_tool_manifest()
    {
        using var fixture = new Fixture();
        fixture.Scope("service");
        fixture.Run("init", "App.csproj").Should().NotBe(0);
        File.Exists(fixture.Path("service", ".agentdocs", "agent-context.json")).Should().BeFalse();
        File.Exists(fixture.Path(".agentdocs", "agent-context.json")).Should().BeFalse();
    }

    [Fact]
    public void Init_rejects_removed_scope_switch()
    {
        using var fixture = new Fixture();
        fixture.Run("init", "--scope", ".", "App.csproj").Should().NotBe(0);
        fixture.LastOutput.Should().Contain("Unknown option '--scope'");
        File.Exists(fixture.Path(".agentdocs", "agent-context.json")).Should().BeFalse();
    }

    [Fact]
    public void Check_rejects_assets_after_project_target_framework_changes()
    {
        using var fixture = new Fixture();
        fixture.Run("init", "App.csproj").Should().Be(0);
        fixture.Write("App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup></Project>");
        fixture.Run("check").Should().NotBe(0);
    }

    [Fact]
    public void Init_rejects_assets_before_new_project_reference_is_restored()
    {
        using var fixture = new Fixture();
        fixture.Write("Dependency.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        fixture.Write("App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <ItemGroup><ProjectReference Include="Dependency.csproj" /></ItemGroup>
            </Project>
            """);
        fixture.Run("init", "App.csproj").Should().NotBe(0);
        fixture.LastOutput.Should().Contain("ProjectReference");
    }

    [Fact]
    public void Init_rejects_assets_after_project_reference_is_removed()
    {
        using var fixture = new Fixture();
        fixture.Write("obj/project.assets.json", JsonSerializer.Serialize(new
        {
            project = new
            {
                restore = new
                {
                    originalTargetFrameworks = new List<string> { "net10.0" },
                    frameworks = new Dictionary<string, object>
                    {
                        ["net10.0"] = new
                        {
                            projectReferences = new Dictionary<string, object>
                            {
                                [fixture.Path("Dependency.csproj")] = new { projectPath = fixture.Path("Dependency.csproj") }
                            }
                        }
                    }
                },
                frameworks = new Dictionary<string, object> { ["net10.0"] = new { } }
            }
        }));
        fixture.Run("init", "App.csproj").Should().NotBe(0);
        fixture.LastOutput.Should().Contain("ProjectReference");
    }

    [Fact]
    public void Init_accepts_matching_restored_project_reference()
    {
        using var fixture = new Fixture();
        fixture.Write("Dependency.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        fixture.Write("App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <ItemGroup><ProjectReference Include="Dependency.csproj" /></ItemGroup>
            </Project>
            """);
        fixture.Write("obj/project.assets.json", JsonSerializer.Serialize(new
        {
            project = new
            {
                restore = new
                {
                    originalTargetFrameworks = new List<string> { "net10.0" },
                    frameworks = new Dictionary<string, object>
                    {
                        ["net10.0"] = new
                        {
                            projectReferences = new Dictionary<string, object>
                            {
                                [fixture.Path("Dependency.csproj")] = new { projectPath = fixture.Path("Dependency.csproj") }
                            }
                        }
                    }
                },
                frameworks = new Dictionary<string, object> { ["net10.0"] = new { } }
            }
        }));
        fixture.Run("init", "App.csproj").Should().Be(0);
    }

    [Fact]
    public void Init_accepts_non_csproj_references_omitted_from_restore_graph()
    {
        using var fixture = new Fixture();
        fixture.Write("App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="Schema.sqlproj"
                                  ReferenceOutputAssembly="false"
                                  SkipGetTargetFrameworkProperties="true"
                                  OutputItemType="None" />
                <ProjectReference Include="Native.vcxproj"
                                  ReferenceOutputAssembly="false" />
              </ItemGroup>
            </Project>
            """);
        fixture.Run("init", "App.csproj").Should().Be(0, fixture.LastOutput);
        fixture.Run("check").Should().Be(0, fixture.LastOutput);
    }

    [Fact]
    public void Init_accepts_build_only_csproj_reference_omitted_from_restore_graph()
    {
        using var fixture = new Fixture();
        fixture.Write("App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="Dependency.csproj"
                                  ReferenceOutputAssembly="false"
                                  SkipGetTargetFrameworkProperties="true"
                                  OutputItemType="None" />
              </ItemGroup>
            </Project>
            """);
        fixture.Run("init", "App.csproj").Should().Be(0, fixture.LastOutput);
        fixture.Run("check").Should().Be(0, fixture.LastOutput);
    }

    [Fact]
    public void Init_rejects_unrestored_fsharp_project_reference()
    {
        using var fixture = new Fixture();
        fixture.Write("App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <ItemGroup><ProjectReference Include="Dependency.fsproj" /></ItemGroup>
            </Project>
            """);
        fixture.Run("init", "App.csproj").Should().NotBe(0);
        fixture.LastOutput.Should().Contain("ProjectReference changed");
    }

    [Fact]
    public void Init_rejects_restored_reference_missing_from_evaluated_graph()
    {
        using var fixture = new Fixture();
        fixture.Write("obj/project.assets.json", JsonSerializer.Serialize(new
        {
            project = new
            {
                restore = new
                {
                    originalTargetFrameworks = new List<string> { "net10.0" },
                    frameworks = new Dictionary<string, object>
                    {
                        ["net10.0"] = new
                        {
                            projectReferences = new Dictionary<string, object>
                            {
                                [fixture.Path("Schema.sqlproj")] = new { projectPath = fixture.Path("Schema.sqlproj") }
                            }
                        }
                    }
                },
                frameworks = new Dictionary<string, object> { ["net10.0"] = new { } }
            }
        }));
        fixture.Run("init", "App.csproj").Should().NotBe(0);
        fixture.LastOutput.Should().Contain("ProjectReference changed");
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"_RestoreGraphEntry":{}}""")]
    [InlineData("""{"_RestoreGraphEntry":[{}]}""")]
    [InlineData("""{"_RestoreGraphEntry":[{"Type":"ProjectReference"}]}""")]
    public void ProjectReference_validation_reports_unavailable_nuget_graph(string itemsJson)
    {
        using var items = JsonDocument.Parse(itemsJson);
        using var restore = JsonDocument.Parse("{}");
        var method = typeof(AgentDocsCommand).GetMethod("ValidateProjectReferences",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        Action check = () => method.Invoke(null, ["App.csproj", "net10.0", restore.RootElement, items.RootElement]);

        check.Should().Throw<TargetInvocationException>().Which.InnerException.Should()
            .BeOfType<InvalidOperationException>().Which.Message.Should().Contain("Cannot evaluate NuGet restore graph");
    }

    [Fact]
    public void Init_rejects_runtime_identifier_missing_from_assets()
    {
        using var fixture = new Fixture();
        fixture.Write("App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><RuntimeIdentifier>win-x64</RuntimeIdentifier></PropertyGroup>
            </Project>
            """);
        fixture.Run("init", "App.csproj").Should().NotBe(0);
        fixture.LastOutput.Should().Contain("runtime");
    }

    [Fact]
    public void Init_accepts_runtime_identifier_recorded_in_assets()
    {
        using var fixture = new Fixture();
        fixture.Write("App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><RuntimeIdentifier>win-x64</RuntimeIdentifier></PropertyGroup>
            </Project>
            """);
        fixture.Write("obj/project.assets.json", JsonSerializer.Serialize(new
        {
            project = new
            {
                restore = new
                {
                    originalTargetFrameworks = new List<string> { "net10.0" },
                    runtimes = new List<string> { "win-x64" }
                },
                frameworks = new Dictionary<string, object> { ["net10.0"] = new { } }
            }
        }));
        fixture.Run("init", "App.csproj").Should().Be(0);
    }

    [Fact]
    public void Init_rejects_changed_runtime_identifiers_before_restore()
    {
        using var fixture = new Fixture();
        fixture.Write("App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><RuntimeIdentifier>win-x64</RuntimeIdentifier><RuntimeIdentifiers>win-x64;linux-x64</RuntimeIdentifiers></PropertyGroup>
            </Project>
            """);
        fixture.Write("obj/project.assets.json", JsonSerializer.Serialize(new
        {
            project = new
            {
                restore = new
                {
                    originalTargetFrameworks = new List<string> { "net10.0" },
                    runtimes = new List<string> { "win-x64" }
                },
                frameworks = new Dictionary<string, object> { ["net10.0"] = new { } }
            }
        }));
        fixture.Run("init", "App.csproj").Should().NotBe(0);
        fixture.LastOutput.Should().Contain("runtime");
    }

    [Fact]
    public void Init_accepts_matching_runtime_identifiers()
    {
        using var fixture = new Fixture();
        fixture.Write("App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><RuntimeIdentifier>win-x64</RuntimeIdentifier><RuntimeIdentifiers>win-x64;linux-x64</RuntimeIdentifiers></PropertyGroup>
            </Project>
            """);
        fixture.Write("obj/project.assets.json", JsonSerializer.Serialize(new
        {
            project = new
            {
                restore = new
                {
                    originalTargetFrameworks = new List<string> { "net10.0" },
                    runtimes = new List<string> { "win-x64", "linux-x64" }
                },
                frameworks = new Dictionary<string, object> { ["net10.0"] = new { } }
            }
        }));
        fixture.Run("init", "App.csproj").Should().Be(0);
        fixture.Run("check").Should().Be(0);
    }

    [Theory]
    [InlineData("BaseIntermediateOutputPath")]
    [InlineData("MSBuildProjectExtensionsPath")]
    public void Init_uses_evaluated_assets_path_for_custom_intermediate_directory(string property)
    {
        using var fixture = new Fixture();
        fixture.Write("Directory.Build.props",
            $"<Project><PropertyGroup><{property}>generated/</{property}></PropertyGroup></Project>");
        fixture.SetAssets("generated/project.assets.json");
        fixture.Run("init", "--restore", "App.csproj").Should().Be(0);
        File.Exists(fixture.Path("generated", "project.assets.json")).Should().BeTrue();
        fixture.Run("check").Should().Be(0);
    }

    [Fact]
    public void Conditional_multi_target_reference_is_checked_against_its_target_assets()
    {
        using var fixture = new Fixture();
        var version = ToolVersion();
        fixture.Write("App.csproj", MultiTargetProject("9.9.9"));
        fixture.Write("obj/project.assets.json", JsonSerializer.Serialize(new
        {
            project = new
            {
                restore = new { originalTargetFrameworks = new List<string> { "net9.0", "net10.0" } },
                frameworks = new Dictionary<string, object>
                {
                    ["net9.0"] = new { dependencies = new Dictionary<string, object>() },
                    ["net10.0"] = new { dependencies = new Dictionary<string, object>
                        { ["Example.Library"] = new { version = "[" + version + ", )" } } }
                }
            }
        }));
        fixture.Run("init", "App.csproj").Should().NotBe(0);
        fixture.Write("App.csproj", MultiTargetProject(version));
        fixture.Run("init", "App.csproj").Should().Be(0);
        fixture.Write("App.csproj", MultiTargetProject("9.9.9"));
        fixture.Run("check").Should().NotBe(0);
    }

    [Fact]
    public void Check_rejects_assets_after_imported_props_changes()
    {
        using var fixture = new Fixture();
        fixture.Write("Directory.Build.props", "<Project><PropertyGroup><MyRestoreChoice>One</MyRestoreChoice></PropertyGroup></Project>");
        fixture.Run("init", "App.csproj").Should().Be(0);
        fixture.Write("Directory.Build.props", "<Project><PropertyGroup><MyRestoreChoice>Two</MyRestoreChoice></PropertyGroup></Project>");
        fixture.Run("check").Should().NotBe(0);
    }

    [Fact]
    public void Init_requires_complete_restore_framework_snapshot()
    {
        using var fixture = new Fixture();
        fixture.Write("obj/project.assets.json", "{\"project\":{\"restore\":{}}}");
        fixture.Run("init", "App.csproj").Should().NotBe(0);
        File.Exists(fixture.Path("AGENTS.md")).Should().BeFalse();
    }

    [Fact]
    public void Check_does_not_take_writer_lock_or_change_restore_outputs()
    {
        using var fixture = new Fixture();
        fixture.Run("init", "App.csproj").Should().Be(0);
        var before = File.ReadAllBytes(fixture.Path("obj", "project.assets.json"));
        using var held = new FileStream(fixture.Path(".git", "agentdocs.lock"),
            FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        fixture.Run("check").Should().Be(0);
        File.ReadAllBytes(fixture.Path("obj", "project.assets.json")).Should().Equal(before);
        fixture.Run("check", "--restore").Should().NotBe(0);
        File.ReadAllBytes(fixture.Path("obj", "project.assets.json")).Should().Equal(before);
    }

    [Fact]
    public void Remove_succeeds_without_project_assets_or_package_cache()
    {
        using var fixture = new Fixture();
        fixture.Run("init", "App.csproj").Should().Be(0);
        File.Delete(fixture.Path("App.csproj"));
        File.Delete(fixture.Path("obj", "project.assets.json"));
        Directory.Delete(fixture.Path("package"), recursive: true);
        fixture.RunWithoutReader("remove").Should().Be(0);
        File.Exists(fixture.Path(".agentdocs", "agent-context.json")).Should().BeFalse();
    }

    [Fact]
    public void Init_consumes_shared_reader_without_parallel_manifest_parser()
    {
        using var fixture = new Fixture();
        var version = ToolVersion();
        var package = fixture.Path("cache", "example.library", version);
        Directory.CreateDirectory(System.IO.Path.Combine(package, "guidance"));
        Directory.CreateDirectory(System.IO.Path.Combine(package, "guides"));
        var payloads = new Dictionary<string, string>
        {
            ["overview.md"] = "# Guide\n",
            ["start-here.md"] = "Start here.\n",
            ["details.md"] = "# Cookbook\n"
        };
        foreach (var (name, content) in payloads)
            File.WriteAllText(System.IO.Path.Combine(package, "guides", name), content, new UTF8Encoding(false));
        File.WriteAllText(System.IO.Path.Combine(package, "guidance", "reference-manifest.json"),
            JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                documents = payloads.Keys.Select(name => new
                {
                    path = "guides/" + name,
                    sha256 = Convert.ToHexString(SHA256.HashData(
                        File.ReadAllBytes(System.IO.Path.Combine(package, "guides", name)))).ToLowerInvariant(),
                    usage = name == "start-here.md" ? "required" : "onDemand",
                    description = "About " + name
                }).ToArray(),
                publisherMetadata = new Dictionary<string, object>
                {
                    ["com.example"] = new { labels = ExampleMetadata }
                }
            }), new UTF8Encoding(false));
        var identity = "Example.Library/" + version;
        File.WriteAllText(fixture.Path("obj", "project.assets.json"), JsonSerializer.Serialize(new
        {
            version = 3,
            targets = new Dictionary<string, object>
            {
                ["net10.0"] = new Dictionary<string, object> { [identity] = new { type = "package" } }
            },
            libraries = new Dictionary<string, object>
            {
                [identity] = new { type = "package", path = "example.library/" + version, sha512 = "content-hash" }
            },
            packageFolders = new Dictionary<string, object> { [fixture.Path("cache") + System.IO.Path.DirectorySeparatorChar] = new { } },
            project = new
            {
                restore = new { projectPath = fixture.Path("App.csproj"), originalTargetFrameworks = new List<string> { "net10.0" } },
                frameworks = new Dictionary<string, object> { ["net10.0"] = new { } }
            }
        }), new UTF8Encoding(false));
        fixture.RunReal("init", "App.csproj").Should().Be(0);
        using var state = JsonDocument.Parse(File.ReadAllText(fixture.Path(".agentdocs", "agent-context.json")));
        state.RootElement.GetProperty("Graph").GetProperty("Projects")[0]
            .GetProperty("Packages")[0].GetProperty("ContentHash").GetString().Should().Be("content-hash");
        fixture.RunReal("check").Should().Be(0);
        File.WriteAllText(System.IO.Path.Combine(package, "guides", "overview.md"),
            "# Guide\r\n", new UTF8Encoding(false));
        fixture.RunReal("check").Should().NotBe(0);
    }

    [Fact]
    public void Init_restore_is_explicit_and_check_does_not_restore()
    {
        using var fixture = new Fixture();
        File.Delete(fixture.Path("obj", "project.assets.json"));
        fixture.Run("check", "--restore").Should().NotBe(0);
        File.Exists(fixture.Path("obj", "project.assets.json")).Should().BeFalse();
        fixture.Run("init", "--restore", "App.csproj").Should().Be(0);
        File.Exists(fixture.Path("obj", "project.assets.json")).Should().BeTrue();
    }

    [Fact]
    public void Refresh_verb_no_longer_exists_and_init_installs_no_restore_hooks()
    {
        using var fixture = new Fixture();
        fixture.Run("init", "App.csproj").Should().Be(0, fixture.LastOutput);
        File.Exists(fixture.Path("Directory.Build.targets")).Should().BeFalse();
        File.Exists(fixture.Path("Directory.Solution.targets")).Should().BeFalse();
        File.Exists(fixture.Path(".agentdocs", "restore.targets")).Should().BeFalse();
        fixture.Run("refresh").Should().NotBe(0);
        fixture.LastOutput.Should().Contain("Usage: agentdocs init|sync|check|remove");
    }

    [Fact]
    public void Legacy_state_with_restore_hooks_is_rejected_with_guidance()
    {
        using var fixture = new Fixture();
        fixture.Run("init", "App.csproj").Should().Be(0, fixture.LastOutput);
        var state = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(fixture.Path(".agentdocs", "agent-context.json")))!;
        state["RestoreEntries"] = System.Text.Json.Nodes.JsonNode.Parse(
            "[{\"InstructionFile\":\"Directory.Build.targets\",\"CanonicalSha256\":\"" + new string('0', 64) +
            "\",\"ExistedBefore\":false}]");
        File.WriteAllText(fixture.Path(".agentdocs", "agent-context.json"), state.ToJsonString());

        fixture.Run("sync").Should().NotBe(0);
        fixture.LastOutput.Should().Contain("restore hooks");
    }

    [Fact]
    public void Init_creates_an_empty_policy_and_leaves_every_package_pending()
    {
        using var fixture = new Fixture();
        fixture.RemovePolicy();
        fixture.Run("init", "App.csproj").Should().Be(0, fixture.LastOutput);
        var policy = File.ReadAllText(fixture.Path(".agentdocs", "policy.json"));
        using (var json = JsonDocument.Parse(policy))
            json.RootElement.GetProperty("approvedPackages").GetArrayLength().Should().Be(0);
        File.Exists(fixture.Path(".agentdocs", "packages", "example.library", "guides", "start-here.md")).Should().BeFalse();
        var index = File.ReadAllText(fixture.Path(".agentdocs", "README.md"));
        index.Should().Contain("No package guidance is enabled").And.Contain("## Pending review")
            .And.Contain("Example.Library").And.NotContain("## Rules").And.NotContain("start-here");
        fixture.LastOutput.Should().Contain("approvedPackages").And.Contain("\"Example.Library\"");
        fixture.Run("check").Should().Be(0, fixture.LastOutput);

        fixture.Approve("Example.Library");
        fixture.Run("sync").Should().Be(0, fixture.LastOutput);
        File.Exists(fixture.Path(".agentdocs", "packages", "example.library", "guides", "start-here.md")).Should().BeTrue();
        File.ReadAllText(fixture.Path(".agentdocs", "README.md")).Should().NotContain("## Pending review");
    }

    [Fact]
    public void Existing_policy_is_never_rewritten_or_removed()
    {
        using var fixture = new Fixture();
        var policy = File.ReadAllBytes(fixture.Path(".agentdocs", "policy.json"));
        fixture.Run("init", "App.csproj").Should().Be(0, fixture.LastOutput);
        File.ReadAllBytes(fixture.Path(".agentdocs", "policy.json")).Should().Equal(policy);
        fixture.Run("sync").Should().Be(0);
        fixture.Run("check").Should().Be(0);
        fixture.Run("remove").Should().Be(0);
        File.ReadAllBytes(fixture.Path(".agentdocs", "policy.json")).Should().Equal(policy);
        File.Exists(fixture.Path(".agentdocs", "agent-context.json")).Should().BeFalse();
    }

    [Fact]
    public void Discovery_is_asked_to_load_only_approved_packages()
    {
        using var fixture = new Fixture();
        fixture.Run("init", "App.csproj").Should().Be(0, fixture.LastOutput);
        fixture.LastShouldLoad.Should().NotBeNull();
        fixture.LastShouldLoad!("Example.Library").Should().BeTrue();
        fixture.LastShouldLoad("example.library").Should().BeTrue();
        fixture.LastShouldLoad("Other.Package").Should().BeFalse();
    }

    [Fact]
    public void Unapproved_packages_are_pending_and_never_installed()
    {
        using var fixture = new Fixture();
        fixture.Write("package-two/guide/README.md", "# Other\n");
        fixture.AddPackage("Other.Publisher", "package-two", "guide/README.md", approve: false);
        fixture.Run("init", "App.csproj").Should().Be(0, fixture.LastOutput);
        File.Exists(fixture.Path(".agentdocs", "packages", "other.publisher", "guide", "README.md")).Should().BeFalse();
        File.Exists(fixture.Path(".agentdocs", "packages", "example.library", "guides", "start-here.md")).Should().BeTrue();
        var index = File.ReadAllText(fixture.Path(".agentdocs", "README.md"));
        index.Should().Contain("## Pending review").And.Contain("Other.Publisher 2.0.0")
            .And.NotContain("Guide for Other.Publisher");
        fixture.LastOutput.Should().Contain("pending Other.Publisher 2.0.0")
            .And.Contain("\"Other.Publisher\"");
        fixture.Run("check").Should().Be(0);
    }

    [Fact]
    public void Mixed_versions_of_a_pending_package_do_not_block_init()
    {
        using var fixture = new Fixture();
        fixture.Write("package-two/guide/README.md", "# Other\n");
        fixture.Write("package-three/guide/README.md", "# Other\n");
        fixture.AddPackage("Other.Publisher", "package-two", "guide/README.md", approve: false);
        fixture.AddPackage("Other.Publisher", "package-three", "guide/README.md", "3.0.0", approve: false);
        fixture.Run("init", "App.csproj").Should().Be(0, fixture.LastOutput);
        File.ReadAllText(fixture.Path(".agentdocs", "README.md")).Should().Contain("Other.Publisher 2.0.0, 3.0.0");
    }

    [Fact]
    public void Withdrawing_approval_removes_the_owned_guide()
    {
        using var fixture = new Fixture();
        fixture.Run("init", "App.csproj").Should().Be(0, fixture.LastOutput);
        var guide = fixture.Path(".agentdocs", "packages", "example.library", "guides", "start-here.md");
        File.Exists(guide).Should().BeTrue();
        fixture.Unapprove("Example.Library");
        fixture.Run("sync").Should().Be(0, fixture.LastOutput);
        fixture.LastOutput.Should().Contain("is not approved");
        File.Exists(guide).Should().BeFalse();
        fixture.Run("check").Should().Be(0);
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("{\"schemaVersion\":2,\"approvedPackages\":[]}")]
    [InlineData("{\"schemaVersion\":1,\"approvedPackages\":[\"A\",\"a\"]}")]
    [InlineData("{\"schemaVersion\":1,\"approvedPackages\":[\"bad id\"]}")]
    [InlineData("{\"schemaVersion\":1,\"approvedPackages\":[],\"extra\":1}")]
    [InlineData("{\"schemaVersion\":1}")]
    public void Init_rejects_an_invalid_policy_before_writing(string policy)
    {
        using var fixture = new Fixture();
        fixture.Write(".agentdocs/policy.json", policy);
        fixture.Run("init", "App.csproj").Should().NotBe(0);
        fixture.LastOutput.Should().Contain("Invalid .agentdocs/policy.json");
        File.Exists(fixture.Path("AGENTS.md")).Should().BeFalse();
        File.Exists(fixture.Path(".agentdocs", "agent-context.json")).Should().BeFalse();
    }

    [Fact]
    public void Index_without_enabled_guidance_issues_no_read_instructions()
    {
        using var fixture = new Fixture { DefaultHasManifest = false };
        fixture.Run("init", "App.csproj").Should().Be(0, fixture.LastOutput);
        var index = File.ReadAllText(fixture.Path(".agentdocs", "README.md"));
        index.Should().Contain("No package guidance is enabled")
            .And.NotContain("## Rules").And.NotContain("Required").And.NotContain("packages/");
    }

    [Fact]
    public void Projects_are_grouped_by_the_enabled_packages_they_restore()
    {
        using var fixture = new Fixture();
        AddSecondProject(fixture);
        fixture.Write("package-two/guide/README.md", "# Other\n");
        fixture.AddPackage("Other.Publisher", "package-two", "guide/README.md", scope: OtherScope(fixture));

        fixture.Run("init", "Apps.slnx").Should().Be(0, fixture.LastOutput);
        var index = File.ReadAllText(fixture.Path(".agentdocs", "README.md"));
        var first = index[index.IndexOf("### Group 1", StringComparison.Ordinal)..index.IndexOf("### Group 2", StringComparison.Ordinal)];
        first.Should().Contain("`App.csproj`").And.Contain("Example.Library").And.NotContain("Other.Publisher");
        var second = index[index.IndexOf("### Group 2", StringComparison.Ordinal)..
            index.IndexOf("## On-demand documents", StringComparison.Ordinal)];
        second.Should().Contain("`Other/Other.csproj`").And.Contain("Other.Publisher").And.NotContain("Example.Library");
        index.Should().Contain("Restored by: Group 2");
    }

    [Fact]
    public void Projects_restoring_the_same_enabled_packages_share_one_group()
    {
        using var fixture = new Fixture { DefaultHasManifest = false };
        AddSecondProject(fixture);
        fixture.Write("package-two/guide/README.md", "# Other\n");
        fixture.AddPackage("Other.Publisher", "package-two", "guide/README.md");
        fixture.AddPackage("Other.Publisher", "package-two", "guide/README.md", scope: OtherScope(fixture));

        fixture.Run("init", "Apps.slnx").Should().Be(0, fixture.LastOutput);
        var index = File.ReadAllText(fixture.Path(".agentdocs", "README.md"));
        index.Should().Contain("### Group 1").And.NotContain("### Group 2")
            .And.Contain("`App.csproj`, `Other/Other.csproj`");
    }

    [Fact]
    public void Projects_without_enabled_guidance_are_listed_separately()
    {
        using var fixture = new Fixture();
        AddSecondProject(fixture);
        fixture.AddWithoutManifest("Azure.Core", "package-two", "1.53.0", OtherScope(fixture));

        fixture.Run("init", "Apps.slnx").Should().Be(0, fixture.LastOutput);
        var index = File.ReadAllText(fixture.Path(".agentdocs", "README.md"));
        index[index.IndexOf("### Projects without enabled guidance", StringComparison.Ordinal)..]
            .Should().Contain("`Other/Other.csproj`").And.NotContain("`App.csproj`");
    }

    [Fact]
    public void Printed_summary_reports_required_bytes_and_index_size()
    {
        using var fixture = new Fixture();
        fixture.Run("init", "App.csproj").Should().Be(0, fixture.LastOutput);
        fixture.LastOutput.Should().Contain("AgentDocs: group 1: 1 project(s)")
            .And.Contain("1 required document(s), 12 bytes.")
            .And.Contain("AgentDocs: index is ");
    }

    [Fact]
    public void Descriptions_render_as_inert_code_spans()
    {
        using var fixture = new Fixture();
        fixture.Write("package-two/guide/README.md", "# Other\n");
        fixture.AddPackage("Other.Publisher", "package-two", "guide/README.md",
            description: "See [x](http://e.example) `tick` <b>bold</b>");
        fixture.Run("init", "App.csproj").Should().Be(0, fixture.LastOutput);
        File.ReadAllText(fixture.Path(".agentdocs", "README.md"))
            .Should().Contain("``See [x](http://e.example) `tick` <b>bold</b>``");
    }

    private static void AddSecondProject(Fixture fixture)
    {
        fixture.Write("Other/Other.csproj", File.ReadAllText(fixture.Path("App.csproj")));
        fixture.Write("Other/obj/project.assets.json", File.ReadAllText(fixture.Path("obj", "project.assets.json")));
        fixture.Write("Apps.slnx", """
            <Solution>
              <Project Path="App.csproj" />
              <Project Path="Other/Other.csproj" />
            </Solution>
            """);
    }

    private static GuidanceScope OtherScope(Fixture fixture) =>
        new(fixture.Path("Other", "obj", "project.assets.json"), fixture.Path("Other", "Other.csproj"), "net10.0", null);

    private sealed class Fixture : IDisposable
    {
        private readonly string _root;
        private readonly string _previous;
        private readonly List<GuidancePackage> _extraPackages = [];
        private string _project = "App.csproj";
        private string _assets = "obj/project.assets.json";
        private readonly HashSet<string> _approved = new(StringComparer.OrdinalIgnoreCase) { "Example.Library" };
        public bool ShortGuideOnly { get; set; }
        public bool OmitPublisherMetadata { get; set; }
        public bool DefaultHasManifest { get; set; } = true;
        public string DefaultPackageVersion { get; set; } = ToolVersion();
        public string OverviewDescription { get; set; } = "Overview of the library.";
        public GuidanceUsage OverviewUsage { get; set; } = GuidanceUsage.OnDemand;
        public string? ReaderDiagnostic { get; set; }
        public string LastOutput { get; private set; } = "";
        public Func<string, bool>? LastShouldLoad { get; private set; }

        public Fixture()
        {
            _root = System.IO.Path.Combine(AppContext.BaseDirectory, "context-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            Directory.CreateDirectory(Path(".git"));
            _previous = Environment.CurrentDirectory;
            Environment.CurrentDirectory = _root;
            Write("Directory.Build.props", "<Project />");
            Write("App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
            Write("src/App.cs", "class App {}");
            Write("obj/project.assets.json", "{\"project\":{\"restore\":{\"originalTargetFrameworks\":[\"net10.0\"]},\"frameworks\":{\"net10.0\":{}}}}");
            var version = ToolVersion();
            Write(".config/dotnet-tools.json", JsonSerializer.Serialize(new
            {
                version = 1,
                isRoot = true,
                tools = new Dictionary<string, object>
                {
                    ["trellis.agentdocs"] = new { version, commands = new List<string> { "agentdocs" } }
                }
            }));
            Write("package/guides/overview.md", "# Guide\n");
            Write("package/guides/details.md", "# Cookbook\n");
            Write("package/guides/start-here.md", "Start here.\n");
            WritePolicy();
        }

        public void WritePolicy() => Write(".agentdocs/policy.json", JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            approvedPackages = _approved.OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToArray()
        }));

        public void Approve(string id)
        {
            _approved.Add(id);
            WritePolicy();
        }

        public void Unapprove(string id)
        {
            _approved.Remove(id);
            WritePolicy();
        }

        public void RemovePolicy() => File.Delete(Path(".agentdocs", "policy.json"));

        public string Path(params string[] pieces) => System.IO.Path.Combine([_root, .. pieces]);
        public void EnterRoot() => Environment.CurrentDirectory = _root;
        public void SetAssets(string relative) => _assets = relative;

        public void Scope(string relative)
        {
            _project = relative + "/App.csproj";
            _assets = relative + "/obj/project.assets.json";
            Write(_project, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
            Write(_assets, "{\"project\":{\"restore\":{\"originalTargetFrameworks\":[\"net10.0\"]},\"frameworks\":{\"net10.0\":{}}}}");
            Write(relative + "/.config/dotnet-tools.json", File.ReadAllText(Path(".config", "dotnet-tools.json")));
            Environment.CurrentDirectory = Path(relative);
        }

        public void AddPackage(string id, string root, string document, string version = "2.0.0",
            string? description = null, GuidanceScope? scope = null, bool approve = true)
        {
            var file = Path(root, document.Replace('/', System.IO.Path.DirectorySeparatorChar));
            var sha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))).ToLowerInvariant();
            var guidance = new GuidanceContribution(
                [new GuidanceDocument(new GuidanceIdentity(id, version, document), file, sha, GuidanceUsage.OnDemand,
                    description ?? "Guide for " + id)],
                new Dictionary<string, JsonElement>());
            _extraPackages.Add(new GuidancePackage(scope ?? new GuidanceScope(Path(_assets.Replace('/', System.IO.Path.DirectorySeparatorChar)),
                Path(_project.Replace('/', System.IO.Path.DirectorySeparatorChar)), "net10.0", null),
                id, version, Path(root), GuidanceStatus.Valid, guidance, null));
            if (approve)
                Approve(id);
        }

        public void AddWithoutManifest(string id, string root, string version = "1.0.0", GuidanceScope? scope = null)
        {
            Directory.CreateDirectory(Path(root));
            _extraPackages.Add(new GuidancePackage(scope ?? new GuidanceScope(
                Path(_assets.Replace('/', System.IO.Path.DirectorySeparatorChar)),
                Path(_project.Replace('/', System.IO.Path.DirectorySeparatorChar)), "net10.0", null),
                id, version, Path(root), GuidanceStatus.NoManifest, null, null));
        }

        public void RemovePackage(string id) => _extraPackages.RemoveAll(p => p.PackageId == id);

        public void Write(string path, string content)
        {
            var fullPath = System.IO.Path.Combine(_root, path.Replace('/', System.IO.Path.DirectorySeparatorChar));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, content, new UTF8Encoding(content.StartsWith('\uFEFF') ? false : true));
        }

        public int Run(params string[] args)
        {
            var sha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path("package", "guides", "overview.md")))).ToLowerInvariant();
            var doc = new GuidanceDocument(new GuidanceIdentity("Example.Library", DefaultPackageVersion,
                "guides/overview.md"), Path("package", "guides", "overview.md"), sha, OverviewUsage,
                OverviewDescription);
            var documents = new List<GuidanceDocument> { doc };
            if (!ShortGuideOnly)
                foreach (var name in new[] { "details.md", "start-here.md" })
                {
                    var path = Path("package", "guides", name);
                    documents.Add(new GuidanceDocument(new GuidanceIdentity("Example.Library", ToolVersion(), "guides/" + name),
                        path, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant(),
                        name == "details.md" ? GuidanceUsage.Supporting : GuidanceUsage.Required,
                        name == "details.md" ? null : "Start here."));
                }

            var contribution = new GuidanceContribution(documents,
                OmitPublisherMetadata ? new Dictionary<string, JsonElement>() : new Dictionary<string, JsonElement>
                {
                    ["com.example"] = JsonSerializer.SerializeToElement(new { labels = ExampleMetadata })
                });
            var guidance = new GuidanceDiscovery([new GuidancePackage(new GuidanceScope(
                Path(_assets.Replace('/', System.IO.Path.DirectorySeparatorChar)),
                Path(_project.Replace('/', System.IO.Path.DirectorySeparatorChar)), "net10.0", null),
                "Example.Library", doc.Identity.Version, Path("package"),
                DefaultHasManifest ? GuidanceStatus.Valid : GuidanceStatus.NoManifest,
                DefaultHasManifest ? contribution : null, null),
                .. _extraPackages], ReaderDiagnostic is null ? [] : [ReaderDiagnostic]);
            using var output = new StringWriter();
            var result = AgentDocsCommand.Run(args, output, (_, load) =>
            {
                LastShouldLoad = load;
                return guidance;
            });
            LastOutput = output.ToString();
            if (result != 0)
                Console.Error.WriteLine(LastOutput);
            return result;
        }

        public int RunReal(params string[] args)
        {
            Environment.CurrentDirectory = _root;
            using var output = new StringWriter();
            var result = AgentDocsCommand.Run(args, output, GuidanceReader.Discover);
            if (result != 0)
                Console.Error.WriteLine(output.ToString());
            return result;
        }

        public int RunWithoutReader(params string[] args)
        {
            Environment.CurrentDirectory = _root;
            using var output = new StringWriter();
            var result = AgentDocsCommand.Run(args, output,
                (_, _) => throw new InvalidOperationException("Remove must not discover package assets."));
            if (result != 0)
                Console.Error.WriteLine(output.ToString());
            return result;
        }

        public void Dispose()
        {
            Environment.CurrentDirectory = _previous;
            Directory.Delete(_root, recursive: true);
        }
    }

    private static string ToolVersion() => typeof(Program).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .Single(attribute => attribute.Key == "AgentDocsToolPackageVersion").Value ??
        throw new InvalidOperationException("Missing stamped tool package version.");

    private static string MultiTargetProject(string packageVersion) =>
        $"""
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup><TargetFrameworks>net9.0;net10.0</TargetFrameworks></PropertyGroup>
          <ItemGroup Condition="'$(TargetFramework)' == 'net10.0'">
            <PackageReference Include="Example.Library" Version="{packageVersion}" />
          </ItemGroup>
        </Project>
        """;
}
