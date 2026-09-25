// SkyPilot X-Plane plugin
// =======================
//
// Native X-Plane 11/12 plugin that connects X-Plane to the SkyPilot desktop app
// (the pilot client for the SkyNetwork virtual aviation network):
//   * draws the other network aircraft with CSL models (rendered by the XPMP2 library),
//   * reports the user's own aircraft state, radios and transponder to the app,
//   * lets the app tune COM1/COM2 and set the transponder code.
//
// Communication with the app: UDP on localhost, ASCII, one message per datagram,
// fields separated by single spaces, numbers always use '.' as decimal point.
// The plugin binds 127.0.0.1:51730. The "app endpoint" is the source address/port
// of the most recent HELLO.
//
// App -> plugin
//   HELLO <protocol>                        every second; protocol = 1
//   ADD <callsign> <icaoType> <airline> <livery>
//                                           create (or re-create) an aircraft; airline/livery "-" = none
//   POS <callsign> <lat> <lon> <altFeetMSL> <pitchDeg> <bankDeg> <headingTrueDeg> <groundSpeedKt> <onGround 0|1>
//                                           pitch + = nose up, bank + = right wing down
//   DEL <callsign>                          remove one aircraft
//   CLEAR                                   remove all aircraft
//   COM <1|2> <kHz>                         tune the COM1/COM2 active frequency, e.g. "COM 1 118105"
//   XPDR <code>                             set the transponder code, e.g. "XPDR 7000"
//   BYE                                     remove all aircraft and forget the app endpoint
//
// Plugin -> app (only while an app endpoint is known)
//   HELLO 1 <xplaneVersion> <pluginVersion> reply to every HELLO
//   OWN <lat> <lon> <altFeetMSL> <pitch> <bank> <headingTrue> <groundSpeedKt> <onGround 0|1>
//       <pressureAltFeet> <com1kHz> <com2kHz> <xpdrCode> <xpdrOn 0|1>
//                                           about 10 times per second
//   LOG <text>                              problems worth showing to the user
//
// If no HELLO arrives for 5 seconds all aircraft are removed and the plugin stops sending.

#include "UdpSocket.h"

#include "XPMPAircraft.h"
#include "XPMPMultiplayer.h"

#include "XPLMDataAccess.h"
#include "XPLMGraphics.h"
#include "XPLMPlugin.h"
#include "XPLMProcessing.h"
#include "XPLMScenery.h"
#include "XPLMUtilities.h"

#include <algorithm>
#include <charconv>
#include <chrono>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <filesystem>
#include <map>
#include <memory>
#include <string>
#include <string_view>
#include <system_error>
#include <vector>

