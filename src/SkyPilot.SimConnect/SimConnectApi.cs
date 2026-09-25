using System.Runtime.InteropServices;

namespace SkyPilot.SimConnect;

/// <summary>
/// The SimConnect C API (SimConnect.h), loaded from a given SimConnect.dll at run time. Each simulator family ships
/// its own client library (MSFS and Prepar3D cannot talk to each other's), so every <see cref="SimConnectApi"/> holds
/// the functions of one DLL. All SimConnect structures are packed on 1 byte.
/// </summary>
internal sealed unsafe class SimConnectApi
{
    public const uint ObjectIdUser = 0;
    public const uint Unused = 0xFFFFFFFF;

    public enum DataType : uint { Int32 = 1, Int64 = 2, Float32 = 3, Float64 = 4, String256 = 9 }
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

    private readonly delegate* unmanaged[Stdcall]<IntPtr*, byte*, IntPtr, uint, IntPtr, uint, int> _open;
    private readonly delegate* unmanaged[Stdcall]<IntPtr, int> _close;
    private readonly delegate* unmanaged[Stdcall]<IntPtr, uint, byte*, byte*, DataType, float, uint, int> _addToDataDefinition;
    private readonly delegate* unmanaged[Stdcall]<IntPtr, uint, uint, uint, Period, uint, uint, uint, uint, int> _requestDataOnSimObject;
    private readonly delegate* unmanaged[Stdcall]<IntPtr, uint, uint, uint, uint, uint, IntPtr, int> _setDataOnSimObject;
    private readonly delegate* unmanaged[Stdcall]<IntPtr, uint, byte*, int> _mapClientEventToSimEvent;
    private readonly delegate* unmanaged[Stdcall]<IntPtr, uint, uint, uint, uint, uint, int> _transmitClientEvent;
    private readonly delegate* unmanaged[Stdcall]<IntPtr, byte*, byte*, InitPosition, uint, int> _aiCreateNonAtcAircraft;
    private readonly delegate* unmanaged[Stdcall]<IntPtr, uint, uint, int> _aiRemoveObject;
    private readonly delegate* unmanaged[Stdcall]<IntPtr, uint, uint, int> _aiReleaseControl;
    private readonly delegate* unmanaged[Stdcall]<IntPtr, uint*, int> _getLastSentPacketId;
    private readonly delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, uint*, int> _getNextDispatch;

    /// <summary>Where the library was loaded from.</summary>
    public string Path { get; }

