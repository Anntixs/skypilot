using System.Runtime.InteropServices;
using SkyPilot.Core.Model;
using SkyPilot.Core.Simulation;

namespace SkyPilot.SimConnect;

/// <summary>
/// Microsoft Flight Simulator 2020 / 2024 via the SimConnect C API.
/// A background thread pumps SimConnect messages; events are raised on that thread, but never
/// while the internal lock is held, so handlers may call back into this class.
/// </summary>
public sealed class MsfsSimulator : ISimulator
{
    private const uint DefOwnAircraft = 1;
    private const uint DefRemoteAircraft = 2;
    private const uint ReqOwnAircraft = 1;
    private const uint ReqAiRelease = 2;
    private const uint ReqAiRemove = 3;
    private const uint ReqCreateBase = 1000;
    private const uint EvtCom1SetHz = 1, EvtCom2SetHz = 2, EvtTransponderSet = 3;
    private const uint EvtFreezeLatLon = 4, EvtFreezeAltitude = 5, EvtFreezeAttitude = 6;

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct OwnAircraftStruct
    {
        public double Latitude;
        public double Longitude;
        public double Altitude;
        public double Pitch;
        public double Bank;
        public double Heading;
        public double GroundSpeed;
        public int OnGround;
        public double PressureAltitude;
        public double Com1Mhz;
        public double Com2Mhz;
        public int TransponderBco16;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct RemoteAircraftStruct
    {
        public double Latitude;
        public double Longitude;
        public double Altitude;
        public double Pitch;
        public double Bank;
        public double Heading;
    }

    private sealed class SimAircraft(string callsign, string title)
    {
        public string Callsign { get; } = callsign;
        public string Title { get; } = title;
        public uint? ObjectId { get; set; }
        public uint RequestId { get; set; }
        public uint SendId { get; set; }
    }

    private readonly object _lock = new();
    private readonly Dictionary<string, SimAircraft> _aircraft = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<uint, SimAircraft> _byRequest = [];
    private readonly List<Action> _pendingEvents = [];
    private IntPtr _handle;
    private AutoResetEvent? _signal;
    private Thread? _pump;
    private volatile bool _connected;
    private uint _nextRequest = ReqCreateBase;

    public string Name => "Microsoft Flight Simulator";
    public bool IsConnected => _connected;

    /// <summary>Why the last <see cref="Connect"/> failed, for the status bar.</summary>
    public string? LastError { get; private set; }

    public event EventHandler<bool>? ConnectionChanged;
    public event EventHandler<OwnAircraftData>? OwnAircraftUpdated;
    public event EventHandler<AircraftCreateFailedEventArgs>? AircraftCreateFailed;

    public bool Connect()
    {
        lock (_lock)
        {
            if (_handle != IntPtr.Zero) return true;
            if (!Native.TryLoad())
            {
                LastError = "SimConnect.dll not found — place it next to SkyPilot.exe (from the MSFS SDK: SimConnect SDK\\lib)";
                return false;
            }
            var signal = new AutoResetEvent(false);
            int hr;
            try
            {
                hr = Native.Open(out _handle, "SkyPilot", IntPtr.Zero, 0, signal.SafeWaitHandle.DangerousGetHandle(), 0);
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            {
                signal.Dispose();
                LastError = "Could not load SimConnect.dll: " + e.Message;
                return false;
            }
            if (hr != 0 || _handle == IntPtr.Zero)
            {
                _handle = IntPtr.Zero;
                signal.Dispose();
                LastError = null; // simulator is simply not running
                return false;
            }
            LastError = null;
            _signal = signal;
            _pump = new Thread(PumpLoop) { IsBackground = true, Name = "SimConnect" };
            _pump.Start();
            return true;
        }
    }

    public void Disconnect()
    {
        bool wasConnected;
        lock (_lock)
        {
            if (_handle == IntPtr.Zero) return;
            Native.Close(_handle);
            _handle = IntPtr.Zero;
            _aircraft.Clear();
            _byRequest.Clear();
            wasConnected = _connected;
            _connected = false;
            _signal?.Set();
        }
        if (wasConnected) ConnectionChanged?.Invoke(this, false);
    }

    private void PumpLoop()
    {
        while (true)
        {
            var signal = _signal;
            if (signal == null) return;
            try { signal.WaitOne(250); } catch (ObjectDisposedException) { return; }
            lock (_lock)
            {
                if (_handle == IntPtr.Zero) return;
                while (Native.GetNextDispatch(_handle, out var data, out _) == 0 && data != IntPtr.Zero)
                {
                    Dispatch(data);
                    if (_handle == IntPtr.Zero) break;
                }
            }
            FlushEvents();
        }
    }

    /// <summary>Queue an event to raise after the lock is released.</summary>
    private void Defer(Action action) => _pendingEvents.Add(action);

    private void FlushEvents()
    {
        List<Action> events;
        lock (_lock)
        {
            if (_pendingEvents.Count == 0) return;
            events = [.. _pendingEvents];
            _pendingEvents.Clear();
        }
        foreach (var e in events) e();
    }

    private void Dispatch(IntPtr data)
    {
        var header = Marshal.PtrToStructure<Native.Recv>(data);
        switch (header.Id)
        {
            case Native.RecvId.Open:
                OnOpen();
                break;
            case Native.RecvId.Quit:
                Defer(Disconnect);
                break;
            case Native.RecvId.SimObjectData:
                OnSimObjectData(data);
                break;
            case Native.RecvId.AssignedObjectId:
                OnAssignedObjectId(Marshal.PtrToStructure<Native.RecvAssignedObjectId>(data));
                break;
            case Native.RecvId.Exception:
                OnException(Marshal.PtrToStructure<Native.RecvException>(data));
                break;
        }
    }

    private void OnOpen()
    {
        void Add(uint def, string name, string units, Native.DataType type = Native.DataType.Float64) =>
            Native.AddToDataDefinition(_handle, def, name, units, type, 0, Native.Unused);

        Add(DefOwnAircraft, "PLANE LATITUDE", "degrees");
        Add(DefOwnAircraft, "PLANE LONGITUDE", "degrees");
        Add(DefOwnAircraft, "PLANE ALTITUDE", "feet");
        Add(DefOwnAircraft, "PLANE PITCH DEGREES", "degrees");
        Add(DefOwnAircraft, "PLANE BANK DEGREES", "degrees");
        Add(DefOwnAircraft, "PLANE HEADING DEGREES TRUE", "degrees");
        Add(DefOwnAircraft, "GROUND VELOCITY", "knots");
        Add(DefOwnAircraft, "SIM ON GROUND", "bool", Native.DataType.Int32);
        Add(DefOwnAircraft, "PRESSURE ALTITUDE", "feet");
        Add(DefOwnAircraft, "COM ACTIVE FREQUENCY:1", "MHz");
        Add(DefOwnAircraft, "COM ACTIVE FREQUENCY:2", "MHz");
        Add(DefOwnAircraft, "TRANSPONDER CODE:1", "Bco16", Native.DataType.Int32);

        Add(DefRemoteAircraft, "PLANE LATITUDE", "degrees");
        Add(DefRemoteAircraft, "PLANE LONGITUDE", "degrees");
        Add(DefRemoteAircraft, "PLANE ALTITUDE", "feet");
        Add(DefRemoteAircraft, "PLANE PITCH DEGREES", "degrees");
        Add(DefRemoteAircraft, "PLANE BANK DEGREES", "degrees");
        Add(DefRemoteAircraft, "PLANE HEADING DEGREES TRUE", "degrees");

        Native.MapClientEventToSimEvent(_handle, EvtCom1SetHz, "COM_RADIO_SET_HZ");
        Native.MapClientEventToSimEvent(_handle, EvtCom2SetHz, "COM2_RADIO_SET_HZ");
        Native.MapClientEventToSimEvent(_handle, EvtTransponderSet, "XPNDR_SET");
        Native.MapClientEventToSimEvent(_handle, EvtFreezeLatLon, "FREEZE_LATITUDE_LONGITUDE_SET");
        Native.MapClientEventToSimEvent(_handle, EvtFreezeAltitude, "FREEZE_ALTITUDE_SET");
        Native.MapClientEventToSimEvent(_handle, EvtFreezeAttitude, "FREEZE_ATTITUDE_SET");

        // About 10 updates per second of the user's aircraft.
        Native.RequestDataOnSimObject(_handle, ReqOwnAircraft, DefOwnAircraft, Native.ObjectIdUser,
            Native.Period.SimFrame, 0, 0, 6, 0);

        _connected = true;
        Defer(() => ConnectionChanged?.Invoke(this, true));
    }

    private void OnSimObjectData(IntPtr data)
    {
        var header = Marshal.PtrToStructure<Native.RecvSimObjectData>(data);
        if (header.RequestId != ReqOwnAircraft) return;
        var s = Marshal.PtrToStructure<OwnAircraftStruct>(data + Native.SimObjectDataOffset);
        var own = new OwnAircraftData(
            new AircraftState(s.Latitude, s.Longitude, s.Altitude,
                PitchDegrees: -s.Pitch, BankDegrees: -s.Bank, s.Heading, s.GroundSpeed, s.OnGround != 0),
            s.PressureAltitude,
            (int)Math.Round(s.Com1Mhz * 1000),
            (int)Math.Round(s.Com2Mhz * 1000),
            FromBco16(s.TransponderBco16),
            TransponderOn: true);
        Defer(() => OwnAircraftUpdated?.Invoke(this, own));
    }

    private void OnAssignedObjectId(Native.RecvAssignedObjectId data)
    {
        if (!_byRequest.Remove(data.RequestId, out var ac)) return;
        if (!_aircraft.TryGetValue(ac.Callsign, out var current) || !ReferenceEquals(current, ac))
        {
            // Removed while it was being created.
            Native.AIRemoveObject(_handle, data.ObjectId, ReqAiRemove);
            return;
        }
        ac.ObjectId = data.ObjectId;
        Native.AIReleaseControl(_handle, data.ObjectId, ReqAiRelease);
        foreach (var e in new[] { EvtFreezeLatLon, EvtFreezeAltitude, EvtFreezeAttitude })
            Native.TransmitClientEvent(_handle, data.ObjectId, e, 1, Native.GroupPriorityHighest, Native.EventFlagGroupIdIsPriority);
    }

    private void OnException(Native.RecvException data)
    {
        if (data.Exception != Native.ExceptionCreateObjectFailed) return;
        var failed = _aircraft.Values.FirstOrDefault(a => a.ObjectId == null && a.SendId == data.SendId);
        if (failed == null) return;
        _aircraft.Remove(failed.Callsign);
        _byRequest.Remove(failed.RequestId);
        Defer(() => AircraftCreateFailed?.Invoke(this, new AircraftCreateFailedEventArgs(failed.Callsign, failed.Title)));
    }

    public void AddAircraft(string callsign, string modelTitle, AircraftState state)
    {
        lock (_lock)
        {
            if (_handle == IntPtr.Zero || !_connected) return;
            RemoveLocked(callsign);
            var ac = new SimAircraft(callsign, modelTitle) { RequestId = _nextRequest++ };
            var init = new Native.InitPosition
            {
                Latitude = state.Latitude,
                Longitude = state.Longitude,
                Altitude = state.AltitudeFeet,
                Pitch = -state.PitchDegrees,
                Bank = -state.BankDegrees,
                Heading = state.HeadingDegrees,
                OnGround = state.OnGround ? 1u : 0u,
                Airspeed = (uint)Math.Max(0, state.GroundSpeedKnots),
            };
            string tail = callsign.Length > 12 ? callsign[..12] : callsign;
            Native.AICreateNonATCAircraft(_handle, modelTitle, tail, init, ac.RequestId);
            Native.GetLastSentPacketId(_handle, out var sendId);
            ac.SendId = sendId;
            _aircraft[callsign] = ac;
            _byRequest[ac.RequestId] = ac;
        }
    }

    public void UpdateAircraft(string callsign, AircraftState state)
    {
        lock (_lock)
        {
            if (_handle == IntPtr.Zero || !_aircraft.TryGetValue(callsign, out var ac) || ac.ObjectId is not { } id) return;
            var data = new RemoteAircraftStruct
            {
                Latitude = state.Latitude,
                Longitude = state.Longitude,
                Altitude = state.AltitudeFeet,
                Pitch = -state.PitchDegrees,
                Bank = -state.BankDegrees,
                Heading = state.HeadingDegrees,
            };
            int size = Marshal.SizeOf<RemoteAircraftStruct>();
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(data, buffer, false);
                Native.SetDataOnSimObject(_handle, DefRemoteAircraft, id, 0, 0, (uint)size, buffer);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    public void RemoveAircraft(string callsign)
    {
        lock (_lock) RemoveLocked(callsign);
    }

    public void RemoveAllAircraft()
    {
        lock (_lock)
            foreach (var callsign in _aircraft.Keys.ToList())
                RemoveLocked(callsign);
    }

    private void RemoveLocked(string callsign)
    {
        if (!_aircraft.Remove(callsign, out var ac)) return;
        // Pending creations are cleaned up in OnAssignedObjectId.
        if (ac.ObjectId is { } id && _handle != IntPtr.Zero) Native.AIRemoveObject(_handle, id, ReqAiRemove);
    }

    public void SetComFrequency(int radio, int khz)
    {
        lock (_lock)
        {
            if (_handle == IntPtr.Zero) return;
            Native.TransmitClientEvent(_handle, Native.ObjectIdUser, radio == 2 ? EvtCom2SetHz : EvtCom1SetHz,
                (uint)khz * 1000, Native.GroupPriorityHighest, Native.EventFlagGroupIdIsPriority);
        }
    }

    public void SetTransponderCode(int code)
    {
        lock (_lock)
        {
            if (_handle == IntPtr.Zero) return;
            Native.TransmitClientEvent(_handle, Native.ObjectIdUser, EvtTransponderSet, ToBco16(code),
                Native.GroupPriorityHighest, Native.EventFlagGroupIdIsPriority);
        }
    }

    /// <summary>Transponder codes are BCD: 7000 is 0x7000.</summary>
    internal static int FromBco16(int value) =>
        int.TryParse((value & 0xFFFF).ToString("X4"), out var code) ? code : 0;

    internal static uint ToBco16(int code) => Convert.ToUInt32(code.ToString("0000"), 16);

    public void Dispose()
    {
        Disconnect();
        _signal?.Dispose();
        _signal = null;
    }
}
