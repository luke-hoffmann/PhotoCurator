using System.Text.Json;

namespace PhotoCurator.Core;

/// <summary>
/// The app's own memory, kept in its data folder (never in the photo folder): recent folders, folders whose
/// "make a copy first" warning was dismissed for good, and the last export destination per folder.
/// </summary>
public sealed class AppSettings
{
    const int MaxRecent = 8;
    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public List<string> Recent { get; set; } = new();
    public List<string> Acknowledged { get; set; } = new();
    public Dictionary<string, string> ExportDestinations { get; set; } = new();

    string? _file;

    public static AppSettings Load(string dataDir)
    {
        var file = Path.Combine(dataDir, "settings.json");
        AppSettings settings;
        try
        {
            settings = File.Exists(file) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(file), JsonOptions) ?? new() : new();
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            settings = new AppSettings();
        }
        settings._file = file;
        return settings;
    }

    public void Save()
    {
        if (_file is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            var temp = _file + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, JsonOptions));
            File.Move(temp, _file, overwrite: true);
        }
        catch (IOException)
        {
            // Settings are a convenience; never fail an action over them.
        }
    }

    public bool IsAcknowledged(string path) => Acknowledged.Contains(path, PathGuard.Comparer);

    public void Acknowledge(string path)
    {
        if (!IsAcknowledged(path)) Acknowledged.Add(path);
    }

    public void AddRecent(string path)
    {
        Recent.RemoveAll(p => PathGuard.Comparer.Equals(p, path));
        Recent.Insert(0, path);
        if (Recent.Count > MaxRecent) Recent.RemoveRange(MaxRecent, Recent.Count - MaxRecent);
    }
}
