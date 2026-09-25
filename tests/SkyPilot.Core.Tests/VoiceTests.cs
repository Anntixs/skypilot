using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using SkyNetwork.Voice;
using SkyPilot.Core.Model;
using SkyPilot.Core.Settings;
using SkyPilot.Core.Voice;

namespace SkyPilot.Core.Tests;

public class VoiceTests
{
    [Theory]
    [InlineData(118100, 118_100_000u)]
    [InlineData(118105, 118_100_000u)]  // 8.33 kHz name of the same frequency
    [InlineData(118000, 118_000_000u)]
    [InlineData(118005, 118_000_000u)]
    [InlineData(118008, 118_010_000u)]  // MSFS reports 118.010 as 118.00833
    [InlineData(118017, 118_015_000u)]  // and 118.015 as 118.01667
    [InlineData(118033, 118_035_000u)]
    [InlineData(118025, 118_025_000u)]
    [InlineData(121500, 121_500_000u)]
    [InlineData(136990, 136_990_000u)]
    [InlineData(117950, 0u)]
    [InlineData(0, 0u)]
    public void ChannelHz_MatchesChannelNames(int khz, uint hz) => Assert.Equal(hz, PilotRadios.ChannelHz(khz));

    [Fact]
    public void ComRadios_MapToVoiceRadios()
    {
        var radios = PilotRadios.Build(118100, 121500, com1Rx: true, com2Rx: false, txRadio: 1);
        Assert.Equal([new Radio(118_100_000, true, true), new Radio(121_500_000, false, false)], radios);

        radios = PilotRadios.Build(118100, 0, com1Rx: false, com2Rx: true, txRadio: 2);
        Assert.Equal([new Radio(118_100_000, false, false), new Radio(0, true, true)], radios);
    }

    [Fact]
    public void Site_IsTheAircraft()
    {
        var site = PilotRadios.Site(new AircraftState(55.97, 37.41, 3500, 0, 0, 90, 150, false));
        Assert.Equal(new AntennaSite(55.97, 37.41, 3500), site);
    }

    [Fact]
    public void ReceiveTracker_ShowsWhoIsHeardPerFrequency()
    {
        var t = new ReceiveTracker();
        t.Update("afl123", 118_100_000, true);
        t.Update("SBI45", 118_100_000, true);
        t.Update("UUEE_TWR", 121_500_000, true);
        Assert.Equal("AFL123, SBI45", t.On(118_100_000));
        Assert.Equal("UUEE_TWR", t.On(121_500_000));
        Assert.Equal("", t.On(0));

        t.Update("AFL123", 118_100_000, false);
        Assert.Equal("SBI45", t.On(118_100_000));
        t.Clear();
        Assert.Equal("", t.On(121_500_000));
    }

    [Fact]
    public void VoiceSettings_FromAppSettings()
    {
        var s = new AppSettings
        {
            InputDevice = "USB Headset", OutputDevice = "Gone", MicGain = 1.5, OutputVolume = 9, PttKey = "joy:1:4",
        };
        var v = PilotRadios.ToVoiceSettings(s, ["Realtek Mic", "USB Headset"], ["Speakers"]);
        Assert.Equal(1, v.InputDevice);
        Assert.Equal(-1, v.OutputDevice); // device unplugged: Windows default
        Assert.Equal(1.5f, v.MicGain);
        Assert.Equal(2f, v.OutputVolume);
        Assert.Equal(new PttBinding(PttKind.Joystick, 4, 1), v.Ptt);

        var defaults = PilotRadios.ToVoiceSettings(new AppSettings(), [], []);
        Assert.Equal(-1, defaults.InputDevice);
        Assert.Equal(-1, defaults.OutputDevice);
        Assert.Equal(PttBinding.None, defaults.Ptt);
    }

