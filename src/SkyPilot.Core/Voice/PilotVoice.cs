using System.Net.Sockets;
using SkyNetwork.Voice;
using SkyPilot.Core.Model;
using SkyPilot.Core.Settings;

namespace SkyPilot.Core.Voice;

/// <summary>Voice server sign-in: the FSD server's host with the voice port, and the FSD credentials.</summary>
public sealed record VoiceLogin(string Host, int Port, int Cid, string Callsign, string Password);

/// <summary>
/// Radio voice for the pilot. Connects together with the network session, keeps COM1/COM2 and the
/// aircraft position up to date on the voice server and, if the voice server cannot be reached or
/// drops the connection, tries again every 30 seconds. Voice problems are reported as messages and
/// never affect the FSD connection. Events are raised on background threads.
/// </summary>
public sealed class PilotVoice : IDisposable
{
    public static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(30);

    private readonly VoiceClient _client = new();
    private readonly ReceiveTracker _heard = new();
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private readonly object _gate = new();
    private readonly TimeSpan _retryInterval;
    private IReadOnlyList<Radio> _radios = PilotRadios.Build(0, 0, true, true, 1);
    private VoiceLogin? _login;
    private CancellationTokenSource? _attempt;
    private Timer? _retry;
    private volatile bool _connecting;
    private volatile bool _failing;

    public PilotVoice() : this(RetryInterval)
    {
    }

    internal PilotVoice(TimeSpan retryInterval)
    {
        _retryInterval = retryInterval;
        _client.StateChanged += OnStateChanged;
        _client.TransmitChanged += _ => Changed?.Invoke(this, EventArgs.Empty);
        _client.ReceiveActivity += (callsign, freq, active) =>
        {
            _heard.Update(callsign, freq, active);
            Changed?.Invoke(this, EventArgs.Empty);
        };
    }

    public VoiceState State => _client.State;

    /// <summary>The voice server could not be reached or dropped us; a retry is scheduled.</summary>
    public bool Failing => _failing;

    public bool Transmitting => _client.Transmitting;
    public float MicLevel => _client.MicLevel;
    public IReadOnlyList<Radio> Radios => _radios;

    /// <summary>Connection state, transmission or received stations changed.</summary>
    public event EventHandler? Changed;

    /// <summary>A line for the messages area (Russian).</summary>
    public event EventHandler<string>? Info;
    public event EventHandler<string>? Error;

    /// <summary>Connect for a new network session (the previous one, if any, is closed).</summary>
    public void Start(VoiceLogin login)
    {
        Stop();
        lock (_gate) _login = login;
        _ = ConnectAsync(login);
    }

    public void Stop()
    {
        lock (_gate)
        {
            _login = null;
            _attempt?.Cancel();
            _retry?.Dispose();
            _retry = null;
            _failing = false;
        }
        _client.Disconnect();
        _heard.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Reconnect now (the user clicked the voice indicator). False if there is no network session.</summary>
    public bool Reconnect()
    {
        VoiceLogin? login;
        lock (_gate) login = _login;
        if (login == null) return false;
        _client.Disconnect();
        _ = ConnectAsync(login);
        return true;
    }

    public void UpdateRadios(int com1Khz, int com2Khz, bool com1Rx, bool com2Rx, int txRadio)
    {
        _radios = PilotRadios.Build(com1Khz, com2Khz, com1Rx, com2Rx, txRadio);
        _client.SetRadios(_radios);
    }

    public void UpdatePosition(AircraftState state) => _client.SetSites([PilotRadios.Site(state)]);

    public void ApplySettings(AppSettings settings)
    {
        var (inputs, outputs) = VoiceClient.Devices();
        _client.ApplySettings(PilotRadios.ToVoiceSettings(settings, inputs, outputs));
    }

    /// <summary>The PTT button in the window, in addition to the bound key.</summary>
    public void SetManualPtt(bool down) => _client.SetManualPtt(down);

    /// <summary>Callsigns heard right now on COM1 (radio = 1) or COM2 (radio = 2), or empty.</summary>
    public string HeardOn(int radio)
    {
        var radios = _radios;
        if (radio < 1 || radio > radios.Count || !radios[radio - 1].Receive) return "";
        return _heard.On(radios[radio - 1].FrequencyHz);
    }

    private async Task ConnectAsync(VoiceLogin login)
    {
        await _connectLock.WaitAsync().ConfigureAwait(false);
        try
        {
            CancellationToken ct;
            lock (_gate)
            {
                // Stopped, restarted for another session, or already connected by an earlier attempt.
                if (!ReferenceEquals(_login, login) || _client.State == VoiceState.Connected) return;
                _retry?.Dispose();
                _retry = null;
                _attempt?.Dispose();
                _attempt = new CancellationTokenSource();
                ct = _attempt.Token;
            }
            _connecting = true;
            try
            {
                await _client.ConnectAsync(login.Host, login.Port, (uint)login.Cid, login.Callsign, login.Password, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is VoiceException or SocketException or OperationCanceledException or ArgumentException)
            {
                if (!ct.IsCancellationRequested) OnFailed(login, "нет связи с голосовым сервером", ex.Message);
                return;
            }
            finally
            {
                _connecting = false;
            }
            bool wanted;
            lock (_gate) wanted = ReferenceEquals(_login, login);
            if (!wanted)
            {
                _client.Disconnect();
                return;
            }
            _failing = false;
            Info?.Invoke(this, $"Голос: подключено к {login.Host}:{login.Port}");
            Changed?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _connectLock.Release();
        }
    }

    private void OnStateChanged(VoiceState state, string reason)
    {
        if (state == VoiceState.Disconnected) _heard.Clear();
        if (reason.Length > 0 && state != VoiceState.Disconnected)
        {
            // The connection is fine but the microphone or speakers could not be opened.
            Error?.Invoke(this, "Голос: " + Describe(reason));
        }
        else if (reason.Length > 0 && !_connecting)
        {
            VoiceLogin? login;
            lock (_gate) login = _login;
            if (login != null) OnFailed(login, "связь с голосовым сервером потеряна", reason);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnFailed(VoiceLogin login, string what, string reason)
    {
        bool first;
        lock (_gate)
        {
            if (!ReferenceEquals(_login, login)) return;
            first = !_failing;
            _failing = true;
            _retry?.Dispose();
            _retry = new Timer(_ => _ = ConnectAsync(login), null, _retryInterval, Timeout.InfiniteTimeSpan);
        }
        // Report once; the retries every 30 s stay quiet until one succeeds.
        if (first)
            Error?.Invoke(this, $"Голос: {what} ({Describe(reason)}). Повтор каждые {_retryInterval.TotalSeconds:0} с; " +
                                "щёлкните ГОЛОС, чтобы переподключиться сейчас.");
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The voice client's reasons in Russian (server reasons are shown as they are).</summary>
    internal static string Describe(string reason) => reason switch
    {
        "Voice server not responding" => "голосовой сервер не отвечает",
        "Disconnected by the server" => "отключено сервером",
        _ when reason.StartsWith("Cannot resolve ", StringComparison.Ordinal) => "не найден адрес " + reason["Cannot resolve ".Length..],
        _ when reason.StartsWith("Audio device error: ", StringComparison.Ordinal) =>
            "ошибка аудиоустройства: " + reason["Audio device error: ".Length..],
        _ => reason,
    };

    public void Dispose()
    {
        Stop();
        _client.Dispose();
    }
}