namespace {

// ---------------------------------------------------------------------------
// Constants
// ---------------------------------------------------------------------------

constexpr const char* kPluginName = "SkyPilot";
constexpr const char* kPluginSig = "skynetwork.skypilot";
constexpr const char* kPluginDesc = "Shows SkyNetwork traffic and connects X-Plane to the SkyPilot app.";
constexpr const char* kPluginVersion = "1.0";
constexpr const char* kDefaultIcao = "A320";
constexpr int kProtocolVersion = 1;
constexpr std::uint16_t kListenPort = 51730;

constexpr double kSessionTimeoutSec = 5.0;  // no HELLO for this long -> drop the session
constexpr double kOwnIntervalSec = 0.1;      // OWN report rate (10 Hz)
constexpr int kMaxDatagramsPerFrame = 1000;  // safety cap for draining the socket

constexpr double kFeetPerMeter = 3.28083989501;
constexpr double kKnotsPerMps = 1.94384449244;

constexpr double kGearDownBelowAglFt = 2000.0;  // gear extended below this height above ground
constexpr double kLandingLightsBelowFt = 10000.0;
constexpr float kGearTransitSec = 5.0f;          // time for a full gear extension/retraction
constexpr double kGroundFadeAglFt = 1500.0;      // ground alignment correction fades out up to this height

// ---------------------------------------------------------------------------
// Small helpers
// ---------------------------------------------------------------------------

/// Monotonic time in seconds.
double Now()
{
    using namespace std::chrono;
    return duration<double>(steady_clock::now().time_since_epoch()).count();
}

/// Writes a line to X-Plane's Log.txt.
void LogXP(const std::string& msg)
{
    std::string line = std::string(kPluginName) + ": " + msg + "\n";
    XPLMDebugString(line.c_str());
}

/// Splits a message at single spaces (empty fields from repeated spaces are skipped).
std::vector<std::string_view> SplitFields(std::string_view text)
{
    std::vector<std::string_view> out;
    size_t pos = 0;
    while (pos < text.size()) {
        size_t end = text.find(' ', pos);
        if (end == std::string_view::npos)
            end = text.size();
        if (end > pos)
            out.push_back(text.substr(pos, end - pos));
        pos = end + 1;
    }
    return out;
}

/// Locale-independent number parsing ('.' decimal point).
bool ParseDouble(std::string_view s, double& out)
{
    if (!s.empty() && s.front() == '+')
        s.remove_prefix(1);
    auto res = std::from_chars(s.data(), s.data() + s.size(), out);
    return res.ec == std::errc() && res.ptr == s.data() + s.size() && std::isfinite(out);
}

bool ParseInt(std::string_view s, long& out)
{
    if (!s.empty() && s.front() == '+')
        s.remove_prefix(1);
    auto res = std::from_chars(s.data(), s.data() + s.size(), out);
    return res.ec == std::errc() && res.ptr == s.data() + s.size();
}

/// Locale-independent fixed-point formatting.
void AppendFixed(std::string& out, double value, int decimals)
{
    if (!std::isfinite(value))
        value = 0.0;
    char buf[64];
    auto res = std::to_chars(buf, buf + sizeof(buf), value, std::chars_format::fixed, decimals);
    out.push_back(' ');
    out.append(buf, res.ptr);
}

void AppendInt(std::string& out, long value)
{
    char buf[32];
    auto res = std::to_chars(buf, buf + sizeof(buf), value);
    out.push_back(' ');
    out.append(buf, res.ptr);
}

/// Normalizes an angle difference to -180..180 degrees.
double WrapDeg180(double d)
{
    d = std::fmod(d + 180.0, 360.0);
    if (d < 0.0)
        d += 360.0;
    return d - 180.0;
}

/// Normalizes an angle to 0..360 degrees.
double WrapDeg360(double d)
{
    d = std::fmod(d, 360.0);
    return d < 0.0 ? d + 360.0 : d;
}

/// Interpolates between two angles along the shorter arc.
double LerpAngle(double a, double b, double t) { return a + WrapDeg180(b - a) * t; }

double Lerp(double a, double b, double t) { return a + (b - a) * t; }

// ---------------------------------------------------------------------------
// One position sample as received with POS
// ---------------------------------------------------------------------------

struct PosSample {
    double lat = 0, lon = 0, altFt = 0;
    double pitch = 0, bank = 0, heading = 0;
    double gsKt = 0;
    bool onGround = false;
};

PosSample Interpolate(const PosSample& a, const PosSample& b, double t)
{
    PosSample r;
    r.lat = Lerp(a.lat, b.lat, t);
    r.lon = WrapDeg180(LerpAngle(a.lon, b.lon, t));  // handles crossing the antimeridian
    r.altFt = Lerp(a.altFt, b.altFt, t);
    r.pitch = LerpAngle(a.pitch, b.pitch, t);
    r.bank = LerpAngle(a.bank, b.bank, t);
    r.heading = WrapDeg360(LerpAngle(a.heading, b.heading, t));
    r.gsKt = Lerp(a.gsKt, b.gsKt, t);
    r.onGround = b.onGround;
    return r;
}

// ---------------------------------------------------------------------------
// A network aircraft rendered by XPMP2
// ---------------------------------------------------------------------------

class NetworkAircraft : public XPMP2::Aircraft {
public:
    /// Creates the aircraft and lets XPMP2 match a CSL model.
    /// Throws (XPMP2::XPMP2Error / std::exception) if no model can be used.
    NetworkAircraft(const std::string& callsign, const std::string& icaoType,
                    const std::string& airline, const std::string& livery)
        : XPMP2::Aircraft(icaoType, airline, livery, 0, "", callsign)
    {
        label = callsign;
        // Nothing to show until the first POS arrives.
        SetVisible(false);
        // Engines running, all lights sensible from the start.
        SetLightsNav(true);
        SetLightsBeacon(true);
        SetThrustRatio(0.3f);
    }

