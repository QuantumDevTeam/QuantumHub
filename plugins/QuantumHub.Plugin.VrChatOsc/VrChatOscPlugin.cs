using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using QuantumHub.Contracts.Bus;
using QuantumHub.Contracts.Plugin;
using QuantumHub.Contracts.Proto;
using QuantumHub.Contracts.Ui;

namespace QuantumHub.Plugin.VrChatOsc;

/// <summary>Запрос osc.send.</summary>
/// <param name="Address">Адрес OSC.</param>
/// <param name="Args">Аргументы, смотри <see cref="OscJson.ToArgument"/>.</param>
public sealed record OscSendRequest(string Address, JsonElement[]? Args);

/// <summary>Запрос chatbox.send.</summary>
/// <param name="Text">Текст, до 144 символов.</param>
/// <param name="Notify">Со звуком, как настоящее сообщение.</param>
public sealed record ChatboxRequest(string Text, bool Notify);

/// <summary>Запрос avatar.parameter.set.</summary>
/// <param name="Name">Имя параметра.</param>
/// <param name="Value">Значение.</param>
public sealed record ParameterRequest(string Name, JsonElement Value);

/// <summary>Запрос tracker.send. Координаты в метрах, углы в градусах, по осям Unity.</summary>
/// <param name="Id">Номер трекера от 1 до 8, либо "head".</param>
/// <param name="Position">Положение: x, y, z.</param>
/// <param name="Rotation">Поворот: x, y, z.</param>
public sealed record TrackerRequest(string Id, float[] Position, float[] Rotation);

/// <summary>Сообщение из игры. Тело события vrchat.osc.received.</summary>
/// <param name="Address">Адрес OSC.</param>
/// <param name="Args">Аргументы.</param>
public sealed record OscReceived(string Address, object[] Args);

/// <summary>Ответ avatar.get.</summary>
/// <param name="AvatarId">Id текущего аватара, если он уже известен.</param>
/// <param name="Parameters">Последние значения параметров.</param>
public sealed record AvatarState(string? AvatarId, IReadOnlyDictionary<string, object> Parameters);

/// <summary>Связь с VRChat. Транспорт для других плагинов: сам плагин ничего не показывает и ничего не решает.</summary>
[HubPlugin]
public sealed class VrChatOscPlugin : IHubPlugin, IUiProvider
{
    /// <summary>Топик сообщений из игры.</summary>
    public const string ReceivedTopic = "vrchat.osc.received";

    private const string ParameterPrefix = "/avatar/parameters/";

    private readonly ConcurrentDictionary<string, object> _parameters = new();
    private IPluginContext _context = null!;
    private CancellationTokenSource? _stop;
    private Task? _listener;
    private string? _avatar;
    private int _sent;
    private int _received;

