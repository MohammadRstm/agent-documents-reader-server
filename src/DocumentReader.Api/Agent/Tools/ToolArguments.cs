using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DocumentReader.Api.Agent.Tools;

/// <summary>
/// Strict reader for a tool's JSON input. The input must be an object containing only the allowed
/// properties, each with the expected type and within bounds; anything else is rejected with a
/// <see cref="ToolArgumentException"/>. Schemas given to the model are documentation only; this is
/// the enforcement.
/// </summary>
public sealed partial class ToolArguments
{
    private readonly JsonElement _input;

    public ToolArguments(JsonElement input, params string[] allowedProperties)
    {
        if (input.ValueKind != JsonValueKind.Object)
        {
            throw new ToolArgumentException("Arguments must be a JSON object.");
        }

        var allowed = new HashSet<string>(allowedProperties, StringComparer.Ordinal);
        var unknown = input.EnumerateObject().Select(property => property.Name).Where(name => !allowed.Contains(name)).ToList();
        if (unknown.Count > 0)
        {
            throw new ToolArgumentException(
                $"Unknown argument(s): {string.Join(", ", unknown.Take(5).Select(Quote))}. Allowed: {(allowed.Count == 0 ? "none" : string.Join(", ", allowedProperties))}.");
        }

        _input = input;
    }

    public string RequiredString(string name, int maxLength)
    {
        var value = OptionalString(name, maxLength);
        return value ?? throw new ToolArgumentException($"\"{name}\" is required.");
    }

    public string? OptionalString(string name, int maxLength)
    {
        if (!TryGet(name, out var element))
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            throw new ToolArgumentException($"\"{name}\" must be a string.");
        }

        var value = element.GetString()!.Trim();
        if (value.Length == 0 || value.Length > maxLength)
        {
            throw new ToolArgumentException($"\"{name}\" must be 1 to {maxLength} characters.");
        }

        return value;
    }

    public int OptionalInt(string name, int min, int max, int defaultValue)
    {
        if (!TryGet(name, out var element))
        {
            return defaultValue;
        }

        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out var value) || value < min || value > max)
        {
            throw new ToolArgumentException($"\"{name}\" must be a whole number from {min} to {max}.");
        }

        return value;
    }

    public bool OptionalBool(string name, bool defaultValue)
    {
        if (!TryGet(name, out var element))
        {
            return defaultValue;
        }

        return element.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new ToolArgumentException($"\"{name}\" must be true or false."),
        };
    }

    public IReadOnlyList<string>? OptionalStringArray(string name, int maxItems, int maxItemLength)
    {
        if (!TryGet(name, out var element))
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            throw new ToolArgumentException($"\"{name}\" must be an array of strings.");
        }

        var items = new List<string>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || item.GetString() is not { } text
                || text.Trim().Length == 0 || text.Length > maxItemLength)
            {
                throw new ToolArgumentException($"\"{name}\" must contain non-empty strings of at most {maxItemLength} characters.");
            }

            items.Add(text.Trim());
        }

        if (items.Count == 0 || items.Count > maxItems)
        {
            throw new ToolArgumentException($"\"{name}\" must contain 1 to {maxItems} items.");
        }

        return items.Distinct(StringComparer.Ordinal).ToList();
    }

    public IReadOnlyList<string> RequiredStringArray(string name, int maxItems, int maxItemLength) =>
        OptionalStringArray(name, maxItems, maxItemLength) ?? throw new ToolArgumentException($"\"{name}\" is required.");

    /// <summary>Reads an enum given as its camelCase name, e.g. "sum" or "sectionHeader".</summary>
    public TEnum? OptionalEnum<TEnum>(string name)
        where TEnum : struct, Enum
    {
        var text = OptionalString(name, 50);
        return text is null ? null : ParseEnum<TEnum>(name, text);
    }

    public TEnum RequiredEnum<TEnum>(string name)
        where TEnum : struct, Enum =>
        OptionalEnum<TEnum>(name) ?? throw new ToolArgumentException($"\"{name}\" is required.");

    public IReadOnlyList<TEnum>? OptionalEnumArray<TEnum>(string name)
        where TEnum : struct, Enum =>
        OptionalStringArray(name, Enum.GetValues<TEnum>().Length, 50)?.Select(text => ParseEnum<TEnum>(name, text)).ToList();

    /// <summary>Reads a calendar date in yyyy-MM-dd format.</summary>
    public DateOnly? OptionalDate(string name)
    {
        var text = OptionalString(name, 10);
        if (text is null)
        {
            return null;
        }

        return DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : throw new ToolArgumentException($"\"{name}\" must be a date in yyyy-MM-dd format.");
    }

    /// <summary>Reads an array of objects; each item is checked with its own <see cref="ToolArguments"/>.</summary>
    public IReadOnlyList<JsonElement>? OptionalObjectArray(string name, int maxItems)
    {
        if (!TryGet(name, out var element))
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            throw new ToolArgumentException($"\"{name}\" must be an array.");
        }

        var items = element.EnumerateArray().ToList();
        if (items.Count > maxItems)
        {
            throw new ToolArgumentException($"\"{name}\" may contain at most {maxItems} items.");
        }

        return items;
    }

    public bool TryGet(string name, out JsonElement element)
    {
        if (_input.TryGetProperty(name, out element) && element.ValueKind != JsonValueKind.Null)
        {
            return true;
        }

        element = default;
        return false;
    }

    /// <summary>Checks an ID's format before any lookup (e.g. E12, T3).</summary>
    public static bool IsEvidenceId(string value) => EvidenceIdPattern().IsMatch(value);

    public static bool IsTableId(string value) => TableIdPattern().IsMatch(value);

    public static bool IsDocumentId(string value) => DocumentIdPattern().IsMatch(value);

    private static TEnum ParseEnum<TEnum>(string name, string text)
        where TEnum : struct, Enum
    {
        // Only exact camelCase names are accepted; numeric values are rejected.
        foreach (var value in Enum.GetValues<TEnum>())
        {
            if (string.Equals(JsonNamingPolicyName(value), text, StringComparison.Ordinal))
            {
                return value;
            }
        }

        var allowed = string.Join(", ", Enum.GetValues<TEnum>().Select(JsonNamingPolicyName));
        throw new ToolArgumentException($"\"{name}\" must be one of: {allowed}.");
    }

    private static string JsonNamingPolicyName<TEnum>(TEnum value)
        where TEnum : struct, Enum =>
        JsonNamingPolicy.CamelCase.ConvertName(value.ToString());

    private static string Quote(string name) => $"\"{(name.Length > 40 ? name[..40] : name)}\"";

    [GeneratedRegex("^E[1-9][0-9]{0,8}$")]
    private static partial Regex EvidenceIdPattern();

    [GeneratedRegex("^T[1-9][0-9]{0,8}$")]
    private static partial Regex TableIdPattern();

    [GeneratedRegex("^[0-9a-f]{32}$")]
    private static partial Regex DocumentIdPattern();
}