    ~NetworkAircraft() override
    {
        if (probe_)
            XPLMDestroyProbe(probe_);
    }

    /// Stores a new POS sample. Rendering glides from the currently shown state
    /// to this sample over the expected time until the next one arrives.
    void AddSample(const PosSample& s)
    {
        const double now = Now();
        if (!hasTarget_) {
            from_ = s;
            shown_ = s;
            hasTarget_ = true;
            gearRatio_ = s.onGround ? 1.0f : -1.0f;  // decided on the first frame
        } else {
            // Smoothed estimate of the POS interval (the app sends ~15-20 per second).
            const double dt = now - targetTime_;
            if (dt > 0.0 && dt < 1.0)
                interval_ = interval_ * 0.8 + dt * 0.2;
            from_ = shown_;
        }
        to_ = s;
        targetTime_ = now;
        if (!visible_) {
            visible_ = true;
            SetVisible(true);
        }
    }

    /// Called by XPMP2 once per frame before drawing.
    void UpdatePosition(float elapsedSinceLastCall, int /*flCounter*/) override
    {
        if (!hasTarget_)
            return;

        const double now = Now();
        const double dur = std::clamp(interval_, 0.03, 0.5);
        const double t = std::clamp((now - targetTime_) / dur, 0.0, 1.0);
        shown_ = Interpolate(from_, to_, t);
        const PosSample& p = shown_;
        const float dt = std::clamp(elapsedSinceLastCall, 0.0f, 1.0f);

        // --- Terrain below the aircraft (X-Plane's own scenery) ---
        const double terrainFt = TerrainElevationFt(p, now);
        const bool haveTerrain = !std::isnan(terrainFt);

        // --- Vertical alignment with X-Plane's ground ---
        // The sender's scenery elevation can differ from X-Plane's. On the ground the
        // aircraft is put exactly on X-Plane's terrain; after take-off that correction
        // fades out with height, and it is blended over time so nothing jumps.
        double desiredCorrFt = 0.0;
        if (haveTerrain) {
            if (p.onGround) {
                desiredCorrFt = terrainFt - p.altFt;
                groundCorrFt_ = desiredCorrFt;
            } else {
                const double agl = p.altFt - terrainFt;
                const double fade = std::clamp(1.0 - agl / kGroundFadeAglFt, 0.0, 1.0);
                if (fade <= 0.0)
                    groundCorrFt_ = 0.0;  // forget it once well clear of the ground
                desiredCorrFt = groundCorrFt_ * fade;
            }
        }
        if (!corrInitialized_) {
            corrFt_ = desiredCorrFt;
            corrInitialized_ = haveTerrain;
        } else {
            const double k = 1.0 - std::exp(-double(dt) / 0.7);  // ~0.7 s time constant
            corrFt_ += (desiredCorrFt - corrFt_) * k;
        }
        const double altFt = p.altFt + corrFt_;
        const double aglFt = haveTerrain ? altFt - terrainFt : (p.onGround ? 0.0 : 99999.0);

        // --- Position and attitude ---
        SetLocation(p.lat, p.lon, altFt, p.onGround);
        SetPitch(float(p.pitch));
        SetRoll(float(p.bank));
        SetHeading(float(p.heading));
        // Never sink below X-Plane's terrain when close to it.
        bClampToGround = aglFt < 500.0;

        // --- Gear ---
        const float gearTarget = (p.onGround || aglFt < kGearDownBelowAglFt) ? 1.0f : 0.0f;
        if (gearRatio_ < 0.0f)
            gearRatio_ = gearTarget;  // first frame: no animation
        const float step = dt / kGearTransitSec;
        if (gearRatio_ < gearTarget)
            gearRatio_ = std::min(gearTarget, gearRatio_ + step);
        else if (gearRatio_ > gearTarget)
            gearRatio_ = std::max(gearTarget, gearRatio_ - step);
        SetGearRatio(gearRatio_);

        // --- Lights ---
        SetLightsNav(true);
        SetLightsBeacon(true);
        SetLightsStrobe(!p.onGround || p.gsKt > 40.0);           // strobes on the runway and in the air
        SetLightsTaxi(p.onGround);
        SetLightsLanding((!p.onGround && altFt < kLandingLightsBelowFt) || (p.onGround && p.gsKt > 40.0));

        // --- Simple animations: wheels, engines, propellers ---
        const double gsMps = p.gsKt / kKnotsPerMps;
        SetTireRotRpm(p.onGround ? float(gsMps / (2.0 * 3.14159265358979 * 0.5) * 60.0) : 0.0f);
        tireAngle_ = float(WrapDeg360(tireAngle_ + GetTireRotRpm() / 60.0 * 360.0 * dt));
        SetTireRotAngle(tireAngle_);

        const float propRpm = p.onGround && p.gsKt < 30.0 ? 1000.0f : 2200.0f;
        SetPropRotRpm(propRpm);
        SetEngineRotRpm(propRpm);
        propAngle_ = float(WrapDeg360(propAngle_ + propRpm / 60.0 * 360.0 * dt));
        SetPropRotAngle(propAngle_);
        SetEngineRotAngle(propAngle_);
        SetThrustRatio(p.onGround && p.gsKt < 30.0 ? 0.1f : 0.6f);
    }

private:
    /// Returns X-Plane's terrain elevation below the aircraft in feet MSL, or NaN.
    /// Probing is cheap enough every frame near the ground; high up it is refreshed every 2 s.
    double TerrainElevationFt(const PosSample& p, double now)
    {
        const bool nearGround = p.onGround || std::isnan(lastTerrainFt_) || (p.altFt - lastTerrainFt_) < 5000.0;
        if (!nearGround && now - lastProbeTime_ < 2.0)
            return lastTerrainFt_;
        lastProbeTime_ = now;

        if (!probe_)
            probe_ = XPLMCreateProbe(xplm_ProbeY);
        if (!probe_)
            return lastTerrainFt_;

        double x, y, z;
        XPLMWorldToLocal(p.lat, p.lon, p.altFt / kFeetPerMeter, &x, &y, &z);
        XPLMProbeInfo_t info{};
        info.structSize = sizeof(info);
        if (XPLMProbeTerrainXYZ(probe_, float(x), float(y), float(z), &info) == xplm_ProbeHitTerrain) {
            double lat, lon, altM;
            XPLMLocalToWorld(info.locationX, info.locationY, info.locationZ, &lat, &lon, &altM);
            lastTerrainFt_ = altM * kFeetPerMeter;
        }
        return lastTerrainFt_;
    }

