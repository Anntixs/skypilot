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
    /// <summary>SkyNetwork website, where flight plans are filed.</summary>
    public string Website { get; set; } = "http://127.0.0.1:8000/";
    /// <summary>SimBrief username or numeric Pilot ID: SIMBRIEF loads the latest plan made there.</summary>
    public string SimbriefUser { get; set; } = "";
    public List<ServerEntry> Servers { get; set; } = [new ServerEntry()];
    public string SelectedServer { get; set; } = "SKYNET";
    public string LastCallsign { get; set; } = "";
    public string LastTypeCode { get; set; } = "";
    public bool PlaySoundOnPrivateMessage { get; set; } = true;
    public bool KeepWindowOnTop { get; set; }

    /// <summary>UDP port of the voice server; it runs on the same host as the FSD server.</summary>
    public int VoicePort { get; set; } = 3782;
    /// <summary>Microphone and speakers by device name; empty = Windows default.</summary>
    public string InputDevice { get; set; } = "";
    public string OutputDevice { get; set; } = "";
    /// <summary>Microphone gain and receive volume (1 = 100 %).</summary>
    public double MicGain { get; set; } = 1;
    public double OutputVolume { get; set; } = 1;
    /// <summary>Push-to-talk key or joystick button, e.g. "key:162" or "joy:0:4"; empty = none.</summary>
    public string PttKey { get; set; } = "";

    /// <summary>Which simulator to use: "auto", "msfs", "p3d" or "xplane" (see SimulatorKind).</summary>
    public string Simulator { get; set; } = "auto";
    /// <summary>Prepar3D's SimConnect.dll when it is not found by itself; empty = look for it.</summary>
    public string P3dSimConnectPath { get; set; } = "";

    /// <summary>MSFS Community folder with FSLTL. Empty: found automatically from UserCfg.opt.</summary>
    public string CommunityFolder { get; set; } = "";

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
