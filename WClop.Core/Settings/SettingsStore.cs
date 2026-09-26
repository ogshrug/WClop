using System.Text.Json;
using System.Text.Json.Serialization;

namespace WClop.Core.Settings;

/// <summary>Loads and saves <see cref="AppSettings"/> as JSON.</summary>
public sealed class SettingsStore(string path)
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public string Path { get; } = path;

    /// <summary>
    /// Returns the saved settings, or defaults if the file doesn't exist.
    /// A corrupt file is moved aside to <c>settings.json.bad</c> rather than overwritten, so it can be recovered.
    /// </summary>
    public AppSettings Load()
    {
        if (!File.Exists(Path))
            return new AppSettings();

        try
        {
            using var stream = File.OpenRead(Path);
            return JsonSerializer.Deserialize<AppSettings>(stream, JsonOptions) ?? new AppSettings();
        }
        catch (JsonException)
        {
            File.Move(Path, Path + ".bad", overwrite: true);
            return new AppSettings();
        }
    }

    /// <summary>
    /// Writes to a temp file then swaps it in, so a crash mid-write can't leave a truncated file.
    /// Safe to call from several threads (the tray and the watchers both save).
    /// </summary>
    public void Save(AppSettings settings)
    {
        lock (_saveGate)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);

            var tempPath = Path + ".tmp";
            using (var stream = File.Create(tempPath))
                JsonSerializer.Serialize(stream, settings, JsonOptions);

            File.Move(tempPath, Path, overwrite: true);
        }
    }

    private readonly object _saveGate = new();

    /// <summary>The settings as they'd be saved, for detecting whether anything actually changed.</summary>
    public static string Snapshot(AppSettings settings) => JsonSerializer.Serialize(settings, JsonOptions);
}
