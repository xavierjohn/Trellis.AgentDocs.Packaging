namespace Trellis.AgentDocs;

using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

/// <summary>The consumer-owned policy: which packages are approved and where agent skills are written.</summary>
internal sealed record AgentDocsPolicy(HashSet<string> ApprovedPackages, string[] SkillDirectories);

public static partial class AgentDocsCommand
{
    private const string PolicyFile = "policy.json";
    private static readonly string[] ToolOwnedPaths = ["README.md"];
    private static readonly Regex PackageId = new(@"^[A-Za-z0-9][A-Za-z0-9._-]*$", RegexOptions.Compiled);

    private static byte[] PolicyTemplate() =>
        new UTF8Encoding(false).GetBytes("{\n  \"schemaVersion\": 1,\n  \"approvedPackages\": []\n}\n");

    /// <summary>
    /// Reads the consumer-owned policy. A missing file approves nothing, so every guidance package is
    /// pending, and writes skills to the default directory. The tool never rewrites an existing policy.
    /// </summary>
    private static AgentDocsPolicy LoadPolicy(string path)
    {
        var approved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var skillDirectories = DefaultSkillDirectories;
        if (!File.Exists(path))
            return new AgentDocsPolicy(approved, skillDirectories);
        try
        {
            using var policy = JsonDocument.Parse(File.ReadAllBytes(path).AsMemory(
                File.ReadAllBytes(path).AsSpan().StartsWith(UTF8Encoding.UTF8.GetPreamble()) ? 3 : 0));
            var root = policy.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                root.EnumerateObject().GroupBy(p => p.Name, StringComparer.Ordinal).Any(g => g.Count() > 1) ||
                root.EnumerateObject().Any(p => p.Name is not ("schemaVersion" or "approvedPackages" or "skillDirectories")))
                throw Invalid("expected an object with only schemaVersion, approvedPackages and skillDirectories");
            if (!root.TryGetProperty("schemaVersion", out var version) || version.ValueKind != JsonValueKind.Number ||
                !version.TryGetInt32(out var number) || number != 1)
                throw Invalid("unsupported or missing schemaVersion");
            if (!root.TryGetProperty("approvedPackages", out var list) || list.ValueKind != JsonValueKind.Array)
                throw Invalid("approvedPackages must be an array of package IDs");
            foreach (var item in list.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String || !PackageId.IsMatch(item.GetString()!))
                    throw Invalid("approvedPackages must contain only NuGet package IDs");
                if (!approved.Add(item.GetString()!))
                    throw Invalid($"duplicate package ID '{item.GetString()}'");
            }

            if (root.TryGetProperty("skillDirectories", out var directories))
            {
                if (directories.ValueKind != JsonValueKind.Array)
                    throw Invalid("skillDirectories must be an array of skill directories");
                var chosen = new List<string>();
                foreach (var item in directories.EnumerateArray())
                {
                    var value = item.ValueKind == JsonValueKind.String ? item.GetString()! : null;
                    if (value is null || !AllowedSkillDirectories.Contains(value, StringComparer.Ordinal))
                        throw Invalid("skillDirectories may contain only " +
                            string.Join(" and ", AllowedSkillDirectories.Select(d => $"'{d}'")));
                    if (chosen.Contains(value, StringComparer.Ordinal))
                        throw Invalid($"duplicate skill directory '{value}'");
                    chosen.Add(value);
                }

                skillDirectories = chosen.ToArray();
            }
        }
        catch (JsonException e)
        {
            throw Invalid(e.Message);
        }

        return new AgentDocsPolicy(approved, skillDirectories);

        static InvalidOperationException Invalid(string reason) =>
            new($"Invalid .agentdocs/{PolicyFile}: {reason}.");
    }
}
