// Signatures mirror the managed SimConnect wrapper shipped with the MSFS 2020/2024 SDK.
#pragma warning disable CS0067 // events are never raised by the stub
using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Microsoft.FlightSimulator.SimConnect
{
    public enum SIMCONNECT_DATATYPE { INVALID, INT32, INT64, FLOAT32, FLOAT64, STRING8, STRING32, STRING64, STRING128, STRING256, STRING260, STRINGV }
    public enum SIMCONNECT_PERIOD { NEVER, ONCE, VISUAL_FRAME, SIM_FRAME, SECOND }
    [Flags] public enum SIMCONNECT_DATA_REQUEST_FLAG { DEFAULT = 0, CHANGED = 1, TAGGED = 2 }
    [Flags] public enum SIMCONNECT_DATA_SET_FLAG { DEFAULT = 0, TAGGED = 1 }
    [Flags] public enum SIMCONNECT_EVENT_FLAG { DEFAULT = 0, FAST_REPEAT_TIMER = 1, SLOW_REPEAT_TIMER = 2, GROUPID_IS_PRIORITY = 16 }
    public enum SIMCONNECT_GROUP_PRIORITY : uint { HIGHEST = 1, HIGHEST_MASKABLE = 10000000, STANDARD = 1900000000, DEFAULT = 2000000000, LOWEST = 4000000000 }

    public enum SIMCONNECT_EXCEPTION : uint
    {
        NONE, ERROR, SIZE_MISMATCH, UNRECOGNIZED_ID, UNOPENED, VERSION_MISMATCH, TOO_MANY_GROUPS, NAME_UNRECOGNIZED,
        TOO_MANY_EVENT_NAMES, EVENT_ID_DUPLICATE, TOO_MANY_MAPS, TOO_MANY_OBJECTS, TOO_MANY_REQUESTS, WEATHER_INVALID_PORT,
        WEATHER_INVALID_METAR, WEATHER_UNABLE_TO_GET_OBSERVATION, WEATHER_UNABLE_TO_CREATE_STATION, WEATHER_UNABLE_TO_REMOVE_STATION,
        INVALID_DATA_TYPE, INVALID_DATA_SIZE, DATA_ERROR, INVALID_ARRAY, CREATE_OBJECT_FAILED, LOAD_FLIGHTPLAN_FAILED,
        OPERATION_INVALID_FOR_OBJECT_TYPE, ILLEGAL_OPERATION, ALREADY_SUBSCRIBED, INVALID_ENUM, DEFINITION_ERROR,
        DUPLICATE_ID, DATUM_ID, OUT_OF_BOUNDS, ALREADY_CREATED, OBJECT_OUTSIDE_REALITY_BUBBLE, OBJECT_CONTAINER,
        OBJECT_AI, OBJECT_ATC, OBJECT_SCHEDULE,
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct SIMCONNECT_DATA_INITPOSITION
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

    public class SIMCONNECT_RECV { public uint dwSize; public uint dwVersion; public uint dwID; }
    public class SIMCONNECT_RECV_OPEN : SIMCONNECT_RECV { public string szApplicationName; }
    public class SIMCONNECT_RECV_EXCEPTION : SIMCONNECT_RECV { public uint dwException; public uint dwSendID; public uint dwIndex; }
    public class SIMCONNECT_RECV_ASSIGNED_OBJECT_ID : SIMCONNECT_RECV { public uint dwRequestID; public uint dwObjectID; }
    public class SIMCONNECT_RECV_SIMOBJECT_DATA : SIMCONNECT_RECV
    {
        public uint dwRequestID; public uint dwObjectID; public uint dwDefineID; public uint dwFlags;
        public uint dwentrynumber; public uint dwoutof; public uint dwDefineCount; public object[] dwData;
    }

    public delegate void RecvOpenEventHandler(SimConnect sender, SIMCONNECT_RECV_OPEN data);
    public delegate void RecvQuitEventHandler(SimConnect sender, SIMCONNECT_RECV data);
    public delegate void RecvExceptionEventHandler(SimConnect sender, SIMCONNECT_RECV_EXCEPTION data);
    public delegate void RecvAssignedObjectIdEventHandler(SimConnect sender, SIMCONNECT_RECV_ASSIGNED_OBJECT_ID data);
    public delegate void RecvSimobjectDataEventHandler(SimConnect sender, SIMCONNECT_RECV_SIMOBJECT_DATA data);

    public class SimConnect : IDisposable
    {
        public const uint SIMCONNECT_OBJECT_ID_USER = 0;
        public const int SIMCONNECT_UNUSED = -1;

        public SimConnect(string szName, IntPtr hWnd, uint UserEventWin32, WaitHandle hEventHandle, uint ConfigIndex) =>
            throw new COMException("SkyPilot was built without the MSFS SDK (SimConnect stub)", unchecked((int)0x80004005));

        public event RecvOpenEventHandler OnRecvOpen;
        public event RecvQuitEventHandler OnRecvQuit;
        public event RecvExceptionEventHandler OnRecvException;
        public event RecvAssignedObjectIdEventHandler OnRecvAssignedObjectId;
        public event RecvSimobjectDataEventHandler OnRecvSimobjectData;

        public void ReceiveMessage() { }
        public void AddToDataDefinition(Enum DefineID, string DatumName, string UnitsName, SIMCONNECT_DATATYPE DatumType, float fEpsilon, uint DatumID) { }
        public void RegisterDataDefineStruct<T>(Enum dwID) { }
        public void RequestDataOnSimObject(Enum RequestID, Enum DefineID, uint ObjectID, SIMCONNECT_PERIOD Period,
            SIMCONNECT_DATA_REQUEST_FLAG Flags, uint origin, uint interval, uint limit) { }
        public void SetDataOnSimObject(Enum DefineID, uint ObjectID, SIMCONNECT_DATA_SET_FLAG Flags, object pDataSet) { }
        public void MapClientEventToSimEvent(Enum EventID, string EventName) { }
        public void TransmitClientEvent(uint ObjectID, Enum EventID, uint dwData, Enum GroupID, SIMCONNECT_EVENT_FLAG Flags) { }
        public void AICreateNonATCAircraft(string szContainerTitle, string szTailNumber, SIMCONNECT_DATA_INITPOSITION InitPos, Enum RequestID) { }
        public void AIReleaseControl(uint ObjectID, Enum RequestID) { }
        public void AIRemoveObject(uint ObjectID, Enum RequestID) { }
        public void GetLastSentPacketID(out uint dwSendID) => dwSendID = 0;
        public void Dispose() { }
    }
}
