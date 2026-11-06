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
public sealed record NotificationRequest(string? Key, string Title, string? Subtitle, string? ImageBase64, string? Badge);

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
                               ?? throw new HubCallException(StatusCode.InvalidArgument, "Пустое уведомление.");
            Show(notification);
            return ValueTask.FromResult(HubPayload.Empty);
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

    private void Show(NotificationRequest request)
    {
        var config = _context.Config;
        var display = new NotificationDisplay(request,
            (int)(config.Get("durationSeconds", 3d) * 1000),
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
        yield return Ui.Page("main", "Проверка", "E7E7",
            Ui.Text("Настройки внешнего вида лежат на соседней вкладке."),
            Ui.Button("test", "Показать пробное уведомление", accent: true));
    }

    /// <inheritdoc/>
    public void OnUiEvent(UiEvent uiEvent)
    {
        if (uiEvent.NodeId == "test") Show(new NotificationRequest("test", "QuantumHub", "Так выглядит уведомление", null, null));
    }
}
