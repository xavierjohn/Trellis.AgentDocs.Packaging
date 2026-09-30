namespace Trellis.AgentDocs;

using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using Trellis.Guidance.Reader;

public static partial class AgentDocsCommand
{
    private const string ValidateUsage =
        "Usage: agentdocs validate <package.nupkg|directory> [--strict] [--max-required-bytes N] [--format text|json]";

    /// <summary>
    /// Validates a package's guidance for its author. Read-only and independent of any repository: it needs no
    /// <c>.agentdocs/</c> folder, no restore and no approval policy.
    /// </summary>
    private static int Validate(string[] args, TextWriter output)
    {
        string? target = null;
        var strict = false;
        var json = false;
        var options = new GuidanceValidationOptions();
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--strict": strict = true; break;
                case "--max-required-bytes":
                    if (++i == args.Length || !long.TryParse(args[i], NumberStyles.None, CultureInfo.InvariantCulture, out var bytes))
                        throw new ArgumentException("--max-required-bytes requires a non-negative integer.");
                    options = options with { MaxRequiredBytes = bytes };
                    break;
                case "--format":
                    if (++i == args.Length || args[i] is not ("text" or "json"))
                        throw new ArgumentException("--format must be text or json.");
                    json = args[i] == "json";
                    break;
                default:
                    if (args[i].StartsWith('-'))
                        throw new ArgumentException($"Unknown option '{args[i]}' for 'validate'.");
                    if (target is not null)
                        throw new ArgumentException("validate takes one package or directory.");
                    target = args[i];
                    break;
            }
        }

        if (target is null)
        {
            output.WriteLine(ValidateUsage);
            return 2;
        }

        if (!Directory.Exists(target) && !File.Exists(target))
            throw new ArgumentException($"'{target}' is neither a package file nor a directory.");

        GuidanceValidation result;
        try
        {
            result = GuidanceValidator.ValidatePackage(target, options);
        }
        catch (InvalidDataException e)
        {
            throw new ArgumentException($"'{target}' is not a readable package: {e.Message}");
        }

        var failed = result.HasErrors || (strict && result.WarningCount > 0);
        if (json)
            WriteJson(result, failed, output);
        else
            WriteText(result, output);
        return failed ? 1 : 0;
    }

    private static void WriteText(GuidanceValidation result, TextWriter output)
    {
        foreach (var d in result.Diagnostics.OrderBy(d => d.Severity).ThenBy(d => d.Code, StringComparer.Ordinal)
                     .ThenBy(d => d.Path, StringComparer.Ordinal).ThenBy(d => d.Line))
        {
            var where = d.Path is null ? "" : d.Line is { } line ? $"{d.Path}({line}): " : $"{d.Path}: ";
            output.WriteLine($"{where}{(d.Severity == GuidanceSeverity.Error ? "error" : "warning")} {d.Code}: {d.Message}");
        }

        output.WriteLine($"agentdocs validate: {result.DocumentCount} document(s); required documents {result.RequiredBytes:N0} bytes " +
            $"(about {result.RequiredBytes / 4:N0} tokens); {result.ErrorCount} error(s), {result.WarningCount} warning(s).");
    }

    private static void WriteJson(GuidanceValidation result, bool failed, TextWriter output)
    {
        var document = new
        {
            ok = !failed,
            documents = result.DocumentCount,
            requiredBytes = result.RequiredBytes,
            errors = result.ErrorCount,
            warnings = result.WarningCount,
            diagnostics = result.Diagnostics.Select(d => new
            {
                code = d.Code,
                severity = d.Severity == GuidanceSeverity.Error ? "error" : "warning",
                path = d.Path,
                line = d.Line,
                message = d.Message
            })
        };
        output.WriteLine(JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true }));
    }
}