    /// <inheritdoc/>
    public Task StartAsync(IPluginContext context, CancellationToken token)
    {
        _context = context;
        Handle<OscSendRequest>("osc.send", request => Send(request.Address, (request.Args ?? []).Select(OscJson.ToArgument).ToArray()));
        Handle<ChatboxRequest>("chatbox.send", request => Send("/chatbox/input", request.Text, true, request.Notify));
        Handle<bool>("chatbox.typing", typing => Send("/chatbox/typing", typing));
        Handle<ParameterRequest>("avatar.parameter.set", request => Send(ParameterPrefix + request.Name, OscJson.ToArgument(request.Value)));
        Handle<TrackerRequest>("tracker.send", SendTracker);
        context.Bus.Handle("avatar.get", (_, _) => ValueTask.FromResult(HubPayload.Json(new AvatarState(_avatar, _parameters))));

        context.Config.Changed += OnConfigChanged;
        Listen();
        PushState();
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task StopAsync(CancellationToken token)
    {
        _context.Config.Changed -= OnConfigChanged;
        await StopListeningAsync();
    }

    private void Handle<T>(string method, Action<T> action) => _context.Bus.Handle(method, (request, _) =>
    {
        try
        {
            action(request.Payload.AsJson<T>() ?? throw new ArgumentException("Пустой запрос."));
            return ValueTask.FromResult(HubPayload.Empty);
        }
        catch (Exception ex) when (ex is ArgumentException or JsonException)
        {
            throw new HubCallException(StatusCode.InvalidArgument, ex.Message);
        }
    });

    private void Send(string address, params object[] arguments)
    {
        var packet = OscMessage.Encode(address, arguments);
        using var client = new UdpClient();
        client.Send(packet, _context.Config.Get("host", "127.0.0.1"), (int)_context.Config.Get("sendPort", 9000d));
        Interlocked.Increment(ref _sent);
    }

    private void SendTracker(TrackerRequest tracker)
    {
        if (tracker.Position is not { Length: 3 } || tracker.Rotation is not { Length: 3 })
            throw new ArgumentException("Нужны три координаты положения и три угла поворота.");

        Send($"/tracking/trackers/{tracker.Id}/position", tracker.Position.Cast<object>().ToArray());
        Send($"/tracking/trackers/{tracker.Id}/rotation", tracker.Rotation.Cast<object>().ToArray());
    }

    private async void OnConfigChanged(string key)
    {
        if (key is not ("receive" or "receivePort")) return;
        await StopListeningAsync();
        Listen();
    }

    private void Listen()
    {
        if (!_context.Config.Get("receive", true))
        {
            _context.Ui.Set("receive", Loc.T("выключен", "off"));
            return;
        }

        var port = (int)_context.Config.Get("receivePort", 9001d);
        try
        {
            var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));
            _stop = new CancellationTokenSource();
            _listener = Task.Run(() => ReceiveAsync(client, _stop.Token), CancellationToken.None);
            _context.Ui.Set("receive", Loc.T($"слушаем порт {port}", $"listening on port {port}"));
        }
        catch (SocketException)
        {
            // Порт приёма может держать только одна программа. Отправка от этого не страдает.
            _context.Logger.Warning($"Порт {port} занят другой программой, приём выключен.");
            _context.Ui.Set("receive", Loc.T($"порт {port} занят другой программой", $"port {port} is used by another program"));
        }
    }

    private async Task StopListeningAsync()
    {
        if (_stop is not null) await _stop.CancelAsync();
        if (_listener is not null) await _listener;
        _stop?.Dispose();
        _stop = null;
        _listener = null;
    }

    private async Task ReceiveAsync(UdpClient client, CancellationToken token)
    {
        using (client)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    var packet = await client.ReceiveAsync(token);
                    if (OscMessage.Decode(packet.Buffer) is { } message) OnMessage(message.Address, message.Arguments);
                }
            }
            catch (OperationCanceledException)
            {
                // Приём выключили.
            }
        }
    }

    private void OnMessage(string address, object[] arguments)
    {
        Interlocked.Increment(ref _received);
        if (address.StartsWith(ParameterPrefix, StringComparison.Ordinal) && arguments.Length > 0)
            _parameters[address[ParameterPrefix.Length..]] = arguments[0];

        if (address == "/avatar/change" && arguments is [string avatar, ..])
        {
            _avatar = avatar;
            _parameters.Clear();
            _context.Ui.Set("avatar", avatar);
        }

        _ = _context.Bus.PublishAsync(ReceivedTopic, HubPayload.Json(new OscReceived(address, arguments)));
    }

    private void PushState()
    {
        _context.Ui.Set("sent", _sent.ToString());
        _context.Ui.Set("received", _received.ToString());
    }

    /// <inheritdoc/>
    public IEnumerable<UiPage> GetPages()
    {
        PushState();
        yield return Ui.Page("main", Loc.T("Связь", "Link"), "E704",
            Ui.KeyValue(Loc.T("Приём", "Receiving"), "receive"),
            Ui.KeyValue(Loc.T("Отправлено сообщений", "Messages sent"), "sent"),
            Ui.KeyValue(Loc.T("Получено сообщений", "Messages received"), "received"),
            Ui.KeyValue(Loc.T("Текущий аватар", "Current avatar"), "avatar"));
    }

    /// <inheritdoc/>
    public void OnUiEvent(UiEvent uiEvent)
    {
    }
}