    private SimConnectApi(string path, IntPtr module)
    {
        Path = path;
        IntPtr F(string name) => NativeLibrary.GetExport(module, name);
        _open = (delegate* unmanaged[Stdcall]<IntPtr*, byte*, IntPtr, uint, IntPtr, uint, int>)F("SimConnect_Open");
        _close = (delegate* unmanaged[Stdcall]<IntPtr, int>)F("SimConnect_Close");
        _addToDataDefinition = (delegate* unmanaged[Stdcall]<IntPtr, uint, byte*, byte*, DataType, float, uint, int>)F("SimConnect_AddToDataDefinition");
        _requestDataOnSimObject = (delegate* unmanaged[Stdcall]<IntPtr, uint, uint, uint, Period, uint, uint, uint, uint, int>)F("SimConnect_RequestDataOnSimObject");
        _setDataOnSimObject = (delegate* unmanaged[Stdcall]<IntPtr, uint, uint, uint, uint, uint, IntPtr, int>)F("SimConnect_SetDataOnSimObject");
        _mapClientEventToSimEvent = (delegate* unmanaged[Stdcall]<IntPtr, uint, byte*, int>)F("SimConnect_MapClientEventToSimEvent");
        _transmitClientEvent = (delegate* unmanaged[Stdcall]<IntPtr, uint, uint, uint, uint, uint, int>)F("SimConnect_TransmitClientEvent");
        _aiCreateNonAtcAircraft = (delegate* unmanaged[Stdcall]<IntPtr, byte*, byte*, InitPosition, uint, int>)F("SimConnect_AICreateNonATCAircraft");
        _aiRemoveObject = (delegate* unmanaged[Stdcall]<IntPtr, uint, uint, int>)F("SimConnect_AIRemoveObject");
        _aiReleaseControl = (delegate* unmanaged[Stdcall]<IntPtr, uint, uint, int>)F("SimConnect_AIReleaseControl");
        _getLastSentPacketId = (delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)F("SimConnect_GetLastSentPacketID");
        _getNextDispatch = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, uint*, int>)F("SimConnect_GetNextDispatch");
    }

    /// <summary>The first of <paramref name="candidates"/> that exists and loads, or null (with the reason in <paramref name="error"/>).</summary>
    public static SimConnectApi? Load(IEnumerable<string> candidates, out string? error)
    {
        error = null;
        foreach (var path in candidates)
        {
            if (!File.Exists(path)) continue;
            if (!NativeLibrary.TryLoad(path, out var module))
            {
                error = $"Cannot load {path} (a 64-bit SimConnect.dll is needed)";
                continue;
            }
            try
            {
                return new SimConnectApi(path, module);
            }
            catch (EntryPointNotFoundException e)
            {
                error = $"{path} is not a SimConnect library: {e.Message}";
            }
        }
        return null;
    }

    /// <summary>A zero-terminated single-byte copy of <paramref name="s"/> for the duration of a call.</summary>
    private static byte[]? Ansi(string? s) => s == null ? null : [.. System.Text.Encoding.Latin1.GetBytes(s), 0];

    public int Open(out IntPtr handle, string name, IntPtr eventHandle)
    {
        IntPtr h;
        int hr;
        fixed (byte* n = Ansi(name)) hr = _open(&h, n, IntPtr.Zero, 0, eventHandle, 0);
        handle = h;
        return hr;
    }

    public int Close(IntPtr handle) => _close(handle);

    public int AddToDataDefinition(IntPtr handle, uint defineId, string datumName, string? unitsName, DataType type)
    {
        fixed (byte* n = Ansi(datumName))
        fixed (byte* u = Ansi(unitsName))
            return _addToDataDefinition(handle, defineId, n, u, type, 0, Unused);
    }

    public int RequestDataOnSimObject(IntPtr handle, uint requestId, uint defineId, uint objectId, Period period, uint interval) =>
        _requestDataOnSimObject(handle, requestId, defineId, objectId, period, 0, 0, interval, 0);

    public int SetDataOnSimObject(IntPtr handle, uint defineId, uint objectId, uint unitSize, IntPtr data) =>
        _setDataOnSimObject(handle, defineId, objectId, 0, 0, unitSize, data);

    public int MapClientEventToSimEvent(IntPtr handle, uint eventId, string eventName)
    {
        fixed (byte* n = Ansi(eventName)) return _mapClientEventToSimEvent(handle, eventId, n);
    }

    public int TransmitClientEvent(IntPtr handle, uint objectId, uint eventId, uint data) =>
        _transmitClientEvent(handle, objectId, eventId, data, GroupPriorityHighest, EventFlagGroupIdIsPriority);

    public int AICreateNonATCAircraft(IntPtr handle, string containerTitle, string tailNumber, InitPosition init, uint requestId)
    {
        fixed (byte* t = Ansi(containerTitle))
        fixed (byte* tail = Ansi(tailNumber))
            return _aiCreateNonAtcAircraft(handle, t, tail, init, requestId);
    }

    public int AIRemoveObject(IntPtr handle, uint objectId, uint requestId) => _aiRemoveObject(handle, objectId, requestId);
    public int AIReleaseControl(IntPtr handle, uint objectId, uint requestId) => _aiReleaseControl(handle, objectId, requestId);

    public uint GetLastSentPacketId(IntPtr handle)
    {
        uint id;
        _getLastSentPacketId(handle, &id);
        return id;
    }

    public bool GetNextDispatch(IntPtr handle, out IntPtr data)
    {
        IntPtr d;
        uint size;
        int hr = _getNextDispatch(handle, &d, &size);
        data = d;
        return hr == 0 && d != IntPtr.Zero;
    }
}
