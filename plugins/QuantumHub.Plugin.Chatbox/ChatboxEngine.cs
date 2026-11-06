namespace QuantumHub.Plugin.Chatbox;

/// <summary>Источник данных для чатбокса. В работе за этим стоят возможности других плагинов.</summary>
public interface IChatData
{
    /// <summary>Читает только то, что нужно включённым строкам.</summary>
    /// <param name="status">Личный статус.</param>
    /// <param name="modules">Что показывается.</param>
    Task<ChatInputs> ReadAsync(string status, ChatModules modules);
}

/// <summary>Куда отправляется текст. Отделено, чтобы тесты не слали ничего в настоящий VRChat.</summary>
public interface IChatSink
{
    /// <summary>Отправляет текст в чатбокс.</summary>
    /// <param name="text">Текст.</param>
    /// <param name="notify">Со звуком уведомления, как настоящее сообщение.</param>
    Task SendAsync(string text, bool notify);
}

/// <summary>Настройки, которые движок читает на каждом шаге.</summary>
/// <param name="Sending">Отправлять ли вообще.</param>
/// <param name="Status">Личный статус.</param>
/// <param name="Modules">Какие строки показывать.</param>
/// <param name="ChatHold">Сколько держать отправленное сообщение.</param>
/// <param name="Format">Оформление строк.</param>
public sealed record ChatboxOptions(bool Sending, string Status, ChatModules Modules, TimeSpan ChatHold, ChatFormat? Format = null);

/// <summary>Решает, что и когда отправить в чатбокс.</summary>
/// <param name="data">Данные.</param>
/// <param name="sink">Куда отправлять.</param>
/// <param name="options">Текущие настройки.</param>
public sealed class ChatboxEngine(IChatData data, IChatSink sink, Func<ChatboxOptions> options)
{
    /// <summary>Чатбокс в игре гаснет сам, поэтому даже неизменный текст надо иногда повторять.</summary>
    private static readonly TimeSpan Resend = TimeSpan.FromSeconds(20);

    private string? _lastSent;
    private DateTimeOffset _lastSentAt;
    private DateTimeOffset _holdUntil;

    /// <summary>Запущен ли VRChat. Без него ничего не отправляется.</summary>
    public bool GameRunning { get; set; }

    /// <summary>Что сейчас ушло бы в чатбокс.</summary>
    public string Preview { get; private set; } = string.Empty;

    /// <summary>Одна проверка. Вызывается по таймеру.</summary>
    public async Task TickAsync()
    {
        var settings = options();
        var inputs = await data.ReadAsync(settings.Status, ChatboxComposer.Needed(settings.Modules, settings.Format));
        Preview = ChatboxComposer.Compose(inputs, settings.Modules, settings.Format);

        if (!settings.Sending || !GameRunning || inputs.Now < _holdUntil) return;
        if (Preview == _lastSent && inputs.Now - _lastSentAt < Resend) return;

        await sink.SendAsync(Preview, false);
        _lastSent = Preview;
        _lastSentAt = inputs.Now;
    }

    /// <summary>Отправляет сообщение и на время занимает чатбокс.</summary>
    /// <param name="text">Текст.</param>
    /// <param name="now">Текущее время.</param>
    /// <returns><see langword="true"/>, если сообщение ушло.</returns>
    public async Task<bool> SendMessageAsync(string text, DateTimeOffset now)
    {
        var settings = options();
        if (!GameRunning || !settings.Sending || string.IsNullOrWhiteSpace(text)) return false;

        var trimmed = text.Trim();
        await sink.SendAsync(trimmed.Length <= ChatboxComposer.MaxLength ? trimmed : trimmed[..ChatboxComposer.MaxLength], true);
        _holdUntil = now + settings.ChatHold;
        _lastSent = null;
        return true;
    }
}
