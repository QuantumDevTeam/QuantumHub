using System.Text.Json.Nodes;
using QuantumHub.Contracts.Bus;
using QuantumHub.Contracts.Proto;

namespace QuantumHub.VrDashboard;

/// <summary>
/// Связь панели с плагинами. Панель не знает их устройства: спрашивает возможности через хаб, а если какого-то плагина
/// нет или он выключен, его раздел просто не показывается.
/// </summary>
/// <param name="bus">Шина хаба.</param>
public sealed class PanelClient(IHubBus bus)
{
    /// <summary>Id плагина, чьи настройки читает панель.</summary>
    private const string PluginId = "qh.vr.dashboard";
    /// <summary>Сколько ждать ответа: в шлеме зависшая панель хуже пустого раздела.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    private sealed record VrSession(bool OnHeadset);
    private sealed record DiscordState(bool Connected, bool Authorized, bool Muted, bool Deafened);
    private sealed record SleepState(bool Enabled);
    private sealed record ChatboxState(bool GameRunning, bool Sending, string Status, IReadOnlyList<string> Statuses, string Preview);

    /// <summary>Собирает данные со всех плагинов.</summary>
    /// <param name="options">Какие разделы показаны: про остальные не спрашиваем.</param>
    public async Task<PanelState> ReadAsync(PanelOptions options)
    {
        var session = options.Audio ? await TryAsync<VrSession>("cap:vr.session", "session.get") : null;
        var discord = options.Discord ? await TryAsync<DiscordState>("cap:discord.client", "discord.state") : null;
        var sleep = options.Sleep ? await TryAsync<SleepState>("cap:vr.pico.sleep", "sleep.get") : null;
        var chat = options.Chatbox ? await TryAsync<ChatboxState>("cap:vrchat.chatbox", "chatbox.state") : null;

        return new PanelState(
            session is not null, session?.OnHeadset == true,
            discord is not null, discord is { Connected: true, Authorized: true }, discord?.Muted == true, discord?.Deafened == true,
            sleep is not null, sleep?.Enabled == true,
            chat is not null, chat?.GameRunning == true, chat?.Sending == true, chat?.Status ?? string.Empty, chat?.Statuses, chat?.Preview ?? string.Empty);
    }

    /// <summary>Читает настройки панели.</summary>
    public async Task<PanelOptions> ReadOptionsAsync()
    {
        try
        {
            var value = await bus.CallAsync<ConfigValue>("hub", "hub.config.get", new ConfigGetRequest { PluginId = PluginId });
            var config = value.Json.Length > 0 ? JsonNode.Parse(value.Json) : null;
            bool Flag(string key, bool fallback = true) => config?[key]?.GetValue<bool>() ?? fallback;
            return new PanelOptions(Flag("showAudio"), Flag("showDiscord"), Flag("showSleep"), Flag("showChatbox"), Flag("flipPointer", false));
        }
        catch (HubCallException)
        {
            return new PanelOptions();
        }
    }

    /// <summary>Выполняет нажатие на кнопку.</summary>
    /// <param name="id">Id кнопки.</param>
    /// <param name="state">Что сейчас показано на панели.</param>
    public async Task PressAsync(string id, PanelState state)
    {
        switch (id)
        {
            case PanelLayout.Audio:
                await bus.CallAsync("cap:vr.session", "audio.target.set", HubPayload.Json(new { target = state.OnHeadset ? "desktop" : "headset" }), Timeout * 5);
                break;
            case PanelLayout.Mute:
                await bus.CallAsync("cap:discord.client", "voice.mute", HubPayload.Json(new { value = !state.Muted }), Timeout);
                break;
            case PanelLayout.Deafen:
                await bus.CallAsync("cap:discord.client", "voice.deafen", HubPayload.Json(new { value = !state.Deafened }), Timeout);
                break;
            case PanelLayout.Sleep:
                await bus.CallAsync("cap:vr.pico.sleep", "sleep.set", HubPayload.Json(new { enabled = !state.SleepOn }), Timeout * 5);
                break;
            case PanelLayout.Sending:
                await bus.CallAsync("cap:vrchat.chatbox", "chatbox.sending.set", HubPayload.Text(state.Sending ? "false" : "true"), Timeout);
                break;
            default:
                if (id.StartsWith(PanelLayout.SavedStatus, StringComparison.Ordinal) &&
                    int.TryParse(id[PanelLayout.SavedStatus.Length..], out var index) && state.Statuses?.ElementAtOrDefault(index) is { } text)
                    await SetStatusAsync(text);
                break;
        }
    }

    /// <summary>Ставит личный статус.</summary>
    /// <param name="text">Текст.</param>
    public async Task SetStatusAsync(string text) =>
        await bus.CallAsync("cap:vrchat.chatbox", "chatbox.status.set", HubPayload.Text(text), Timeout);

    /// <summary>Отправляет сообщение в чатбокс.</summary>
    /// <param name="text">Текст.</param>
    public async Task SendChatAsync(string text) =>
        await bus.CallAsync("cap:vrchat.chatbox", "chatbox.chat.send", HubPayload.Text(text), Timeout);

    /// <summary>Спрашивает плагин. Если его нет, это обычная ситуация, а не ошибка.</summary>
    private async Task<T?> TryAsync<T>(string target, string method) where T : class
    {
        try
        {
            return (await bus.CallAsync(target, method, timeout: Timeout)).AsJson<T>();
        }
        catch (HubCallException)
        {
            return null;
        }
    }
}
