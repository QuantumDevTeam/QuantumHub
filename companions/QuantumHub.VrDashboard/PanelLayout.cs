using QuantumHub.Contracts.Ui;

namespace QuantumHub.VrDashboard;

/// <summary>Что показывает панель. Данные другого плагина пропадают вместе с ним.</summary>
/// <param name="HasAudio">Доступен ли VR Switcher.</param>
/// <param name="OnHeadset">Звук на гарнитуре.</param>
/// <param name="HasDiscord">Доступен ли Discord.</param>
/// <param name="DiscordReady">Можно ли управлять голосом.</param>
/// <param name="Muted">Микрофон выключен.</param>
/// <param name="Deafened">Звук выключен.</param>
/// <param name="HasSleep">Доступен ли Pico Sleep Mode.</param>
/// <param name="SleepOn">Режим включён.</param>
/// <param name="HasChatbox">Доступен ли чатбокс.</param>
/// <param name="GameRunning">Запущен ли VRChat.</param>
/// <param name="Sending">Включена ли отправка.</param>
/// <param name="Status">Личный статус.</param>
/// <param name="Statuses">Сохранённые статусы.</param>
/// <param name="Preview">Что сейчас в чатбоксе.</param>
public sealed record PanelState(
    bool HasAudio = false,
    bool OnHeadset = false,
    bool HasDiscord = false,
    bool DiscordReady = false,
    bool Muted = false,
    bool Deafened = false,
    bool HasSleep = false,
    bool SleepOn = false,
    bool HasChatbox = false,
    bool GameRunning = false,
    bool Sending = false,
    string Status = "",
    IReadOnlyList<string>? Statuses = null,
    string Preview = "");

/// <summary>Какие разделы показывать. Настраивается на странице плагина.</summary>
/// <param name="Audio">Звук.</param>
/// <param name="Discord">Discord.</param>
/// <param name="Sleep">Режим сна.</param>
/// <param name="Chatbox">Чатбокс.</param>
/// <param name="FlipPointer">Считать координаты луча снизу вверх. Так их присылает SteamVR.</param>
public sealed record PanelOptions(bool Audio = true, bool Discord = true, bool Sleep = true, bool Chatbox = true, bool FlipPointer = true);

/// <summary>Прямоугольник на панели.</summary>
/// <param name="X">Левый край.</param>
/// <param name="Y">Верхний край.</param>
/// <param name="Width">Ширина.</param>
/// <param name="Height">Высота.</param>
public readonly record struct PanelRect(double X, double Y, double Width, double Height)
{
    /// <summary>Попадает ли точка внутрь.</summary>
    /// <param name="x">Координата X.</param>
    /// <param name="y">Координата Y.</param>
    public bool Contains(double x, double y) => x >= X && x < X + Width && y >= Y && y < Y + Height;
}

/// <summary>Кнопка на панели.</summary>
/// <param name="Id">Что она делает.</param>
/// <param name="Bounds">Где она.</param>
/// <param name="Title">Подпись.</param>
/// <param name="Value">Текущее значение.</param>
/// <param name="Active">Подсвечена как включённая.</param>
/// <param name="Enabled">Можно ли нажать.</param>
public sealed record PanelTile(string Id, PanelRect Bounds, string Title, string Value, bool Active, bool Enabled);

/// <summary>Вся панель: кнопки и место для текста чатбокса.</summary>
/// <param name="Tiles">Кнопки.</param>
/// <param name="Preview">Место для текста чатбокса или <see langword="null"/>, если раздел скрыт.</param>
public sealed record PanelView(IReadOnlyList<PanelTile> Tiles, PanelRect? Preview);

/// <summary>Раскладка панели. Отдельно от рисования и VR, поэтому проверяется тестами.</summary>
public static class PanelLayout
{
    /// <summary>Ширина панели в пикселях.</summary>
    public const int Width = 1024;
    /// <summary>Высота панели в пикселях.</summary>
    public const int Height = 640;

    /// <summary>Id кнопки звука.</summary>
    public const string Audio = "audio";
    /// <summary>Id кнопки микрофона Discord.</summary>
    public const string Mute = "mute";
    /// <summary>Id кнопки звука Discord.</summary>
    public const string Deafen = "deafen";
    /// <summary>Id кнопки режима сна.</summary>
    public const string Sleep = "sleep";
    /// <summary>Id кнопки ввода статуса.</summary>
    public const string EditStatus = "status.edit";
    /// <summary>Id кнопки ввода сообщения.</summary>
    public const string SendChat = "chat.send";
    /// <summary>Id кнопки отправки в VRChat.</summary>
    public const string Sending = "sending";
    /// <summary>Начало id кнопки сохранённого статуса: дальше идёт её номер.</summary>
    public const string SavedStatus = "status.";

