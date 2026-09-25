using Microsoft.Win32;

namespace SkyPilot.SimConnect;

/// <summary>
/// What differs between the SimConnect simulators: the name, where their SimConnect.dll is, and how a COM frequency
/// is set (MSFS takes Hz, Prepar3D the old BCD value).
/// </summary>
public sealed class SimConnectFlavor
{
    private readonly Func<IEnumerable<string>> _candidates;

    private SimConnectFlavor(string name, bool comHz, string missingDll, Func<IEnumerable<string>> candidates)
    {
        Name = name;
        ComHz = comHz;
        MissingDll = missingDll;
        _candidates = candidates;
    }

    public string Name { get; }
    /// <summary>True: COM_RADIO_SET_HZ (8.33 kHz); false: COM_RADIO_SET with a BCD frequency.</summary>
    public bool ComHz { get; }
    /// <summary>What to tell the user when no SimConnect.dll was found.</summary>
    public string MissingDll { get; }

    /// <summary>Where to look for SimConnect.dll, in order.</summary>
    public IEnumerable<string> Candidates() => _candidates();

    private const string Dll = "SimConnect.dll";

    /// <summary>Microsoft Flight Simulator 2020 / 2024: SimConnect.dll next to SkyPilot.exe, then the MSFS 2024/2020 SDK.</summary>
    public static SimConnectFlavor Msfs { get; } = new("Microsoft Flight Simulator", comHz: true,
        "SimConnect.dll for MSFS not found: put it next to SkyPilot.exe (from the MSFS SDK: SimConnect SDK\\lib)",
        MsfsCandidates);

    private static IEnumerable<string> MsfsCandidates()
    {
        yield return Path.Combine(AppContext.BaseDirectory, Dll);
        foreach (var env in new[] { "MSFS2024_SDK", "MSFS_SDK" })
        {
            var sdk = Environment.GetEnvironmentVariable(env);
            if (!string.IsNullOrEmpty(sdk)) yield return Path.Combine(sdk, "SimConnect SDK", "lib", Dll);
        }
        yield return @"C:\MSFS 2024 SDK\SimConnect SDK\lib\" + Dll;
        yield return @"C:\MSFS SDK\SimConnect SDK\lib\" + Dll;
    }

    /// <summary>
    /// Prepar3D v4, v5 or v6 (64-bit): the SimConnect.dll the user chose, then SkyPilot\p3d\SimConnect.dll, then
    /// the Prepar3D installation and SDK found in the registry.
    /// </summary>
    public static SimConnectFlavor Prepar3D(Func<string?>? chosenDll = null) => new("Prepar3D", comHz: false,
        "SimConnect.dll for Prepar3D not found: copy it from the Prepar3D SDK (lib\\SimConnect) into the p3d folder next to SkyPilot.exe, or choose it in the settings",
        () => Prepar3DCandidates(chosenDll?.Invoke()));

    private static IEnumerable<string> Prepar3DCandidates(string? chosenDll)
    {
        if (!string.IsNullOrWhiteSpace(chosenDll)) yield return chosenDll;
        yield return Path.Combine(AppContext.BaseDirectory, "p3d", Dll);
        foreach (var root in Prepar3DFolders())
            foreach (var dll in FindDll(root, 5))
                yield return dll;
    }

    /// <summary>Installation and SDK folders of Prepar3D v6, v5 and v4 from the registry, newest first.</summary>
    public static IEnumerable<string> Prepar3DFolders()
    {
        if (!OperatingSystem.IsWindows()) yield break;
        foreach (var version in new[] { "v6", "v5", "v4" })
            foreach (var key in new[] { $@"SOFTWARE\Lockheed Martin\Prepar3D {version}", $@"SOFTWARE\Lockheed Martin\Prepar3D {version} SDK" })
                foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
                {
                    string? path = null;
                    try
                    {
                        using var k = hive.OpenSubKey(key);
                        path = k?.GetValue("SetupPath") as string;
                    }
                    catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException)
                    {
                    }
                    if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path)) yield return path;
                }
    }

    /// <summary>SimConnect.dll files under <paramref name="root"/> (64-bit folders first), at most <paramref name="depth"/> levels down.</summary>
    internal static IEnumerable<string> FindDll(string root, int depth)
    {
        List<string> found = [];
        void Walk(string dir, int level)
        {
            string[] files, dirs;
            try
            {
                files = Directory.GetFiles(dir, Dll);
                dirs = level < depth ? Directory.GetDirectories(dir) : [];
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return;
            }
            found.AddRange(files);
            foreach (var d in dirs)
            {
                var name = Path.GetFileName(d);
                // Scenery and texture folders are huge and never hold the library.
                if (name.Equals("Scenery", StringComparison.OrdinalIgnoreCase) || name.Equals("Texture", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("SimObjects", StringComparison.OrdinalIgnoreCase) || name.Equals("Effects", StringComparison.OrdinalIgnoreCase))
                    continue;
                Walk(d, level + 1);
            }
        }
        Walk(root, 0);
        return found.OrderBy(p => p.Contains("x86", StringComparison.OrdinalIgnoreCase) || p.Contains("32", StringComparison.Ordinal) ? 1 : 0);
    }
}
