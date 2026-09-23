using System.Text.Json;

namespace SkyPilot.Core.Settings;

/// <summary>Encrypts stored passwords (DPAPI on Windows).</summary>
public interface ISecretProtector
{
    string Protect(string plain);
    string Unprotect(string protectedValue);
}

public sealed class PlainTextProtector : ISecretProtector
{
    public string Protect(string plain) => plain;
    public string Unprotect(string protectedValue) => protectedValue;
}

public sealed class ServerEntry
{
    public string Name { get; set; } = "SKYNET";
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 6809;
    public override string ToString() => $"{Name} ({Host}:{Port})";
}

public sealed class AppSettings
{
    public int Cid { get; set; }
    public string ProtectedPassword { get; set; } = "";
    public string RealName { get; set; } = "";
    public string HomeAirport { get; set; } = "";
    public List<ServerEntry> Servers { get; set; } = [new ServerEntry()];
    public string SelectedServer { get; set; } = "SKYNET";
    public string LastCallsign { get; set; } = "";
    public string LastTypeCode { get; set; } = "";
    public bool PlaySoundOnPrivateMessage { get; set; } = true;
    public bool KeepWindowOnTop { get; set; }

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SkyPilot");

    public static AppSettings Load(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Options) ?? new AppSettings();
        }
        catch (JsonException)
        {
            // Corrupt file: start from defaults rather than refusing to start.
        }
        return new AppSettings();
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Options));
    }

    public ServerEntry CurrentServer =>
        Servers.FirstOrDefault(s => s.Name == SelectedServer) ?? Servers.FirstOrDefault() ?? new ServerEntry();
}
