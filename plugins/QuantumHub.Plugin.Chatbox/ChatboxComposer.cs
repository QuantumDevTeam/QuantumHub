namespace QuantumHub.Plugin.Chatbox;

/// <summary>Трек из возможности media.player.</summary>
/// <param name="Title">Название.</param>
/// <param name="Artist">Исполнитель.</param>
/// <param name="State">"playing", "paused" или "stopped".</param>
/// <param name="PositionSec">Позиция.</param>
/// <param name="DurationSec">Длительность.</param>
public sealed record MediaInfo(string Title, string Artist, string State, double PositionSec, double DurationSec);

/// <summary>Загрузка из возможности system.stats.</summary>
/// <param name="CpuPercent">Процессор.</param>
/// <param name="RamUsedGb">Занятая память.</param>
/// <param name="RamTotalGb">Вся память.</param>
/// <param name="GpuPercent">Видеокарта.</param>
/// <param name="VramUsedGb">Занятая видеопамять.</param>
/// <param name="VramTotalGb">Вся видеопамять.</param>
public sealed record StatsInfo(double CpuPercent, double RamUsedGb, double RamTotalGb, double? GpuPercent, double? VramUsedGb, double? VramTotalGb);

/// <summary>Всё, из чего собирается чатбокс.</summary>
/// <param name="Status">Личный статус.</param>
/// <param name="WindowProcess">Приложение в фокусе.</param>
/// <param name="IsVr">Запущен ли VR.</param>
/// <param name="Stats">Загрузка компьютера.</param>
/// <param name="Media">Музыка.</param>
/// <param name="Now">Текущее время.</param>
public sealed record ChatInputs(string? Status, string? WindowProcess, bool IsVr, StatsInfo? Stats, MediaInfo? Media, DateTimeOffset Now);

/// <summary>Какие строки показывать.</summary>
/// <param name="Status">Личный статус.</param>
/// <param name="Window">Активность окна.</param>
/// <param name="Component">Загрузка компьютера.</param>
/// <param name="Time">Время.</param>
/// <param name="Media">Музыка.</param>
public sealed record ChatModules(bool Status, bool Window, bool Component, bool Time, bool Media);

/// <summary>Оформление строк.</summary>
/// <param name="VrText">Текст строки окна в VR.</param>
/// <param name="TimeFormat">Формат времени .NET.</param>
/// <param name="ShowProgress">Показывать полосу прогресса трека.</param>
/// <param name="Cpu">Показывать процессор.</param>
/// <param name="Gpu">Показывать видеокарту.</param>
/// <param name="Ram">Показывать память.</param>
/// <param name="Vram">Показывать видеопамять.</param>
public sealed record ChatFormat(
    string VrText = "In VR",
    string TimeFormat = "HH:mm",
    bool ShowProgress = true,
    bool Cpu = true,
    bool Gpu = true,
    bool Ram = true,
    bool Vram = true);

/// <summary>Собирает текст чатбокса. Чистая функция: на входе данные, на выходе строка.</summary>
public static class ChatboxComposer
{
    /// <summary>Сколько символов принимает чатбокс VRChat.</summary>
    public const int MaxLength = 144;

    private const int BarWidth = 8;

    /// <summary>Собирает текст. Если места не хватает, первыми уходят наименее важные строки.</summary>
    /// <param name="inputs">Данные.</param>
    /// <param name="modules">Что показывать.</param>
    /// <param name="format">Оформление. По умолчанию всё включено.</param>
    /// <returns>Текст не длиннее <see cref="MaxLength"/>.</returns>
    public static string Compose(ChatInputs inputs, ChatModules modules, ChatFormat? format = null)
    {
        format ??= new ChatFormat();

        // Чем больше число, тем раньше строка уйдёт при нехватке места. Порядок в списке — порядок на экране.
        var lines = new List<(string Text, int DropOrder)>();
        if (modules.Status && !string.IsNullOrWhiteSpace(inputs.Status)) lines.Add(($"💬 {inputs.Status.Trim()}", 0));
        if (modules.Window && WindowLine(inputs, format) is { } window) lines.Add((window, 4));
        if (modules.Component && inputs.Stats is { } stats && StatsLine(stats, format) is { Length: > 0 } load) lines.Add((load, 5));
        if (modules.Time) lines.Add((TimeLine(inputs.Now, format), 3));

        if (modules.Media && inputs.Media is { State: not "stopped" } media && media.Title.Length > 0)
        {
            if (format.ShowProgress && media.DurationSec > 0) lines.Add((ProgressLine(media), 2));
            lines.Add((TrackLine(media), 1));
        }

        while (lines.Count > 1 && Length(lines) > MaxLength)
            lines.Remove(lines.MaxBy(line => line.DropOrder));

        var text = string.Join('\n', lines.Select(line => line.Text));
        return text.Length <= MaxLength ? text : text[..(MaxLength - 1)] + "…";
    }

    private static int Length(List<(string Text, int DropOrder)> lines) =>
        lines.Sum(line => line.Text.Length) + lines.Count - 1;

    /// <summary>Неверный формат в настройках не должен ломать весь чатбокс.</summary>
    private static string TimeLine(DateTimeOffset now, ChatFormat format)
    {
        try
        {
            return now.ToString(format.TimeFormat, System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (FormatException)
        {
            return now.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    private static string? WindowLine(ChatInputs inputs, ChatFormat format)
    {
        if (inputs.IsVr) return $"🥽 {format.VrText}";
        return string.IsNullOrEmpty(inputs.WindowProcess) ? null : $"🖥️ {inputs.WindowProcess}";
    }

    private static string StatsLine(StatsInfo stats, ChatFormat format)
    {
        var parts = new List<string>();
        if (format.Cpu) parts.Add(Invariant($"CPU {stats.CpuPercent:0}%"));
        if (format.Gpu && stats.GpuPercent is { } gpu) parts.Add(Invariant($"GPU {gpu:0}%"));
        if (format.Ram) parts.Add(Invariant($"RAM {stats.RamUsedGb:0.0}/{stats.RamTotalGb:0}G"));
        if (format.Vram && stats is { VramUsedGb: { } used, VramTotalGb: { } total }) parts.Add(Invariant($"VRAM {used:0.0}/{total:0}G"));
        return string.Join(" ", parts);
    }

    private static string Invariant(FormattableString text) => FormattableString.Invariant(text);

    private static string ProgressLine(MediaInfo media)
    {
        var filled = (int)Math.Round(Math.Clamp(media.PositionSec / media.DurationSec, 0, 1) * BarWidth);
        var bar = new string('━', filled) + "●" + new string('─', BarWidth - filled);
        return $"{Clock(media.PositionSec)} {bar} {Clock(media.DurationSec)}";
    }

    private static string TrackLine(MediaInfo media)
    {
        var icon = media.State == "paused" ? "⏸" : "🎵";
        return media.Artist.Length > 0 ? $"{icon} '{media.Title}' ᵇʸ {media.Artist}" : $"{icon} '{media.Title}'";
    }

    private static string Clock(double seconds)
    {
        var time = TimeSpan.FromSeconds(seconds);
        return $"{(int)time.TotalMinutes}:{time.Seconds:00}";
    }
}
