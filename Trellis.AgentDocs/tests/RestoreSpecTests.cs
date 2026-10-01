namespace Trellis.AgentDocs.Tests;

using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Xunit;

public sealed class RestoreSpecTests
{
    private static string Canonical(string json)
    {
        using var document = JsonDocument.Parse(json);
        var method = typeof(AgentDocsCommand).GetMethod("CanonicalJson", BindingFlags.Static | BindingFlags.NonPublic)!;
        return (string)method.Invoke(null, [document.RootElement.Clone()])!;
    }

    [Fact]
    public void Dependency_text_from_assets_is_the_same_whatever_the_platform_line_endings_and_indentation()
    {
        // project.assets.json is written with the platform's newline, so the same restore reads back as these two
        // spellings on Windows and on Linux. The recorded graph hashes this text, so they must agree.
        var windows = "{\r\n    \"suppressParent\": \"All\",\r\n    \"target\": \"Package\",\r\n    \"version\": \"[2.0.5, )\",\r\n    \"versionCentrallyManaged\": true\r\n  }";
        var linux = "{\n    \"suppressParent\": \"All\",\n    \"target\": \"Package\",\n    \"version\": \"[2.0.5, )\",\n    \"versionCentrallyManaged\": true\n  }";
        var reindented = "{\"suppressParent\":\"All\",\"target\":\"Package\",\"version\":\"[2.0.5, )\",\"versionCentrallyManaged\":true}";

        Canonical(windows).Should().Be(Canonical(linux)).And.Be(Canonical(reindented));
        Canonical(windows).Should().NotContain("\r").And.NotContain("\n").And.Contain("\"version\":\"[2.0.5, )\"");
    }

    [Fact]
    public void A_different_dependency_still_changes_the_text()
    {
        Canonical("{\"version\":\"[2.0.5, )\"}").Should().NotBe(Canonical("{\"version\":\"[2.0.6, )\"}"));
        Canonical("{\"version\":\"[2.0.5, )\",\"target\":\"Package\"}").Should().NotBe(Canonical("{\"version\":\"[2.0.5, )\"}"));
    }
}
