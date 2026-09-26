using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace WClop.Core.Settings;

/// <summary>
/// Reads and writes one setting by a dotted path such as <c>compression.imageFactor</c> or
/// <c>watching.images.folders</c>, on the live settings object (so everything holding a reference sees the change).
/// Used by <c>wclop settings get/set</c>.
/// </summary>
public static class SettingsPath
{
    public static string Get(AppSettings settings, string path)
    {
        var (owner, property) = Resolve(settings, path);
        var value = property.GetValue(owner);
        return JsonSerializer.Serialize(value, value?.GetType() ?? typeof(object), SettingsStore.JsonOptions);
    }

    /// <summary>Sets a value given as text: numbers, true/false, enum names, and comma-separated lists.</summary>
    public static void Set(AppSettings settings, string path, string text)
    {
        var (owner, property) = Resolve(settings, path);
        if (!property.CanWrite)
            throw new ArgumentException($"'{path}' can't be set");
        property.SetValue(owner, Convert(text, property.PropertyType, path));
    }

    /// <summary>Replaces a value (including groups and lists, e.g. <c>pipelines</c>) with JSON.</summary>
    public static void SetJson(AppSettings settings, string path, string json)
    {
        var (owner, property) = Resolve(settings, path);
        if (!property.CanWrite)
            throw new ArgumentException($"'{path}' can't be set");
        try
        {
            property.SetValue(owner, JsonSerializer.Deserialize(json, property.PropertyType, SettingsStore.JsonOptions));
        }
        catch (JsonException e)
        {
            throw new ArgumentException($"That isn't valid JSON for {path}: {e.Message}");
        }
    }

    /// <summary>Every settable leaf path, for <c>wclop settings list</c>.</summary>
    public static IEnumerable<string> All(object? node = null, string prefix = "")
    {
        node ??= new AppSettings();
        foreach (var property in node.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanRead && p.CanWrite))
        {
            var name = prefix + JsonNamingPolicy.CamelCase.ConvertName(property.Name);
            var type = property.PropertyType;
            if (type.IsClass && type != typeof(string) && !type.IsGenericType && property.GetValue(node) is { } child)
            {
                foreach (var nested in All(child, name + "."))
                    yield return nested;
            }
            else
            {
                yield return name;
            }
        }
    }

    private static (object Owner, PropertyInfo Property) Resolve(AppSettings settings, string path)
    {
        object current = settings;
        var parts = path.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            throw new ArgumentException("No setting name given");

        for (var i = 0; i < parts.Length; i++)
        {
            var property = current.GetType().GetProperty(parts[i],
                               BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)
                           ?? throw new ArgumentException($"Unknown setting '{string.Join('.', parts[..(i + 1)])}'");
            if (i == parts.Length - 1)
                return (current, property);
            current = property.GetValue(current) ?? throw new ArgumentException($"'{path}' isn't set");
        }

        throw new InvalidOperationException();
    }

    private static object? Convert(string text, Type type, string path)
    {
        var underlying = Nullable.GetUnderlyingType(type);
        if (underlying is not null)
            return text.Length == 0 || text.Equals("null", StringComparison.OrdinalIgnoreCase) ? null : Convert(text, underlying, path);

        try
        {
            if (type == typeof(string))
                return text;
            if (type == typeof(bool))
                return text.ToLowerInvariant() switch
                {
                    "true" or "on" or "yes" or "1" => true,
                    "false" or "off" or "no" or "0" => false,
                    _ => throw new FormatException(),
                };
            if (type.IsEnum)
                return Enum.Parse(type, text, ignoreCase: true);
            if (type == typeof(List<string>))
                return text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            if (type == typeof(int) || type == typeof(long) || type == typeof(double))
                return System.Convert.ChangeType(text, type, CultureInfo.InvariantCulture);
        }
        catch (Exception e) when (e is FormatException or ArgumentException or OverflowException)
        {
            throw new ArgumentException($"'{text}' isn't a valid value for {path} ({Describe(type)})");
        }

        throw new ArgumentException($"{path} is a group of settings; set one of its entries instead");
    }

    private static string Describe(Type type) => type.IsEnum
        ? "one of " + string.Join(", ", Enum.GetNames(type))
        : type == typeof(bool) ? "true or false" : type.Name.ToLowerInvariant();
}
