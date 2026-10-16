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

    [Fact]
    public void Project_evaluations_are_cached_by_normalized_project_and_framework()
    {
        var project = Path.Combine(AppContext.BaseDirectory, "App.csproj");
        var other = Path.Combine(AppContext.BaseDirectory, "Other.csproj");
        var calls = new List<(string Project, string? Framework)>();
        var cache = CachedEvaluations((path, framework) =>
        {
            calls.Add((path, framework));
            return JsonDocument.Parse(JsonSerializer.Serialize(calls.Count));
        });
        using (cache.Owner)
        {
            cache.Get(project, null).GetInt32().Should().Be(1);
            cache.Get(Path.Combine(AppContext.BaseDirectory, "nested", "..", "App.csproj"), null)
                .GetInt32().Should().Be(1);
            cache.Get(project, "net10.0").GetInt32().Should().Be(2);
            cache.Get(project, "net10.0").GetInt32().Should().Be(2);
            cache.Get(project, "NET10.0").GetInt32().Should().Be(3);
            cache.Get(project, "").GetInt32().Should().Be(4);
            cache.Get(other, null).GetInt32().Should().Be(5);
        }

        calls.Select(call => call.Project).Should().Equal(project, project, project, project, other);
        calls.Select(call => call.Framework).Should().Equal(null, "net10.0", "NET10.0", "", null);
    }

    [Fact]
    public void Project_evaluations_use_the_platform_project_path_comparer()
    {
        var project = Path.Combine(AppContext.BaseDirectory, "App.csproj");
        var calls = 0;
        var cache = CachedEvaluations((_, _) =>
        {
            calls++;
            return JsonDocument.Parse("{}");
        });
        using (cache.Owner)
        {
            cache.Get(project, null);
            cache.Get(project.ToUpperInvariant(), null);
        }

        calls.Should().Be(OperatingSystem.IsWindows() ? 1 : 2);
    }

    [Fact]
    public void Project_evaluation_caches_are_scoped_and_dispose_their_documents()
    {
        var project = Path.Combine(AppContext.BaseDirectory, "App.csproj");
        var documents = new List<JsonDocument>();
        JsonDocument Evaluate(string _, string? __)
        {
            var document = JsonDocument.Parse("{}");
            documents.Add(document);
            return document;
        }

        var cache = CachedEvaluations(Evaluate);
        JsonElement root;
        using (cache.Owner)
        {
            root = cache.Get(project, null);
            cache.Get(project, "net10.0");
        }

        documents.Should().HaveCount(2);
        Action readRoot = () => root.GetRawText();
        readRoot.Should().Throw<ObjectDisposedException>();
        Action useDisposedCache = () => cache.Get(project, null);
        useDisposedCache.Should().Throw<ObjectDisposedException>();

        var next = CachedEvaluations(Evaluate);
        using (next.Owner)
            next.Get(project, null).ValueKind.Should().Be(JsonValueKind.Object);
        documents.Should().HaveCount(3);
        foreach (var document in documents)
        {
            Action read = () => document.RootElement.GetRawText();
            read.Should().Throw<ObjectDisposedException>();
        }
    }

    [Fact]
    public void Failed_project_evaluations_are_not_cached_or_hidden()
    {
        var calls = 0;
        var cache = CachedEvaluations((_, _) =>
        {
            if (++calls == 1)
                throw new InvalidOperationException("evaluation failed");
            return JsonDocument.Parse("{}");
        });
        using (cache.Owner)
        {
            var project = Path.Combine(AppContext.BaseDirectory, "App.csproj");
            Action evaluate = () => cache.Get(project, null);
            evaluate.Should().Throw<InvalidOperationException>().WithMessage("evaluation failed");
            cache.Get(project, null).ValueKind.Should().Be(JsonValueKind.Object);
            cache.Get(project, null).ValueKind.Should().Be(JsonValueKind.Object);
        }

        calls.Should().Be(2);
    }

    private static (IDisposable Owner, Func<string, string?, JsonElement> Get) CachedEvaluations(
        Func<string, string?, JsonDocument> evaluate)
    {
        var type = typeof(AgentDocsCommand).GetNestedType("ProjectEvaluations", BindingFlags.NonPublic)!;
        var owner = (IDisposable)Activator.CreateInstance(type, [evaluate])!;
        return (owner, type.GetMethod("Get")!.CreateDelegate<Func<string, string?, JsonElement>>(owner));
    }
}