    PosSample from_, to_, shown_;
    bool hasTarget_ = false;
    bool visible_ = false;
    double targetTime_ = 0.0;
    double interval_ = 0.066;  // expected time between POS messages [s]

    XPLMProbeRef probe_ = nullptr;
    double lastProbeTime_ = -1e9;
    double lastTerrainFt_ = NAN;

    double groundCorrFt_ = 0.0;  // last measured difference sender altitude -> X-Plane ground
    double corrFt_ = 0.0;        // currently applied vertical correction
    bool corrInitialized_ = false;

    float gearRatio_ = 1.0f;
    float tireAngle_ = 0.0f;
    float propAngle_ = 0.0f;
};

// ---------------------------------------------------------------------------
// Datarefs of the user's aircraft
// ---------------------------------------------------------------------------

struct OwnDataRefs {
    XPLMDataRef lat = nullptr, lon = nullptr, elev = nullptr;
    XPLMDataRef theta = nullptr, phi = nullptr, psi = nullptr;
    XPLMDataRef groundspeed = nullptr, onground = nullptr, pressureAlt = nullptr;
    XPLMDataRef com1 = nullptr, com2 = nullptr;  // kHz (8.33 kHz capable datarefs)
    bool comIn10kHz = false;                    // fallback datarefs in 10 kHz units (older X-Plane)
    XPLMDataRef xpdrCode = nullptr, xpdrMode = nullptr;

