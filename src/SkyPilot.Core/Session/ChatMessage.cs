namespace SkyPilot.Core.Session;

public enum MessageKind
{
    /// <summary>Text on a frequency the user is tuned to.</summary>
    Radio,
    /// <summary>Private message to or from the user.</summary>
    Private,
    /// <summary>Message from the server (MOTD, notices).</summary>
    Server,
    /// <summary>Network-wide broadcast from a supervisor.</summary>
    Broadcast,
    /// <summary>Client-side information.</summary>
    Info,
    Error,
}

/// <param name="Peer">For private messages: the other party's callsign.</param>
/// <param name="Outgoing">True if the user sent this message.</param>
public sealed record ChatMessage(
    MessageKind Kind,
    string From,
    string Text,
    DateTime Time,
    string? Peer = null,
    int? FrequencyKhz = null,
    bool Outgoing = false);
