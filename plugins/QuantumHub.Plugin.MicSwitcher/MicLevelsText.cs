using System.Text;
using QuantumHub.Contracts.Ui;

namespace QuantumHub.Plugin.MicSwitcher;

/// <summary>Рисует уровни текстом: другого индикатора декларативный интерфейс не имеет.</summary>
public static class MicLevelsText
{
    private const int Width = 20;

    /// <summary>Собирает текст для страницы.</summary>
    /// <param name="mics">Микрофоны.</param>
    /// <param name="threshold">Порог от 0 до 1. Показывается в шкале чертой.</param>
    public static string Format(IEnumerable<MicState> mics, double threshold) =>
        string.Join('\n', mics.Select(mic => Line(mic, threshold)));

    private static string Line(MicState mic, double threshold)
    {
        var marker = mic.IsDefault ? "▶" : " ";
        return mic.Listening
            ? $"{marker} {Bar(mic.Peak, threshold)} {mic.Peak * 100,3:0}%  {mic.Name}"
            : $"{marker} {mic.Name}: {Reason(mic)}";
    }

    private static string Bar(double peak, double threshold)
    {
        var filled = (int)Math.Round(Math.Clamp(peak, 0, 1) * Width);
        var mark = Math.Min((int)Math.Round(Math.Clamp(threshold, 0, 1) * Width), Width - 1);
        var bar = new StringBuilder(Width);
        for (var i = 0; i < Width; i++) bar.Append(i == mark ? '|' : i < filled ? '█' : '░');
        return bar.ToString();
    }

    private static string Reason(MicState mic) => mic.Excluded switch
    {
        MicFilter.Bluetooth => Loc.T("Bluetooth, не слушается, чтобы не портить звук", "Bluetooth, not listened to so the sound stays good"),
        MicFilter.Blacklisted => Loc.T("в чёрном списке", "on the blacklist"),
        MicFilter.NotWhitelisted => Loc.T("нет в белом списке", "not on the whitelist"),
        _ => Loc.T("не слушается", "not listened to"),
    };
}