    void Find()
    {
        lat = XPLMFindDataRef("sim/flightmodel/position/latitude");
        lon = XPLMFindDataRef("sim/flightmodel/position/longitude");
        elev = XPLMFindDataRef("sim/flightmodel/position/elevation");
        theta = XPLMFindDataRef("sim/flightmodel/position/theta");
        phi = XPLMFindDataRef("sim/flightmodel/position/phi");
        psi = XPLMFindDataRef("sim/flightmodel/position/true_psi");
        if (!psi)
            psi = XPLMFindDataRef("sim/flightmodel/position/psi");
        groundspeed = XPLMFindDataRef("sim/flightmodel/position/groundspeed");
        onground = XPLMFindDataRef("sim/flightmodel/failures/onground_any");
        pressureAlt = XPLMFindDataRef("sim/flightmodel2/position/pressure_altitude");

        com1 = XPLMFindDataRef("sim/cockpit2/radios/actuators/com1_frequency_hz_833");
        com2 = XPLMFindDataRef("sim/cockpit2/radios/actuators/com2_frequency_hz_833");
        comIn10kHz = false;
        if (!com1 || !com2) {
            com1 = XPLMFindDataRef("sim/cockpit2/radios/actuators/com1_frequency_hz");
            com2 = XPLMFindDataRef("sim/cockpit2/radios/actuators/com2_frequency_hz");
            comIn10kHz = true;
        }
        xpdrCode = XPLMFindDataRef("sim/cockpit/radios/transponder_code");
        xpdrMode = XPLMFindDataRef("sim/cockpit/radios/transponder_mode");
    }
};

double GetD(XPLMDataRef r) { return r ? XPLMGetDatad(r) : 0.0; }
double GetF(XPLMDataRef r) { return r ? double(XPLMGetDataf(r)) : 0.0; }
int GetI(XPLMDataRef r) { return r ? XPLMGetDatai(r) : 0; }

// ---------------------------------------------------------------------------
// Plugin state
// ---------------------------------------------------------------------------

struct PluginState {
    skypilot::UdpSocket socket;
    skypilot::UdpEndpoint app;       // current app endpoint (invalid = no session)
    double lastHelloTime = 0.0;
    double lastOwnTime = 0.0;

    bool xpmpReady = false;           // XPMP2 initialized
    std::vector<std::string> startupWarnings;  // re-sent as LOG to every new app session

