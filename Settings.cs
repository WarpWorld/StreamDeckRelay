using System.Text.Json;

namespace StreamDeckRelay;

internal enum RelayMode
{
    Auto,
    Host,
    Client,
}

internal sealed class Settings
{
    public RelayMode Mode { get; set; } = RelayMode.Auto;
    public int Port { get; set; } = RelayCore.DefaultPort;
    /// <summary>Manual host address for when LAN discovery can't find the host PC.</summary>
    public string? ManualHostAddress { get; set; }
    /// <summary>False until the first-run setup window has been confirmed once.</summary>
    public bool SetupCompleted { get; set; }

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "StreamDeckRelay", "settings.json");

    public static Settings Load()
    {
        try
        {
            return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch
        {
            return new Settings();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
