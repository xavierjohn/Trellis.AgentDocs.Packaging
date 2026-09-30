namespace Trellis.AgentDocs.Tests;

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Trellis.Guidance.Reader;
using Xunit;

public sealed class ValidateCommandTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "agentdocs-validate-" + Guid.NewGuid().ToString("N"));

    public ValidateCommandTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, true);

    [Fact]
    public void A_clean_package_directory_passes()
    {
        WritePackage(startDescription: "Read before using X.");

        var (code, output) = Validate(_root);

        code.Should().Be(0, output);
        output.Should().Contain("2 document(s)").And.Contain("0 error(s), 0 warning(s)");
    }

    [Fact]
    public void A_nupkg_is_read_directly()
    {
        WritePackage(startDescription: "Read before using X.");
        var nupkg = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".nupkg");
        try
        {
            ZipFile.CreateFromDirectory(_root, nupkg);
            var (code, output) = Validate(nupkg);
            code.Should().Be(0, output);
        }
        finally
        {
            File.Delete(nupkg);
        }
    }

    [Fact]
    public void Errors_fail_the_command_and_name_the_rule_and_document()
    {
        WritePackage(startDescription: null);

        var (code, output) = Validate(_root);

        code.Should().Be(1);
        output.Should().Contain("error AD005").And.Contain("guide/start.md").And.Contain("1 error(s)");
    }

    [Fact]
    public void Warnings_pass_unless_strict_is_requested()
    {
        WritePackage(startDescription: "Read before using X.", startText: "# Start\n\n[gone](missing.md)\n");

        var relaxed = Validate(_root);
        relaxed.Code.Should().Be(0, relaxed.Output);
        relaxed.Output.Should().Contain("warning AD102");

        Validate(_root, "--strict").Code.Should().Be(1);
    }

    [Fact]
    public void The_required_size_threshold_is_configurable()
    {
        WritePackage(startDescription: "Read before using X.", startText: "# Start\n\n[h](http.md)\n" + new string('x', 3000));

        Validate(_root).Output.Should().NotContain("AD101");
        var (code, output) = Validate(_root, "--max-required-bytes", "1000", "--strict");
        code.Should().Be(1);
        output.Should().Contain("warning AD101");
    }

    [Fact]
    public void Json_output_is_machine_readable_and_goes_to_the_supplied_writer()
    {
        WritePackage(startDescription: null);

        var (code, output) = Validate(_root, "--format", "json");

        code.Should().Be(1);
        using var document = JsonDocument.Parse(output);
        var root = document.RootElement;
        root.GetProperty("ok").GetBoolean().Should().BeFalse();
        root.GetProperty("errors").GetInt32().Should().Be(1);
        var finding = root.GetProperty("diagnostics").EnumerateArray().Single();
        finding.GetProperty("code").GetString().Should().Be("AD005");
        finding.GetProperty("severity").GetString().Should().Be("error");
        finding.GetProperty("path").GetString().Should().Be("guide/start.md");
    }

    [Fact]
    public void A_package_without_a_manifest_fails_with_AD001()
    {
        File.WriteAllText(Path.Combine(_root, "readme.txt"), "nothing here");
        var (code, output) = Validate(_root);
        code.Should().Be(1);
        output.Should().Contain("error AD001");
    }

    [Theory]
    [InlineData("--bogus")]
    [InlineData("--max-required-bytes")]
    [InlineData("--max-required-bytes", "-5")]
    [InlineData("--max-required-bytes", "many")]
    [InlineData("--format")]
    [InlineData("--format", "xml")]
    public void Bad_options_are_rejected(params string[] extra)
    {
        WritePackage(startDescription: "Read before using X.");
        var (code, output) = Validate(_root, extra);
        code.Should().Be(1);
        output.Should().StartWith("agentdocs: ");
    }

    [Fact]
    public void A_missing_target_is_reported_and_no_target_prints_usage()
    {
        Validate(Path.Combine(_root, "nope.nupkg")).Output.Should().Contain("neither a package file nor a directory");

        using var writer = new StringWriter();
        AgentDocsCommand.Run(["validate"], writer, GuidanceReader.Discover).Should().Be(2);
        writer.ToString().Should().Contain("Usage: agentdocs validate");
    }

    [Fact]
    public void Two_targets_are_rejected_and_a_corrupt_nupkg_is_reported()
    {
        Validate(_root, _root).Output.Should().Contain("one package or directory");

        var corrupt = Path.Combine(_root, "corrupt.nupkg");
        File.WriteAllText(corrupt, "not a zip");
        var (code, output) = Validate(corrupt);
        code.Should().Be(1);
        output.Should().Contain("not a readable package");
    }

    [Fact]
    public void The_general_usage_line_mentions_validate()
    {
        using var writer = new StringWriter();
        AgentDocsCommand.Run([], writer, GuidanceReader.Discover).Should().Be(2);
        writer.ToString().Should().Contain("agentdocs validate");
    }

    private static (int Code, string Output) Validate(string target, params string[] extra)
    {
        using var writer = new StringWriter();
        var code = AgentDocsCommand.Run(["validate", target, .. extra], writer, GuidanceReader.Discover);
        return (code, writer.ToString());
    }

    private void WritePackage(string? startDescription, string startText = "# Start\n\n[http](http.md)\n")
    {
        var start = Encoding.UTF8.GetBytes(startText);
        var http = Encoding.UTF8.GetBytes("# Http\n");
        Directory.CreateDirectory(Path.Combine(_root, "guide"));
        Directory.CreateDirectory(Path.Combine(_root, "guidance"));
        File.WriteAllBytes(Path.Combine(_root, "guide", "start.md"), start);
        File.WriteAllBytes(Path.Combine(_root, "guide", "http.md"), http);
        string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var description = startDescription is null ? "" : ",\"description\":" + JsonSerializer.Serialize(startDescription);
        File.WriteAllText(Path.Combine(_root, "guidance", "reference-manifest.json"),
            "{\"schemaVersion\":1,\"documents\":[" +
            "{\"path\":\"guide/start.md\",\"sha256\":\"" + Hash(start) + "\",\"usage\":\"required\"" + description + "}," +
            "{\"path\":\"guide/http.md\",\"sha256\":\"" + Hash(http) + "\",\"usage\":\"supporting\"}]}");
    }
}
