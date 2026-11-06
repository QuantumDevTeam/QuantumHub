using System.Text.RegularExpressions;

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
/// <param name="WindowTitle">Заголовок окна в фокусе.</param>
public sealed record ChatInputs(string? Status, string? WindowProcess, bool IsVr, StatsInfo? Stats, MediaInfo? Media, DateTimeOffset Now, string? WindowTitle = null);

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
/// <param name="WindowSource">Что писать про окно: "process", "title" или "both".</param>
/// <param name="Template">Свой шаблон по строке на элемент. Если он задан, выбор строк и их порядок берутся из него.</param>
public sealed record ChatFormat(
    string VrText = "In VR",
    string TimeFormat = "HH:mm",
    bool ShowProgress = true,
    bool Cpu = true,
    bool Gpu = true,
    bool Ram = true,
    bool Vram = true,
    string WindowSource = "process",
    IReadOnlyList<string>? Template = null);

/// <summary>Собирает текст чатбокса. Чистая функция: на входе данные, на выходе строка.</summary>
public static class ChatboxComposer
{
    /// <summary>Сколько символов принимает чатбокс VRChat.</summary>
    public const int MaxLength = 144;

    private const int BarWidth = 8;

    /// <summary>Подстановка в шаблоне: {имя}.</summary>
    private static readonly Regex Placeholder = new(@"\{(\w+)\}", RegexOptions.Compiled);

    /// <summary>Какие подстановки каких данных требуют.</summary>
    private static readonly string[] WindowKeys = ["window", "process", "title", "vr"];
    private static readonly string[] StatsKeys = ["stats", "cpu", "gpu", "ram", "vram"];
    private static readonly string[] MediaKeys = ["song", "track", "artist", "progress", "position", "duration"];

    /// <summary>Собирает текст. Если места не хватает, первыми уходят наименее важные строки.</summary>
    /// <param name="inputs">Данные.</param>
    /// <param name="modules">Что показывать.</param>
    /// <param name="format">Оформление. По умолчанию всё включено.</param>
    /// <returns>Текст не длиннее <see cref="MaxLength"/>.</returns>
    public static string Compose(ChatInputs inputs, ChatModules modules, ChatFormat? format = null)
    {
        format ??= new ChatFormat();
        if (HasTemplate(format)) return ComposeTemplate(inputs, format);

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

    /// <summary>Задан ли свой шаблон.</summary>
    /// <param name="format">Оформление.</param>
    public static bool HasTemplate(ChatFormat? format) =>
        format?.Template?.Any(line => !string.IsNullOrWhiteSpace(line)) == true;

    /// <summary>Какие данные нужны. С шаблоном это решают его подстановки, чтобы не спрашивать лишнего у других плагинов.</summary>
    /// <param name="modules">Выбранные строки.</param>
    /// <param name="format">Оформление.</param>
    public static ChatModules Needed(ChatModules modules, ChatFormat? format)
    {
        if (!HasTemplate(format)) return modules;
        var used = format!.Template!.SelectMany(line => Placeholder.Matches(line)).Select(match => match.Groups[1].Value).ToHashSet();
        return new ChatModules(used.Contains("status"), used.Overlaps(WindowKeys), used.Overlaps(StatsKeys), used.Contains("time"), used.Overlaps(MediaKeys));
    }

    /// <summary>
    /// Собирает текст по шаблону. Строка, в которой все подстановки оказались пустыми, пропадает: «🎵 {song}» не висит
    /// без музыки. Если места не хватает, уходят нижние строки.
    /// </summary>
    private static string ComposeTemplate(ChatInputs inputs, ChatFormat format)
    {
        var values = Values(inputs, format);
        var lines = new List<string>();
        foreach (var line in format.Template!)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            var matches = Placeholder.Matches(line).Where(match => values.ContainsKey(match.Groups[1].Value)).ToList();
            if (matches.Count > 0 && matches.All(match => values[match.Groups[1].Value].Length == 0)) continue;

            lines.Add(Placeholder.Replace(line, match => values.GetValueOrDefault(match.Groups[1].Value, match.Value)).Trim());
        }

        while (lines.Count > 1 && lines.Sum(line => line.Length) + lines.Count - 1 > MaxLength) lines.RemoveAt(lines.Count - 1);

        var text = string.Join('\n', lines);
        return text.Length <= MaxLength ? text : text[..(MaxLength - 1)] + "…";
    }

    /// <summary>Значения подстановок.</summary>
    private static Dictionary<string, string> Values(ChatInputs inputs, ChatFormat format)
    {
        var media = inputs.Media is { State: not "stopped" } playing && playing.Title.Length > 0 ? playing : null;
        var hasDuration = media?.DurationSec > 0;
        var stats = inputs.Stats;
        return new Dictionary<string, string>
        {
            ["status"] = inputs.Status?.Trim() ?? string.Empty,
            ["window"] = inputs.IsVr ? format.VrText : WindowName(inputs, format) ?? string.Empty,
            ["process"] = inputs.WindowProcess ?? string.Empty,
            ["title"] = inputs.WindowTitle ?? string.Empty,
            ["vr"] = inputs.IsVr ? format.VrText : string.Empty,
            ["time"] = TimeLine(inputs.Now, format),
            ["stats"] = stats is null ? string.Empty : StatsLine(stats, format),
            ["cpu"] = stats is null ? string.Empty : Invariant($"{stats.CpuPercent:0}%"),
            ["gpu"] = stats?.GpuPercent is { } gpu ? Invariant($"{gpu:0}%") : string.Empty,
            ["ram"] = stats is null ? string.Empty : Invariant($"{stats.RamUsedGb:0.0}/{stats.RamTotalGb:0}G"),
            ["vram"] = stats is { VramUsedGb: { } used, VramTotalGb: { } total } ? Invariant($"{used:0.0}/{total:0}G") : string.Empty,
            ["song"] = media is null ? string.Empty : media.Artist.Length > 0 ? $"{media.Title} - {media.Artist}" : media.Title,
            ["track"] = media?.Title ?? string.Empty,
            ["artist"] = media?.Artist ?? string.Empty,
            ["progress"] = hasDuration ? ProgressLine(media!) : string.Empty,
            ["position"] = hasDuration ? Clock(media!.PositionSec) : string.Empty,
            ["duration"] = hasDuration ? Clock(media!.DurationSec) : string.Empty,
        };
    }

    /// <summary>Как назвать окно в фокусе. Если заголовка нет, например он выключен в User Activity, остаётся имя процесса.</summary>
    private static string? WindowName(ChatInputs inputs, ChatFormat format)
    {
        var process = string.IsNullOrWhiteSpace(inputs.WindowProcess) ? null : inputs.WindowProcess;
        var title = string.IsNullOrWhiteSpace(inputs.WindowTitle) ? null : inputs.WindowTitle.Trim();
        return format.WindowSource switch
        {
            "title" => title ?? process,
            "both" when process is not null && title is not null => $"{process}: {title}",
            "both" => title ?? process,
            _ => process,
        };
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
        return WindowName(inputs, format) is { } name ? $"🖥️ {name}" : null;
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
