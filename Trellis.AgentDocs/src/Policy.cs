namespace Trellis.AgentDocs;

using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

public static partial class AgentDocsCommand
{
    private const string PolicyFile = "policy.json";
    private static readonly string[] ToolOwnedPaths = ["README.md"];
    private static readonly Regex PackageId = new(@"^[A-Za-z0-9][A-Za-z0-9._-]*$", RegexOptions.Compiled);

    private static byte[] PolicyTemplate() =>
        new UTF8Encoding(false).GetBytes("{\n  \"schemaVersion\": 1,\n  \"approvedPackages\": []\n}\n");

    /// <summary>
    /// Reads the consumer-owned approval list. A missing file approves nothing, so every guidance package is
    /// pending. The tool never rewrites an existing policy.
    /// </summary>
    private static HashSet<string> LoadApprovedPackages(string path, TextWriter output)
    {
        var approved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path))
            return approved;
        try
        {
            using var policy = JsonDocument.Parse(File.ReadAllBytes(path).AsMemory(
                File.ReadAllBytes(path).AsSpan().StartsWith(UTF8Encoding.UTF8.GetPreamble()) ? 3 : 0));
            var root = policy.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                root.EnumerateObject().GroupBy(p => p.Name, StringComparer.Ordinal).Any(g => g.Count() > 1) ||
                root.EnumerateObject().Any(p => p.Name is not ("schemaVersion" or "approvedPackages")))
                throw Invalid("expected an object with only schemaVersion and approvedPackages");
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
        }
        catch (JsonException e)
        {
            throw Invalid(e.Message);
        }

        return approved;

        static InvalidOperationException Invalid(string reason) =>
            new($"Invalid .agentdocs/{PolicyFile}: {reason}.");
    }
}
