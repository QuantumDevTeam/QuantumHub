using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using QuantumHub.Client;
using QuantumHub.Contracts.Bus;
using QuantumHub.ProtoMemProtocol;

namespace QuantumHub.Notifier;

/// <summary>Что показать. Копия записи из плагина: общий у них только формат JSON.</summary>
/// <param name="Key">Окошко с тем же ключом обновляется.</param>
/// <param name="Title">Заголовок.</param>
/// <param name="Subtitle">Вторая строка.</param>
/// <param name="ImageBase64">Картинка в base64.</param>
/// <param name="Badge">Значок поверх картинки.</param>
/// <param name="Actions">Кнопки.</param>
public sealed record NotificationRequest(
    string? Key,
    string Title,
    string? Subtitle,
    string? ImageBase64,
    string? Badge,
    IReadOnlyList<NotificationAction>? Actions = null);

/// <summary>Кнопка в окошке.</summary>
/// <param name="Id">Что уйдёт в плагин при нажатии.</param>
/// <param name="Label">Надпись.</param>
public sealed record NotificationAction(string Id, string Label);

/// <summary>Просьба убрать окошко.</summary>
/// <param name="Key">Его ключ.</param>
public sealed record NotificationHide(string Key);

/// <summary>Уведомление вместе с настройками внешнего вида.</summary>
/// <param name="Request">Что показать.</param>
/// <param name="DurationMs">Сколько показывать.</param>
/// <param name="Opacity">Непрозрачность от 0 до 1.</param>
/// <param name="MarginRight">Отступ справа.</param>
/// <param name="MarginTop">Отступ сверху.</param>
/// <param name="SentUnixMs">Когда отправлено.</param>
public sealed record NotificationDisplay(
    NotificationRequest Request,
    int DurationMs,
    double Opacity,
    double MarginRight,
    double MarginTop,
    long SentUnixMs);

/// <summary>
/// Внешняя часть плагина Notifications. Подключается к хабу и показывает окошки. Если хаб пропал, закрывается:
/// без него ей нечего делать.
/// </summary>
internal static class Program
{
    private const long MaxPendingAgeMs = 5000;

    [STAThread]
    private static int Main(string[] args)
    {
        var index = Array.IndexOf(args, "--instance");
        var instance = index >= 0 && index + 1 < args.Length ? args[index + 1] : HubEndpoint.DefaultInstance;

        using var single = new Mutex(true, $@"Local\QuantumHub.Notifier.{instance}", out var isFirst);
        if (!isFirst) return 1;

        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var overlay = new NotificationOverlay();
        application.Startup += async (_, _) => await ConnectAsync(instance, overlay, application.Dispatcher);
        application.Run();
        return 0;
    }

    private static async Task ConnectAsync(string instance, NotificationOverlay overlay, Dispatcher dispatcher)
    {
        try
        {
            var client = await HubClient.ConnectAsync(new HubClientOptions
            {
                ClientId = "qh.ui.notify.overlay",
                DisplayName = "Notification overlay",
                Endpoint = new HubEndpoint(instance),
            });

            client.Disconnected += () => dispatcher.BeginInvoke(() => Application.Current.Shutdown());
            overlay.ActionClicked += (key, action) => _ = SendActionAsync(client, key, action);
            await client.SubscribeAsync("notify.display", e => Show(e.Payload, overlay, dispatcher));
            await client.SubscribeAsync("notify.hide", e => Hide(e.Payload, overlay, dispatcher));

            var pending = await client.CallAsync("cap:ui.notifications", "notify.pending");
            var age = pending.AsJson<NotificationDisplay>() is { } last
                ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - last.SentUnixMs
                : long.MaxValue;
            if (age < MaxPendingAgeMs) Show(pending, overlay, dispatcher);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or HubCallException or HubRejectedException or JsonException)
        {
            Application.Current.Shutdown();
        }
    }

    private static async Task SendActionAsync(HubClient client, string key, string action)
    {
        try
        {
            await client.CallAsync("cap:ui.notifications", "notify.action", HubPayload.Json(new { key, action }));
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or HubCallException)
        {
            // Хаб ушёл, а вместе с ним и тот, кто ждал нажатия.
        }
    }

    private static void Hide(HubPayload payload, NotificationOverlay overlay, Dispatcher dispatcher)
    {
        if (payload.AsJson<NotificationHide>() is { } hide) dispatcher.BeginInvoke(() => overlay.Hide(hide.Key));
    }

    private static void Show(HubPayload payload, NotificationOverlay overlay, Dispatcher dispatcher)
    {
        if (payload.AsJson<NotificationDisplay>() is { } display) dispatcher.BeginInvoke(() => overlay.Show(display));
    }
}
