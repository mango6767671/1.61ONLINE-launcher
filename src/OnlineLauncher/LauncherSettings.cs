using System.Text.Json;
using System.Text.Json.Serialization;

namespace OnlineLauncher;

public sealed class LauncherSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public string Environment { get; set; } = "Local";
    public string LocalApiUrl { get; set; } = "http://127.0.0.1:3000/";
    public string PublicApiUrl { get; set; } = "";
    public string GameInstallPath { get; set; } = "";

    [JsonIgnore]
    public string ActiveApiUrl => string.Equals(Environment, "Public", StringComparison.OrdinalIgnoreCase)
        ? PublicApiUrl.Trim()
        : LocalApiUrl.Trim();

    public static string SettingsPath => Path.Combine(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
        "1.61ONLINE", "launcher-settings.json");

    public static LauncherSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var loaded = JsonSerializer.Deserialize<LauncherSettings>(File.ReadAllText(SettingsPath));
                if (loaded is not null)
                {
                    loaded.Environment = string.Equals(loaded.Environment, "Public", StringComparison.OrdinalIgnoreCase) ? "Public" : "Local";
                    loaded.LocalApiUrl = string.IsNullOrWhiteSpace(loaded.LocalApiUrl) ? "http://127.0.0.1:3000/" : loaded.LocalApiUrl.Trim();
                    loaded.PublicApiUrl = loaded.PublicApiUrl?.Trim() ?? "";
                    loaded.GameInstallPath = loaded.GameInstallPath?.Trim() ?? "";
                    return loaded;
                }
            }
        }
        catch (IOException) { }
        catch (JsonException) { }
        catch (UnauthorizedAccessException) { }

        return new LauncherSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        var temporaryPath = SettingsPath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(temporaryPath, SettingsPath, true);
    }

    public string ValidateActiveEndpoint()
    {
        if (!Uri.TryCreate(ActiveApiUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return "Enter a valid absolute API URL, including https:// or http://.";

        if (!string.IsNullOrWhiteSpace(uri.UserInfo))
            return "Do not put usernames or passwords in the API URL. Use a URL without embedded credentials.";

        if (string.Equals(Environment, "Public", StringComparison.OrdinalIgnoreCase) && uri.Scheme != Uri.UriSchemeHttps)
            return "Public mode requires HTTPS so account credentials and tokens are not sent over plain HTTP.";

        if (string.Equals(Environment, "Local", StringComparison.OrdinalIgnoreCase) &&
            uri.Scheme == Uri.UriSchemeHttp && !uri.IsLoopback)
            return "Local mode only allows plain HTTP for a loopback address such as 127.0.0.1. Use HTTPS for other hosts.";

        return "";
    }
}