    std::map<std::string, std::unique_ptr<NetworkAircraft>> aircraft;
    OwnDataRefs own;
    std::string pluginDir;           // ".../Resources/plugins/SkyPilot/"
};

PluginState* g = nullptr;

// ---------------------------------------------------------------------------
// Sending
// ---------------------------------------------------------------------------

void SendToApp(const std::string& msg)
{
    if (g && g->app.IsValid())
        g->socket.Send(g->app, msg);
}

/// Makes a text safe for a single-line LOG message (no line breaks, trimmed).
std::string OneLine(std::string text)
{
    for (char& c : text)
        if (c == '\r' || c == '\n' || c == '\t')
            c = ' ';
    const size_t first = text.find_first_not_of(' ');
    const size_t last = text.find_last_not_of(' ');
    return first == std::string::npos ? std::string() : text.substr(first, last - first + 1);
}

/// Logs to Log.txt and, if connected, to the app.
void ReportProblem(const std::string& text)
{
    const std::string line = OneLine(text);
    LogXP(line);
    SendToApp("LOG " + line);
}

void SendOwnState()
{
    const OwnDataRefs& r = g->own;
    const double elevFt = GetD(r.elev) * kFeetPerMeter;
    const double pressAltFt = r.pressureAlt ? GetF(r.pressureAlt) : elevFt;

    long com1 = GetI(r.com1), com2 = GetI(r.com2);
    if (r.comIn10kHz) {
        com1 *= 10;
        com2 *= 10;
    }

    std::string msg = "OWN";
    msg.reserve(160);
    AppendFixed(msg, GetD(r.lat), 7);
    AppendFixed(msg, GetD(r.lon), 7);
    AppendFixed(msg, elevFt, 1);
    AppendFixed(msg, GetF(r.theta), 2);
    AppendFixed(msg, GetF(r.phi), 2);
    AppendFixed(msg, WrapDeg360(GetF(r.psi)), 2);
    AppendFixed(msg, GetF(r.groundspeed) * kKnotsPerMps, 1);
    AppendInt(msg, GetI(r.onground) != 0 ? 1 : 0);
    AppendFixed(msg, pressAltFt, 1);
    AppendInt(msg, com1);
    AppendInt(msg, com2);
    AppendInt(msg, GetI(r.xpdrCode));
    AppendInt(msg, GetI(r.xpdrMode) >= 2 ? 1 : 0);
    SendToApp(msg);
}

// ---------------------------------------------------------------------------
// Aircraft management
// ---------------------------------------------------------------------------

void RemoveAllAircraft() { g->aircraft.clear(); }

void AddAircraft(const std::string& callsign, const std::string& icao, std::string airline, std::string livery)
{
    if (airline == "-")
        airline.clear();
    if (livery == "-")
        livery.clear();

    // Re-create with the new model if the callsign already exists.
    g->aircraft.erase(callsign);

    if (!g->xpmpReady) {
        ReportProblem("cannot show " + callsign + ": the multiplayer library is not initialized");
        return;
    }
    if (XPMPGetNumberOfInstalledModels() <= 0) {
        // Already reported to the app at session start; don't repeat it for every aircraft.
        static bool loggedOnce = false;
        if (!loggedOnce)
            LogXP("aircraft are not shown because no CSL models are installed");
        loggedOnce = true;
        return;
    }

    try {
        auto ac = std::make_unique<NetworkAircraft>(callsign, icao, airline, livery);
        LogXP("added " + callsign + " (" + icao + (airline.empty() ? "" : " " + airline) + ") using model '" +
              ac->GetModelName() + "'");
        g->aircraft[callsign] = std::move(ac);
    } catch (const std::exception& e) {
        ReportProblem("cannot show " + callsign + " (" + icao + "): " + e.what());
    }
}

void HandlePos(const std::vector<std::string_view>& f)
{
    // POS <callsign> <lat> <lon> <alt> <pitch> <bank> <heading> <gs> <onGround>
    if (f.size() < 10)
        return;
    auto it = g->aircraft.find(std::string(f[1]));
    if (it == g->aircraft.end())
        return;  // unknown callsign

    PosSample s;
    long onGround = 0;
    if (!ParseDouble(f[2], s.lat) || !ParseDouble(f[3], s.lon) || !ParseDouble(f[4], s.altFt) ||
        !ParseDouble(f[5], s.pitch) || !ParseDouble(f[6], s.bank) || !ParseDouble(f[7], s.heading) ||
        !ParseDouble(f[8], s.gsKt) || !ParseInt(f[9], onGround))
        return;
    if (s.lat < -90.0 || s.lat > 90.0 || s.lon < -180.0 || s.lon > 180.0)
        return;
    s.heading = WrapDeg360(s.heading);
    s.onGround = onGround != 0;
    it->second->AddSample(s);
}

// ---------------------------------------------------------------------------
// Radios
// ---------------------------------------------------------------------------

void TuneCom(long radio, long kHz)
{
    // Airband 118.000-136.990 MHz (kHz values like 118105 for 8.33 kHz channels).
    if (kHz < 118000 || kHz > 137000)
        return;
    XPLMDataRef ref = radio == 1 ? g->own.com1 : radio == 2 ? g->own.com2 : nullptr;
    if (!ref)
        return;
    XPLMSetDatai(ref, int(g->own.comIn10kHz ? kHz / 10 : kHz));
}

void SetSquawk(long code)
{
    // Only digits 0-7 are valid squawk digits.
    if (code < 0 || code > 7777)
        return;
    for (long c = code; c > 0; c /= 10)
        if (c % 10 > 7)
            return;
    if (g->own.xpdrCode)
        XPLMSetDatai(g->own.xpdrCode, int(code));
}

// ---------------------------------------------------------------------------
// Session / message dispatch
// ---------------------------------------------------------------------------

void EndSession()
{
    RemoveAllAircraft();
    g->app = skypilot::UdpEndpoint{};
}

void HandleHello(const std::vector<std::string_view>& f, const skypilot::UdpEndpoint& from)
{
    long proto = 0;
    if (f.size() >= 2 && ParseInt(f[1], proto) && proto != kProtocolVersion)
        LogXP("app uses protocol " + std::to_string(proto) + ", plugin speaks " + std::to_string(kProtocolVersion));

    const bool newSession = !g->app.IsValid() || g->app != from;
    g->app = from;
    g->lastHelloTime = Now();

    int xpVer = 0, xplmVer = 0;
    XPLMHostApplicationID host = 0;
    XPLMGetVersions(&xpVer, &xplmVer, &host);

    std::string reply = "HELLO";
    AppendInt(reply, kProtocolVersion);
    AppendInt(reply, xpVer);
    reply += ' ';
    reply += kPluginVersion;
    SendToApp(reply);

    if (newSession) {
        LogXP("app connected");
        for (const std::string& w : g->startupWarnings)
            SendToApp("LOG " + OneLine(w));
    }
}

void HandleMessage(const char* data, int len, const skypilot::UdpEndpoint& from)
{
    // Only accept traffic from this machine.
    if ((from.address >> 24) != 127)
        return;

    std::string_view text(data, size_t(len));
    while (!text.empty() && (text.back() == '\n' || text.back() == '\r' || text.back() == '\0' || text.back() == ' '))
        text.remove_suffix(1);
    const auto f = SplitFields(text);
    if (f.empty())
        return;
    const std::string_view cmd = f[0];

    if (cmd == "HELLO") {
        HandleHello(f, from);
        return;
    }

    // Everything else is only accepted from the current app endpoint.
    if (!g->app.IsValid() || from != g->app)
        return;

    if (cmd == "POS") {
        HandlePos(f);
    } else if (cmd == "ADD") {
        if (f.size() >= 3)
            AddAircraft(std::string(f[1]), std::string(f[2]), f.size() >= 4 ? std::string(f[3]) : "-",
                        f.size() >= 5 ? std::string(f[4]) : "-");
    } else if (cmd == "DEL") {
        if (f.size() >= 2)
            g->aircraft.erase(std::string(f[1]));
    } else if (cmd == "CLEAR") {
        RemoveAllAircraft();
    } else if (cmd == "COM") {
        long radio = 0, kHz = 0;
        if (f.size() >= 3 && ParseInt(f[1], radio) && ParseInt(f[2], kHz))
            TuneCom(radio, kHz);
    } else if (cmd == "XPDR") {
        long code = 0;
        if (f.size() >= 2 && ParseInt(f[1], code))
            SetSquawk(code);
    } else if (cmd == "BYE") {
        LogXP("app disconnected");
        EndSession();
    }
}

/// Flight loop: drains the socket, checks the session timeout and sends OWN reports.
float FlightLoop(float, float, int, void*)
{
    if (!g)
        return 0.0f;

    char buf[2048];
    for (int i = 0; i < kMaxDatagramsPerFrame; ++i) {
        skypilot::UdpEndpoint from;
        const int n = g->socket.Receive(buf, sizeof(buf), from);
        if (n <= 0)
            break;
        HandleMessage(buf, n, from);
    }

    const double now = Now();
    if (g->app.IsValid()) {
        if (now - g->lastHelloTime > kSessionTimeoutSec) {
            LogXP("app timed out");
            EndSession();
        } else if (now - g->lastOwnTime >= kOwnIntervalSec) {
            g->lastOwnTime = now;
            SendOwnState();
        }
    }
    return -1.0f;  // call again next frame
}

// ---------------------------------------------------------------------------
// XPMP2 set-up
// ---------------------------------------------------------------------------

/// Called by X-Plane when TCAS/AI control becomes available again.
void RequestAiAgain(void*) { XPMPMultiplayerEnable(RequestAiAgain); }

std::string GetPluginDir()
{
    char path[1024] = {0};
    XPLMGetPluginInfo(XPLMGetMyID(), nullptr, path, nullptr, nullptr);
    // path = ".../plugins/SkyPilot/win_x64/SkyPilot.xpl": strip file name and platform folder.
    const char* sep = XPLMGetDirectorySeparator();
    std::string p(path);
    for (int i = 0; i < 2; ++i) {
        size_t pos = p.find_last_of(std::string("/\\") + sep);
        if (pos == std::string::npos)
            break;
        p.erase(pos);
    }
    return p + sep;
}

void InitMultiplayer()
{
    namespace fs = std::filesystem;
    const std::string resDir = g->pluginDir + "Resources";

    const char* err = XPMPMultiplayerInit(kPluginName, resDir.c_str(), nullptr, kDefaultIcao, kPluginName);
    if (err && err[0]) {
        g->xpmpReady = false;
        const std::string msg = std::string("multiplayer library initialization failed: ") + err;
        LogXP(msg);
        g->startupWarnings.push_back(msg);
        return;
    }
    g->xpmpReady = true;

    // Load every CSL package folder below Resources/CSL.
    int packages = 0;
    std::error_code ec;
    const fs::path cslRoot = fs::u8path(resDir + "/CSL");
    if (fs::is_directory(cslRoot, ec)) {
        for (const auto& entry : fs::directory_iterator(cslRoot, ec)) {
            std::error_code ec2;
            if (!entry.is_directory(ec2))
                continue;
            const std::string dir = entry.path().u8string();
            const char* res = XPMPLoadCSLPackage(dir.c_str());
            if (res && res[0]) {
                const std::string msg = "problem loading CSL package '" + entry.path().filename().u8string() + "': " + res;
                LogXP(msg);
                g->startupWarnings.push_back(msg);
            } else {
                ++packages;
            }
        }
    }

    const int models = XPMPGetNumberOfInstalledModels();
    LogXP("loaded " + std::to_string(packages) + " CSL package(s) with " + std::to_string(models) + " model(s)");
    if (models <= 0) {
        const std::string msg =
            "no CSL models found - other aircraft cannot be shown. Install a CSL package (for example the free "
            "Bluebell package) into X-Plane/Resources/plugins/SkyPilot/Resources/CSL/ and restart X-Plane.";
        LogXP(msg);
        g->startupWarnings.push_back(msg);
    }

    XPMPEnableAircraftLabels(true);
    XPMPEnableMap(true, true);

    // Show our traffic on TCAS and to other plugins; fails if another plugin owns it (not fatal).
    const char* ai = XPMPMultiplayerEnable(RequestAiAgain);
    if (ai && ai[0])
        LogXP(std::string("TCAS/AI traffic not available: ") + ai);
}

} // namespace

