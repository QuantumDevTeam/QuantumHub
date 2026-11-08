using QuantumHub.Contracts.Bus;
using QuantumHub.Contracts.Plugin;
using QuantumHub.Contracts.Proto;
using QuantumHub.Contracts.Ui;

namespace QuantumHub.Plugin.Notifications;

/// <summary>Запрос notify.show.</summary>
/// <param name="Key">Окошко с тем же ключом обновится, а не появится новое. Пусто значит отдельное окошко.</param>
/// <param name="Title">Заголовок.</param>
/// <param name="Subtitle">Вторая строка.</param>
/// <param name="ImageBase64">Картинка в base64, обычно png или jpeg.</param>
/// <param name="Badge">Значок поверх картинки, сейчас только "pause".</param>
/// <param name="Actions">Кнопки. Без них окошко пропускает щелчки сквозь себя, с ними ловит. Нужен <paramref name="Key"/>.</param>
/// <param name="DurationSeconds">Сколько показывать вместо обычной настройки: кнопку надо успеть нажать.</param>
public sealed record NotificationRequest(
    string? Key,
    string Title,
    string? Subtitle,
    string? ImageBase64,
    string? Badge,
    IReadOnlyList<NotificationAction>? Actions = null,
    double? DurationSeconds = null);

/// <summary>Кнопка в окошке.</summary>
/// <param name="Id">Что придёт в событии notify.action.</param>
/// <param name="Label">Надпись.</param>
public sealed record NotificationAction(string Id, string Label);

/// <summary>Запрос notify.action и тело одноимённого события.</summary>
/// <param name="Key">Ключ окошка.</param>
/// <param name="Action">Id нажатой кнопки.</param>
public sealed record NotificationClick(string Key, string Action);

/// <summary>Запрос notify.hide и тело одноимённого события.</summary>
/// <param name="Key">Ключ окошка, которое пора убрать.</param>
public sealed record NotificationHide(string Key);

/// <summary>То, что получает внешняя часть: запрос вместе с настройками внешнего вида.</summary>
/// <param name="Request">Что показать.</param>
/// <param name="DurationMs">Сколько показывать.</param>
/// <param name="Opacity">Непрозрачность от 0 до 1.</param>
/// <param name="MarginRight">Отступ справа.</param>
/// <param name="MarginTop">Отступ сверху.</param>
/// <param name="SentUnixMs">Когда отправлено. Старое при запуске внешней части показывать незачем.</param>
public sealed record NotificationDisplay(
    NotificationRequest Request,
    int DurationMs,
    double Opacity,
    double MarginRight,
    double MarginTop,
    long SentUnixMs);

/// <summary>
/// Уведомления для всех плагинов. Хаб — фоновый процесс без окон, поэтому сам показ лежит на внешней части:
/// плагин запускает её при первой необходимости и передаёт ей уведомления через событие.
/// </summary>
[HubPlugin]
public sealed class NotificationsPlugin : IHubPlugin, IUiProvider
{
    /// <summary>Топик, который слушает внешняя часть.</summary>
    public const string DisplayTopic = "notify.display";

    /// <summary>Топик нажатия на кнопку. Тело: <see cref="NotificationClick"/> в JSON.</summary>
    public const string ActionTopic = "notify.action";

    /// <summary>Топик, который просит внешнюю часть убрать окошко. Тело: <see cref="NotificationHide"/> в JSON.</summary>
    public const string HideTopic = "notify.hide";

    private const double MaxDurationSeconds = 120;

    private IPluginContext _context = null!;
    private CompanionProcess _overlay = null!;
    private NotificationDisplay? _last;

    /// <inheritdoc/>
    public Task StartAsync(IPluginContext context, CancellationToken token)
    {
        _context = context;
        _overlay = new CompanionProcess(Path.Combine(context.Paths.InstallDir, "companion", "QuantumHub.Notifier.exe"), context);

        context.Bus.Handle("notify.show", (request, _) =>
        {
            var notification = request.Payload.AsJson<NotificationRequest>()
                               ?? throw new HubCallException(StatusCode.InvalidArgument, Loc.T("Пустое уведомление.", "Empty notification."));
            Show(notification);
            return ValueTask.FromResult(HubPayload.Empty);
        });

        context.Bus.Handle("notify.hide", async (request, token) =>
        {
            var hide = request.Payload.AsJson<NotificationHide>();
            if (string.IsNullOrEmpty(hide?.Key)) throw new HubCallException(StatusCode.InvalidArgument, Loc.T("Нужен ключ окошка.", "A notification key is required."));

            Forget(hide.Key);
            await context.Bus.PublishAsync(HideTopic, HubPayload.Json(hide), token);
            return HubPayload.Empty;
        });

        // Сюда обращается внешняя часть. Кто показывал окошко, тот и подписан на событие.
        context.Bus.Handle("notify.action", async (request, token) =>
        {
            var click = request.Payload.AsJson<NotificationClick>();
            if (string.IsNullOrEmpty(click?.Key) || string.IsNullOrEmpty(click.Action))
                throw new HubCallException(StatusCode.InvalidArgument, Loc.T("Нужны ключ окошка и кнопка.", "A notification key and a button are required."));

            Forget(click.Key);
            await context.Bus.PublishAsync(ActionTopic, HubPayload.Json(click), token);
            return HubPayload.Empty;
        });

        // Запущенная по требованию внешняя часть подключается позже, чем было отправлено первое уведомление.
        context.Bus.Handle("notify.pending", (_, _) => ValueTask.FromResult(HubPayload.Json(_last)));
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken token)
    {
        _overlay.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>Закрытое окошко не должно всплыть снова, когда внешняя часть спросит, что она пропустила.</summary>
    private void Forget(string key)
    {
        if (_last?.Request.Key == key) _last = null;
    }

    private void Show(NotificationRequest request)
    {
        var config = _context.Config;
        var seconds = Math.Clamp(request.DurationSeconds ?? config.Get("durationSeconds", 3d), 1, MaxDurationSeconds);
        var display = new NotificationDisplay(request,
            (int)(seconds * 1000),
            config.Get("opacity", 85d) / 100,
            config.Get("marginRight", 20d),
            config.Get("marginTop", 20d),
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        _last = display;
        _overlay.Ensure();
        _ = _context.Bus.PublishAsync(DisplayTopic, HubPayload.Json(display));
    }

    /// <inheritdoc/>
    public IEnumerable<UiPage> GetPages()
    {
        yield return Ui.Page("main", Loc.T("Проверка", "Test"), "E7E7",
            Ui.Text(Loc.T("Настройки внешнего вида лежат на соседней вкладке.", "Appearance settings are on the next tab.")),
            Ui.Button("test", Loc.T("Показать пробное уведомление", "Show a test notification"), accent: true));
    }

    /// <inheritdoc/>
    public void OnUiEvent(UiEvent uiEvent)
    {
        if (uiEvent.NodeId == "test") Show(new NotificationRequest("test", "QuantumHub", Loc.T("Так выглядит уведомление", "This is how a notification looks"), null, null));
    }
}