    [Fact]
    public void Settings_RoundTripVoiceFields()
    {
        var path = Path.Combine(Path.GetTempPath(), $"skypilot-{Guid.NewGuid():N}", "settings.json");
        try
        {
            new AppSettings
            {
                Cid = 1000001, VoicePort = 4000, InputDevice = "Mic", OutputDevice = "Phones", MicGain = 2, OutputVolume = 0.5,
                PttKey = new PttBinding(PttKind.Keyboard, 0xA3).ToString(),
            }.Save(path);
            var s = AppSettings.Load(path);
            Assert.Equal(4000, s.VoicePort);
            Assert.Equal("Mic", s.InputDevice);
            Assert.Equal("Phones", s.OutputDevice);
            Assert.Equal(2, s.MicGain);
            Assert.Equal(0.5, s.OutputVolume);
            Assert.Equal(new PttBinding(PttKind.Keyboard, 0xA3), PttBinding.Parse(s.PttKey));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void Settings_OldFileGetsVoiceDefaults()
    {
        var path = Path.Combine(Path.GetTempPath(), $"skypilot-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """{ "Cid": 1000001, "RealName": "Ivan Petrov", "KeepWindowOnTop": true }""");
            var s = AppSettings.Load(path);
            Assert.Equal(1000001, s.Cid);
            Assert.Equal(3782, s.VoicePort);
            Assert.Equal("", s.InputDevice);
            Assert.Equal("", s.OutputDevice);
            Assert.Equal(1, s.MicGain);
            Assert.Equal(1, s.OutputVolume);
            Assert.Equal("", s.PttKey);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task PilotVoice_SignsInAndPublishesComRadios()
    {
        using var server = new FakeVoiceServer();
        using var voice = new PilotVoice();
        var errors = new ConcurrentQueue<string>();
        voice.Error += (_, e) => errors.Enqueue(e);
        voice.UpdateRadios(118100, 121500, com1Rx: true, com2Rx: true, txRadio: 1);
        voice.UpdatePosition(new AircraftState(55.97, 37.41, 3000, 0, 0, 90, 150, false));

        voice.Start(new VoiceLogin("127.0.0.1", server.Port, 1000001, "AFL123", "secret"));
        var auth = server.Expect(1);
        Assert.Equal((1000001u, "AFL123", "secret"), FakeVoiceServer.ParseAuth(auth));
        await WaitUntil(() => voice.State == VoiceState.Connected);

        var transceivers = FakeVoiceServer.ParseTransceivers(server.Expect(4));
        Assert.Equal([118_100_000u, 121_500_000u], transceivers.Select(t => t.Freq));
        Assert.All(transceivers, t => Assert.Equal(55.97, t.Lat, 3));

        // Tuning COM2 sends the new transceivers.
        voice.UpdateRadios(118100, 124350, com1Rx: true, com2Rx: true, txRadio: 1);
        Assert.Equal([118_100_000u, 124_350_000u], FakeVoiceServer.ParseTransceivers(server.Expect(4)).Select(t => t.Freq));

        voice.Stop();
        server.Expect(9); // bye
        Assert.Equal(VoiceState.Disconnected, voice.State);
        Assert.Empty(ConnectionErrors(errors));
    }

    [Fact]
    public async Task PilotVoice_ServerDown_ReportsOnceAndRetries()
    {
        int port;
        using (var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0))) port = ((IPEndPoint)probe.Client.LocalEndPoint!).Port;

        using var voice = new PilotVoice(TimeSpan.FromMilliseconds(300));
        var errors = new ConcurrentQueue<string>();
        var infos = new ConcurrentQueue<string>();
        voice.Error += (_, e) => errors.Enqueue(e);
        voice.Info += (_, e) => infos.Enqueue(e);
        voice.Start(new VoiceLogin("127.0.0.1", port, 1000001, "AFL123", "secret"));

        await WaitUntil(() => voice.Failing, 8000);
        Assert.StartsWith("Voice: no connection to the voice server", Assert.Single(ConnectionErrors(errors)));

        // The server comes up: the next retry connects.
        using var server = new FakeVoiceServer(port);
        await WaitUntil(() => voice.State == VoiceState.Connected, 10000);
        Assert.False(voice.Failing);
        Assert.Single(ConnectionErrors(errors));
        Assert.Contains(infos, i => i.StartsWith("Voice: connected", StringComparison.Ordinal));
    }

    [Fact]
    public void Describe_TranslatesClientReasons()
    {
        Assert.Equal("voice server not responding", PilotVoice.Describe("Voice server not responding"));
        Assert.Equal("cannot resolve voice.example", PilotVoice.Describe("Cannot resolve voice.example"));
        Assert.Equal("Invalid password", PilotVoice.Describe("Invalid password"));
    }

    /// <summary>
    /// Errors other than the audio device ones: a build machine has no sound card, so opening the
    /// microphone and speakers fails there (and only there) after connecting.
    /// </summary>
    private static List<string> ConnectionErrors(IEnumerable<string> errors) =>
        errors.Where(e => !e.Contains("audio device error", StringComparison.Ordinal)).ToList();

    private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException();
            await Task.Delay(20);
        }
    }
}

/// <summary>Minimal voice server: accepts every sign-in and records the datagrams it gets.</summary>
public sealed class FakeVoiceServer : IDisposable
{
    private readonly UdpClient _udp;
    private readonly BlockingCollection<byte[]> _received = new();
    private readonly CancellationTokenSource _cts = new();

    public FakeVoiceServer(int port = 0)
    {
        _udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));
        _ = Task.Run(Loop);
    }

    public int Port => ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;

    private async Task Loop()
    {
        while (!_cts.IsCancellationRequested)
        {
            UdpReceiveResult r;
            try { r = await _udp.ReceiveAsync(_cts.Token); }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException) { return; }
            catch (SocketException) { continue; }
            if (r.Buffer.Length < 4 || r.Buffer[0] != 'S' || r.Buffer[1] != 'K') continue;
            _received.Add(r.Buffer);
            if (r.Buffer[3] == 1) // auth: reply AuthOk with a token
                await _udp.SendAsync(new byte[] { (byte)'S', (byte)'K', 1, 2, 0, 0, 0, 42 }, r.RemoteEndPoint);
            else if (r.Buffer[3] == 7) // keepalive
                await _udp.SendAsync(new byte[] { (byte)'S', (byte)'K', 1, 8 }, r.RemoteEndPoint);
        }
    }

    /// <summary>The next datagram of a type (skipping others).</summary>
    public byte[] Expect(byte type, int timeoutMs = 3000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (_received.TryTake(out var d, Math.Max(0, (int)(deadline - DateTime.UtcNow).TotalMilliseconds)))
            if (d[3] == type) return d;
        throw new TimeoutException($"No datagram of type {type}");
    }

    public static (uint Cid, string Callsign, string Password) ParseAuth(byte[] d)
    {
        uint cid = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(4));
        int n = d[8];
        string callsign = Encoding.UTF8.GetString(d, 9, n);
        int m = d[9 + n];
        return (cid, callsign, Encoding.UTF8.GetString(d, 10 + n, m));
    }

    public static List<(uint Freq, double Lat, double Lon, double Alt)> ParseTransceivers(byte[] d)
    {
        var list = new List<(uint, double, double, double)>();
        int count = d[8], pos = 9;
        for (int i = 0; i < count; i++, pos += 29)
            list.Add((BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(pos + 1)),
                BinaryPrimitives.ReadDoubleBigEndian(d.AsSpan(pos + 5)),
                BinaryPrimitives.ReadDoubleBigEndian(d.AsSpan(pos + 13)),
                BinaryPrimitives.ReadDoubleBigEndian(d.AsSpan(pos + 21))));
        return list;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _udp.Dispose();
    }
}
