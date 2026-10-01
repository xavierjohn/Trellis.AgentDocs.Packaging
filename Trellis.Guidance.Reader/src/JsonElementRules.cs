namespace Trellis.Guidance.Reader;

using System.Text.Json;

internal static class JsonElementRules
{
    public static bool Object(JsonElement element) => element.ValueKind == JsonValueKind.Object;

    public static bool DuplicateProperties(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject().GroupBy(p => p.Name, StringComparer.Ordinal).Any(g => g.Count() > 1) ||
            element.EnumerateObject().Any(p => DuplicateProperties(p.Value)),
        JsonValueKind.Array => element.EnumerateArray().Any(DuplicateProperties),
        _ => false
    };

    public static bool TryArray(JsonElement obj, string name, out JsonElement value) =>
        obj.TryGetProperty(name, out value) && value.ValueKind == JsonValueKind.Array;

    public static bool TryObject(JsonElement obj, string name, out JsonElement value) =>
        obj.TryGetProperty(name, out value) && Object(value);

    public static bool TryString(JsonElement obj, string name, out string value)
    {
        value = "";
        if (!obj.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.String ||
            element.GetString() is not { Length: > 0 } text)
            return false;
        value = text;
        return true;
    }
}
