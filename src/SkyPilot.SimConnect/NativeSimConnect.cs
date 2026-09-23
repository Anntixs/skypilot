using System.Reflection;
using System.Runtime.InteropServices;

namespace SkyPilot.SimConnect;

/// <summary>
/// P/Invoke bindings for the SimConnect C API (SimConnect.h from the MSFS SDK).
/// SimConnect.dll is loaded at run time, so SkyPilot builds without the SDK installed.
/// All SimConnect structures are packed on 1 byte.
/// </summary>
internal static class Native
{
    private const string Dll = "SimConnect.dll";

    public const uint ObjectIdUser = 0;
    public const uint Unused = 0xFFFFFFFF;

    public enum DataType : uint { Int32 = 1, Int64 = 2, Float32 = 3, Float64 = 4 }
    public enum Period : uint { Never = 0, Once = 1, VisualFrame = 2, SimFrame = 3, Second = 4 }
    public const uint EventFlagGroupIdIsPriority = 0x10;
    public const uint GroupPriorityHighest = 1;

    public enum RecvId : uint
    {
        Null = 0, Exception = 1, Open = 2, Quit = 3, Event = 4, SimObjectData = 8, AssignedObjectId = 12,
    }

    public const uint ExceptionCreateObjectFailed = 22;

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct Recv
    {
        public uint Size;
        public uint Version;
        public RecvId Id;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct RecvException
    {
        public Recv Header;
        public uint Exception;
        public uint SendId;
        public uint Index;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct RecvAssignedObjectId
    {
        public Recv Header;
        public uint RequestId;
        public uint ObjectId;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct RecvSimObjectData
    {
        public Recv Header;
        public uint RequestId;
        public uint ObjectId;
        public uint DefineId;
        public uint Flags;
        public uint EntryNumber;
        public uint OutOf;
        public uint DefineCount;
        // followed by the data
    }

    public static readonly int SimObjectDataOffset = Marshal.SizeOf<RecvSimObjectData>();

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct InitPosition
    {
        public double Latitude;
        public double Longitude;
        public double Altitude;
        public double Pitch;
        public double Bank;
        public double Heading;
        public uint OnGround;
        public uint Airspeed;
    }

    [DllImport(Dll, EntryPoint = "SimConnect_Open", CharSet = CharSet.Ansi)]
    public static extern int Open(out IntPtr handle, string name, IntPtr hWnd, uint userEventWin32, IntPtr eventHandle, uint configIndex);

    [DllImport(Dll, EntryPoint = "SimConnect_Close")]
    public static extern int Close(IntPtr handle);

    [DllImport(Dll, EntryPoint = "SimConnect_AddToDataDefinition", CharSet = CharSet.Ansi)]
    public static extern int AddToDataDefinition(IntPtr handle, uint defineId, string datumName, string? unitsName,
        DataType datumType, float epsilon, uint datumId);

    [DllImport(Dll, EntryPoint = "SimConnect_RequestDataOnSimObject")]
    public static extern int RequestDataOnSimObject(IntPtr handle, uint requestId, uint defineId, uint objectId,
        Period period, uint flags, uint origin, uint interval, uint limit);

    [DllImport(Dll, EntryPoint = "SimConnect_SetDataOnSimObject")]
    public static extern int SetDataOnSimObject(IntPtr handle, uint defineId, uint objectId, uint flags,
        uint arrayCount, uint unitSize, IntPtr data);

    [DllImport(Dll, EntryPoint = "SimConnect_MapClientEventToSimEvent", CharSet = CharSet.Ansi)]
    public static extern int MapClientEventToSimEvent(IntPtr handle, uint eventId, string eventName);

    [DllImport(Dll, EntryPoint = "SimConnect_TransmitClientEvent")]
    public static extern int TransmitClientEvent(IntPtr handle, uint objectId, uint eventId, uint data, uint groupId, uint flags);

    [DllImport(Dll, EntryPoint = "SimConnect_AICreateNonATCAircraft", CharSet = CharSet.Ansi)]
    public static extern int AICreateNonATCAircraft(IntPtr handle, string containerTitle, string tailNumber,
        InitPosition initPos, uint requestId);

    [DllImport(Dll, EntryPoint = "SimConnect_AIRemoveObject")]
    public static extern int AIRemoveObject(IntPtr handle, uint objectId, uint requestId);

    [DllImport(Dll, EntryPoint = "SimConnect_AIReleaseControl")]
    public static extern int AIReleaseControl(IntPtr handle, uint objectId, uint requestId);

    [DllImport(Dll, EntryPoint = "SimConnect_GetLastSentPacketID")]
    public static extern int GetLastSentPacketId(IntPtr handle, out uint sendId);

    [DllImport(Dll, EntryPoint = "SimConnect_GetNextDispatch")]
    public static extern int GetNextDispatch(IntPtr handle, out IntPtr data, out uint size);

    // ---- locating SimConnect.dll ------------------------------------------------------

    private static string? _loadedFrom;
    private static int _resolverInstalled;

    /// <summary>Where SimConnect.dll was loaded from, or null if it has not been found.</summary>
    public static string? LoadedFrom => _loadedFrom;

    /// <summary>Candidate locations, in order: next to SkyPilot.exe, then the MSFS 2024/2020 SDK.</summary>
    public static IEnumerable<string> Candidates()
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

    /// <summary>Load SimConnect.dll. Returns false if it cannot be found.</summary>
    public static bool TryLoad()
    {
        if (_loadedFrom != null) return true;
        if (Interlocked.Exchange(ref _resolverInstalled, 1) == 0)
            NativeLibrary.SetDllImportResolver(typeof(Native).Assembly, Resolve);
        return Resolve(Dll, typeof(Native).Assembly, null) != IntPtr.Zero;
    }

    private static IntPtr _module;

    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!name.Equals(Dll, StringComparison.OrdinalIgnoreCase)) return IntPtr.Zero;
        if (_module != IntPtr.Zero) return _module;
        foreach (var path in Candidates())
        {
            if (File.Exists(path) && NativeLibrary.TryLoad(path, out var module))
            {
                _module = module;
                _loadedFrom = path;
                return module;
            }
        }
        return IntPtr.Zero;
    }
}
