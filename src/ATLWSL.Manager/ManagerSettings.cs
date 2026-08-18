using System.IO;
using System.Text.Json;
using System.IO;

namespace AtlWsl.Manager;

internal sealed record ManagerSettings(string DistributionName)
{
    private static readonly string SettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ATL-WSL Manager");

    private static readonly string SettingsPath = Path.Combine(SettingsDirectory, "settings.json");

    public static ManagerSettings Load()
    {
        try
        {
            var settings = JsonSerializer.Deserialize<ManagerSettings>(File.ReadAllText(SettingsPath));
            return settings is { DistributionName.Length: > 0 } ? settings : new("ATL-WSL");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return new("ATL-WSL");
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(SettingsDirectory);
        var temporaryPath = SettingsPath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(this));
        File.Move(temporaryPath, SettingsPath, overwrite: true);
    }
}