    private const double Margin = 24;
    private const double Gap = 12;
    private const double TopRowY = 72;
    private const double TopRowHeight = 128;
    private const double PreviewHeight = 150;
    private const double ButtonHeight = 76;
    private const double SavedHeight = 64;
    private const int MaxSavedStatuses = 4;

    /// <summary>Считает раскладку.</summary>
    /// <param name="state">Данные.</param>
    /// <param name="options">Какие разделы показывать.</param>
    public static PanelView Build(PanelState state, PanelOptions options)
    {
        var top = new List<(string Id, string Title, string Value, bool Active, bool Enabled)>();
        if (options.Audio && state.HasAudio)
            top.Add((Audio, Loc.T("Звук", "Sound"), state.OnHeadset ? Loc.T("Гарнитура", "Headset") : Loc.T("Компьютер", "Desktop"), state.OnHeadset, true));
        if (options.Discord && state.HasDiscord)
        {
            top.Add((Mute, Loc.T("Микрофон Discord", "Discord mic"), state.Muted ? Loc.T("Выключен", "Muted") : Loc.T("Включён", "On"), state.Muted, state.DiscordReady));
            top.Add((Deafen, Loc.T("Звук Discord", "Discord sound"), state.Deafened ? Loc.T("Выключен", "Deafened") : Loc.T("Включён", "On"), state.Deafened, state.DiscordReady));
        }

        if (options.Sleep && state.HasSleep)
            top.Add((Sleep, Loc.T("Режим сна", "Sleep mode"), state.SleepOn ? Loc.T("Не спит", "Staying awake") : Loc.T("Выключен", "Off"), state.SleepOn, true));

        var tiles = new List<PanelTile>();
        var inner = Width - Margin * 2;
        for (var i = 0; i < top.Count; i++)
        {
            var width = (inner - Gap * (top.Count - 1)) / top.Count;
            var (id, title, value, active, enabled) = top[i];
            tiles.Add(new PanelTile(id, new PanelRect(Margin + i * (width + Gap), TopRowY, width, TopRowHeight), title, value, active, enabled));
        }

        if (!options.Chatbox || !state.HasChatbox) return new PanelView(tiles, null);

        var y = (top.Count > 0 ? TopRowY + TopRowHeight : TopRowY) + Gap;
        var preview = new PanelRect(Margin, y, inner, PreviewHeight);
        y += PreviewHeight + Gap;

        var third = (inner - Gap * 2) / 3;
        tiles.Add(new PanelTile(EditStatus, new PanelRect(Margin, y, third, ButtonHeight), Loc.T("Статус", "Status"), state.Status, false, true));
        tiles.Add(new PanelTile(SendChat, new PanelRect(Margin + third + Gap, y, third, ButtonHeight), Loc.T("Сообщение", "Message"), Loc.T("Написать", "Write"), false, true));
        tiles.Add(new PanelTile(Sending, new PanelRect(Margin + (third + Gap) * 2, y, third, ButtonHeight), Loc.T("Отправка в VRChat", "Send to VRChat"),
            state.Sending ? Loc.T("Включена", "On") : Loc.T("Выключена", "Off"), state.Sending, true));
        y += ButtonHeight + Gap;

        var saved = (state.Statuses ?? []).Take(MaxSavedStatuses).ToList();
        for (var i = 0; i < saved.Count; i++)
        {
            var width = (inner - Gap * (saved.Count - 1)) / saved.Count;
            tiles.Add(new PanelTile(SavedStatus + i, new PanelRect(Margin + i * (width + Gap), y, width, SavedHeight), saved[i], string.Empty, saved[i] == state.Status, true));
        }

        return new PanelView(tiles, preview);
    }

    /// <summary>Находит кнопку под точкой.</summary>
    /// <param name="view">Панель.</param>
    /// <param name="x">Координата X в пикселях панели.</param>
    /// <param name="y">Координата Y в пикселях панели, сверху вниз.</param>
    /// <returns>Кнопка, которую можно нажать, или <see langword="null"/>.</returns>
    public static PanelTile? HitTest(PanelView view, double x, double y) =>
        view.Tiles.FirstOrDefault(tile => tile.Enabled && tile.Bounds.Contains(x, y));
}
