namespace SkyPilot.Core.Model;

/// <summary>
/// Another pilot on the network. Position reports arrive every ~5 seconds; between reports the
/// aircraft is extrapolated along its last known velocity and the rendered state is blended
/// towards the prediction so corrections never cause a visible jump.
/// </summary>
public sealed class RemoteAircraft(string callsign)
{
    private const double MaxExtrapolationSeconds = 8;
    private const double BlendSeconds = 1.5;

    private AircraftState? _prev;
    private DateTime _prevTime;
    private AircraftState _last;
    private DateTime _lastTime;
    private AircraftState? _rendered;
    private DateTime _renderedTime;

    public string Callsign { get; } = callsign;
    public string Equipment { get; set; } = "";
    public string Airline { get; set; } = "";
    public string? ModelTitle { get; set; }
    public bool InSimulator { get; set; }
    public DateTime FirstSeen { get; } = DateTime.UtcNow;
    public DateTime LastUpdate => _lastTime;
    public AircraftState Last => _last;

    public void OnPositionReport(AircraftState state, DateTime now)
    {
        if (_lastTime != default)
        {
            _prev = _last;
            _prevTime = _lastTime;
        }
        _last = state;
        _lastTime = now;
    }

    /// <summary>Predicted position of the aircraft at <paramref name="now"/>.</summary>
    public AircraftState Predict(DateTime now)
    {
        if (_prev is not { } prev || _last.OnGround && _last.GroundSpeedKnots < 1)
            return _last;
        double interval = (_lastTime - _prevTime).TotalSeconds;
        if (interval <= 0.1 || interval > 30) return _last;
        double t = Math.Min((now - _lastTime).TotalSeconds, MaxExtrapolationSeconds);
        double k = t / interval;
        return _last with
        {
            Latitude = _last.Latitude + (_last.Latitude - prev.Latitude) * k,
            Longitude = _last.Longitude + WrapDelta(_last.Longitude - prev.Longitude, 180) * k,
            AltitudeFeet = _last.AltitudeFeet + (_last.AltitudeFeet - prev.AltitudeFeet) * k,
            HeadingDegrees = Normalize(_last.HeadingDegrees + WrapDelta(_last.HeadingDegrees - prev.HeadingDegrees, 180) * k),
        };
    }

    /// <summary>Smoothed state to draw in the simulator.</summary>
    public AircraftState Render(DateTime now)
    {
        var target = Predict(now);
        if (_rendered is not { } r)
        {
            _rendered = target;
            _renderedTime = now;
            return target;
        }
        double dt = Math.Max(0, (now - _renderedTime).TotalSeconds);
        _renderedTime = now;
        double a = Math.Min(1, dt / BlendSeconds);
        // Large corrections (teleport, slew) are applied at once.
        if (Math.Abs(target.Latitude - r.Latitude) > 0.05 || Math.Abs(target.Longitude - r.Longitude) > 0.05)
            a = 1;
        var next = target with
        {
            Latitude = r.Latitude + (target.Latitude - r.Latitude) * a,
            Longitude = r.Longitude + WrapDelta(target.Longitude - r.Longitude, 180) * a,
            AltitudeFeet = r.AltitudeFeet + (target.AltitudeFeet - r.AltitudeFeet) * a,
            PitchDegrees = r.PitchDegrees + (target.PitchDegrees - r.PitchDegrees) * a,
            BankDegrees = r.BankDegrees + (target.BankDegrees - r.BankDegrees) * a,
            HeadingDegrees = Normalize(r.HeadingDegrees + WrapDelta(target.HeadingDegrees - r.HeadingDegrees, 180) * a),
        };
        _rendered = next;
        return next;
    }

    private static double WrapDelta(double delta, double half)
    {
        while (delta > half) delta -= 2 * half;
        while (delta < -half) delta += 2 * half;
        return delta;
    }

    private static double Normalize(double heading)
    {
        heading %= 360;
        return heading < 0 ? heading + 360 : heading;
    }
}
