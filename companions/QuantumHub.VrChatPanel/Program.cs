using System.IO;
using System.Windows;
using QuantumHub.Client;
using QuantumHub.Contracts.Bus;
using QuantumHub.ProtoMemProtocol;

namespace QuantumHub.VrChatPanel;

/// <summary>Состояние плагина. Копия записи из плагина: общий у них только формат JSON.</summary>
/// <param name="GameRunning">Запущен ли VRChat.</param>
/// <param name="Sending">Включена ли отправка.</param>
/// <param name="Status">Личный статус.</param>
/// <param name="Statuses">Сохранённые статусы.</param>
/// <param name="Preview">Что сейчас показывается в чатбоксе.</param>
public sealed record ChatboxState(bool GameRunning, bool Sending, string Status, IReadOnlyList<string> Statuses, string Preview);

/// <summary>
/// Внешняя часть плагина VRChat Chatbox: мини-окно для статуса и сообщений. Плагин открывает его вместе с игрой.
/// Вся логика живёт в хабе, окно только показывает и передаёт команды.
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var index = Array.IndexOf(args, "--instance");
        var instance = index >= 0 && index + 1 < args.Length ? args[index + 1] : HubEndpoint.DefaultInstance;

        using var single = new Mutex(true, $@"Local\QuantumHub.VrChatPanel.{instance}", out var isFirst);
        if (!isFirst) return 1;

        var application = new Application();
        var window = new PanelWindow();
        application.Startup += async (_, _) =>
        {
            window.Show();
            await ConnectAsync(instance, window);
        };
        application.Run();
        return 0;
    }

    private static async Task ConnectAsync(string instance, PanelWindow window)
    {
        try
        {
            var client = await HubClient.ConnectAsync(new HubClientOptions
            {
                ClientId = "qh.vrchat.chatbox.panel",
                DisplayName = "VRChat panel",
                Endpoint = new HubEndpoint(instance),
            });

            client.Disconnected += () => window.Dispatcher.BeginInvoke(window.Close);
            await client.SubscribeAsync("chatbox.changed", e => Apply(e.Payload, window));
            window.Attach(client);
            Apply(await client.CallAsync("cap:vrchat.chatbox", "chatbox.state"), window);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or HubCallException or HubRejectedException)
        {
            window.Close();
        }
    }

    private static void Apply(HubPayload payload, PanelWindow window)
    {
        if (payload.AsJson<ChatboxState>() is { } state) window.Dispatcher.BeginInvoke(() => window.Apply(state));
    }
}
