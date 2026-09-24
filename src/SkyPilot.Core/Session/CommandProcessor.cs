using System.Globalization;
using SkyPilot.Core.Model;
using SkyPilot.Core.Simulation;

namespace SkyPilot.Core.Session;

/// <summary>
/// Handles the text box: plain text goes out on COM1, dot-commands control the client.
/// </summary>
public sealed class CommandProcessor(NetworkSession session, ISimulator sim)
{
    public const string Help =
        ".com1 118.100 / .com2 121.500 — настроить радио\n" +
        ".x 7000 — код ответчика (также .xpdr, .squawk)\n" +
        ".ident — опознавание (IDENT)\n" +
        ".modec — переключить режим ответчика Standby/Mode C\n" +
        ".msg ПОЗЫВНОЙ текст — личное сообщение\n" +
        ".atis СТАНЦИЯ — запросить ATIS (у диспетчера — информацию о нём)\n" +
        ".disconnect — отключиться от сети\n" +
        "Текст без точки отправляется на частоту радио с включённым TX.";

    /// <summary>Execute one line. Returns feedback for the user, or null.</summary>
    public async Task<string?> ExecuteAsync(string line)
    {
        line = line.Trim();
        if (line.Length == 0) return null;
        if (line[0] != '.')
        {
            await session.SendRadioAsync(line).ConfigureAwait(false);
            return null;
        }

        var parts = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        string cmd = parts[0].ToLowerInvariant();
        string arg = parts.Length > 1 ? parts[1] : "";
        switch (cmd)
        {
            case ".com1":
            case ".com2":
                if (!Frequency.TryParse(arg, out var khz)) return "Неверная частота. Пример: .com1 118.100";
                RequireSim();
                sim.SetComFrequency(cmd == ".com1" ? 1 : 2, khz);
                return $"{cmd[1..].ToUpperInvariant()}: {Frequency.Format(khz)}";
            case ".x":
            case ".xpdr":
            case ".squawk":
                if (!TryParseSquawk(arg, out var code)) return "Код ответчика — 4 цифры от 0 до 7";
                RequireSim();
                sim.SetTransponderCode(code);
                return $"Ответчик: {code:0000}";
            case ".ident":
                session.Ident();
                return "IDENT";
            case ".modec":
                session.ModeC = !session.ModeC;
                return session.ModeC ? "Ответчик: Mode C" : "Ответчик: Standby";
            case ".msg":
            case ".chat":
                if (parts.Length < 3) return "Пример: .msg AFL123 привет";
                await session.SendPrivateAsync(arg, parts[2]).ConfigureAwait(false);
                return null;
            case ".atis":
                if (arg.Length == 0) return "Пример: .atis UUEE_ATIS";
                await session.RequestAtisAsync(arg).ConfigureAwait(false);
                return $"Запрос ATIS: {arg.ToUpperInvariant()}";
            case ".disconnect":
                await session.DisconnectAsync().ConfigureAwait(false);
                return null;
            case ".help":
            case ".?":
                return Help;
            default:
                return $"Неизвестная команда {cmd}. Введите .help";
        }
    }

    public static bool TryParseSquawk(string text, out int code)
    {
        code = 0;
        return text.Length == 4 && text.All(c => c is >= '0' and <= '7') &&
               int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out code);
    }

    private void RequireSim()
    {
        if (!sim.IsConnected) throw new InvalidOperationException("Симулятор не подключён");
    }
}