// ---------------------------------------------------------------------------
// X-Plane plugin entry points
// ---------------------------------------------------------------------------

PLUGIN_API int XPluginStart(char* outName, char* outSig, char* outDesc)
{
    std::snprintf(outName, 256, "%s", kPluginName);
    std::snprintf(outSig, 256, "%s", kPluginSig);
    std::snprintf(outDesc, 256, "%s", kPluginDesc);
    XPLMEnableFeature("XPLM_USE_NATIVE_PATHS", 1);
    LogXP(std::string("version ") + kPluginVersion + " starting");
    return 1;
}

PLUGIN_API void XPluginStop(void) {}

PLUGIN_API int XPluginEnable(void)
{
    g = new PluginState();
    g->pluginDir = GetPluginDir();
    g->own.Find();

    InitMultiplayer();

    std::string err;
    if (!g->socket.Open(kListenPort, err))
        LogXP("cannot open UDP port " + std::to_string(kListenPort) + ": " + err);
    else
        LogXP("listening on 127.0.0.1:" + std::to_string(kListenPort));

    XPLMRegisterFlightLoopCallback(FlightLoop, -1.0f, nullptr);
    return 1;
}

PLUGIN_API void XPluginDisable(void)
{
    XPLMUnregisterFlightLoopCallback(FlightLoop, nullptr);
    if (!g)
        return;

    SendToApp("LOG X-Plane plugin disabled");
    RemoveAllAircraft();
    g->socket.Close();
    if (g->xpmpReady) {
        XPMPMultiplayerDisable();
        XPMPMultiplayerCleanup();
    }
    delete g;
    g = nullptr;
}

PLUGIN_API void XPluginReceiveMessage(XPLMPluginID, int inMsg, void*)
{
    // Another plugin asks for TCAS/AI control: hand it over.
    if (inMsg == XPLM_MSG_RELEASE_PLANES && g && g->xpmpReady)
        XPMPMultiplayerDisable();
}
