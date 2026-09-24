using System.Collections.ObjectModel;
using System.Windows.Media;
using SkyPilot.Core.Session;

namespace SkyPilot.App.ViewModels;

public sealed record ChatLine(string Time, string From, string Text, Brush Color);

/// <summary>The radio tab (Peer == null) or a private conversation.</summary>
public sealed class ChatTab(string title, string? peer) : Observable
{
    private const int MaxLines = 1000;
    private bool _unread;

    public string Title { get; } = title;
    public string? Peer { get; } = peer;
    public ObservableCollection<ChatLine> Lines { get; } = [];

    public bool Unread
    {
        get => _unread;
        set => Set(ref _unread, value);
    }

    public void Add(ChatMessage m)
    {
        string from = m.Kind switch
        {
            MessageKind.Radio when m.FrequencyKhz is { } f => $"{m.From} [{Core.Model.Frequency.Format(f)}]",
            MessageKind.Broadcast => $"{m.From} [ВСЕМ]",
            MessageKind.Atis => $"{m.From} [ATIS]",
            _ => m.From,
        };
        Lines.Add(new ChatLine(m.Time.ToLocalTime().ToString("HH:mm:ss"), from, m.Text, ColorFor(m)));
        if (Lines.Count > MaxLines) Lines.RemoveAt(0);
    }

    private static readonly Brush Normal = Freeze(new SolidColorBrush(Color.FromRgb(0xE6, 0xE9, 0xED)));
    private static readonly Brush Mine = Freeze(new SolidColorBrush(Color.FromRgb(0x8B, 0x95, 0xA1)));
    private static readonly Brush Server = Freeze(new SolidColorBrush(Color.FromRgb(0x4C, 0xC3, 0x8A)));
    private static readonly Brush Private = Freeze(new SolidColorBrush(Color.FromRgb(0x3F, 0xA9, 0xF5)));
    private static readonly Brush Warning = Freeze(new SolidColorBrush(Color.FromRgb(0xF0, 0xB4, 0x29)));
    private static readonly Brush Atis = Freeze(new SolidColorBrush(Color.FromRgb(0xB6, 0x9C, 0xF5)));
    private static readonly Brush Error = Freeze(new SolidColorBrush(Color.FromRgb(0xE5, 0x53, 0x4B)));

    private static Brush Freeze(SolidColorBrush b)
    {
        b.Freeze();
        return b;
    }

    private static Brush ColorFor(ChatMessage m) => m.Kind switch
    {
        _ when m.Outgoing => Mine,
        MessageKind.Server or MessageKind.Info => Server,
        MessageKind.Private => Private,
        MessageKind.Broadcast => Warning,
        MessageKind.Atis => Atis,
        MessageKind.Error => Error,
        _ => Normal,
    };
}
