using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace QuantumHub.Plugin.Discord;

/// <summary>Discord отказал или вернул ошибку.</summary>
/// <param name="code">Код ошибки Discord.</param>
/// <param name="message">Описание.</param>
public sealed class DiscordRpcException(int code, string message) : Exception(message)
{
    /// <summary>Код Discord, например 4006 для отсутствующей авторизации.</summary>
    public int Code { get; } = code;
}

/// <summary>
/// Соединение с Discord: рукопожатие, команды с ответами и события. Ответ находится по nonce, так же как
/// по id в протоколе самого хаба.
/// </summary>
/// <param name="stream">Подключённый поток. Клиент становится его владельцем.</param>
public sealed class DiscordRpcClient(Stream stream) : IAsyncDisposable
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonNode?>> _pending = new();
    private readonly TaskCompletionSource<JsonObject> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();

    /// <summary>Завершается, когда соединение закрыто.</summary>
    public Task Closed => _closed.Task;

    /// <summary>Событие от Discord: имя и данные. Срабатывает на потоке чтения, долго работать в нём нельзя.</summary>
    public event Action<string, JsonNode?>? EventReceived;

    /// <summary>Рукопожатие.</summary>
    /// <param name="clientId">Client ID приложения.</param>
    /// <param name="token">Отмена.</param>
    /// <returns>Данные события READY, в них есть текущий пользователь.</returns>
    /// <exception cref="DiscordRpcException">Discord отказал, например из-за неверного Client ID.</exception>
    public async Task<JsonObject> ConnectAsync(string clientId, CancellationToken token)
    {
        _ = Task.Run(ReadLoopAsync, CancellationToken.None);
        await WriteAsync(DiscordOpcode.Handshake, new JsonObject { ["v"] = 1, ["client_id"] = clientId }, token);
        return await _ready.Task.WaitAsync(DefaultTimeout, token);
    }

    /// <summary>Выполняет команду и ждёт ответ.</summary>
    /// <param name="command">Имя команды, например "SET_VOICE_SETTINGS".</param>
    /// <param name="arguments">Аргументы.</param>
    /// <param name="eventName">Имя события для SUBSCRIBE и UNSUBSCRIBE.</param>
    /// <param name="timeout">Сколько ждать. AUTHORIZE ждёт пользователя и потому дольше обычного.</param>
    /// <param name="token">Отмена.</param>
    /// <returns>Поле data ответа.</returns>
    /// <exception cref="DiscordRpcException">Discord вернул ошибку.</exception>
    public async Task<JsonNode?> CommandAsync(string command, JsonObject? arguments = null, string? eventName = null,
        TimeSpan? timeout = null, CancellationToken token = default)
    {
        var nonce = Guid.NewGuid().ToString();
        var message = new JsonObject { ["cmd"] = command, ["nonce"] = nonce, ["args"] = arguments ?? [] };
        if (eventName is not null) message["evt"] = eventName;

        var waiter = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[nonce] = waiter;
        try
        {
            await WriteAsync(DiscordOpcode.Frame, message, token);
            return await waiter.Task.WaitAsync(timeout ?? DefaultTimeout, token);
        }
        finally
        {
            _pending.TryRemove(nonce, out _);
        }
    }

    private async Task WriteAsync(DiscordOpcode opcode, JsonNode payload, CancellationToken token)
    {
        await _writeLock.WaitAsync(token);
        try
        {
            await DiscordFrame.WriteAsync(stream, opcode, payload, token);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReadLoopAsync()
    {
        Exception? failure = null;
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                var (opcode, payload) = await DiscordFrame.ReadAsync(stream, _lifetime.Token);
                switch (opcode)
                {
                    case DiscordOpcode.Frame:
                        OnFrame(payload as JsonObject);
                        break;
                    case DiscordOpcode.Ping:
                        await WriteAsync(DiscordOpcode.Pong, payload ?? new JsonObject(), _lifetime.Token);
                        break;
                    case DiscordOpcode.Close:
                        failure = new DiscordRpcException(Code(payload), payload?["message"]?.GetValue<string>() ?? "Discord закрыл соединение.");
                        return;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or OperationCanceledException or ObjectDisposedException)
        {
            failure = ex;
        }
        catch (Exception ex) when (ex is InvalidDataException or System.Text.Json.JsonException)
        {
            failure = ex;
        }
        finally
        {
            Finish(failure);
        }
    }

    private void OnFrame(JsonObject? frame)
    {
        if (frame is null) return;

        var name = frame["evt"]?.GetValue<string>();
        var data = frame["data"];
        var nonce = frame["nonce"]?.GetValue<string>();

        if (nonce is not null && _pending.TryRemove(nonce, out var waiter))
        {
            if (name == "ERROR") waiter.TrySetException(new DiscordRpcException(Code(data), data?["message"]?.GetValue<string>() ?? "Ошибка Discord."));
            else waiter.TrySetResult(data);
            return;
        }

        if (frame["cmd"]?.GetValue<string>() != "DISPATCH" || name is null) return;

        if (name == "READY") _ready.TrySetResult(data as JsonObject ?? []);
        else EventReceived?.Invoke(name, data);
    }

    private static int Code(JsonNode? node) => node?["code"]?.GetValue<int>() ?? 0;

    private void Finish(Exception? failure)
    {
        var error = failure as DiscordRpcException ?? new DiscordRpcException(0, "Соединение с Discord потеряно.");
        _ready.TrySetException(error);
        foreach (var nonce in _pending.Keys)
        {
            if (_pending.TryRemove(nonce, out var waiter)) waiter.TrySetException(error);
        }

        _closed.TrySetResult();
    }

    /// <summary>Закрывает соединение.</summary>
    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync();
        await stream.DisposeAsync();
        await _closed.Task.WaitAsync(TimeSpan.FromSeconds(2)).ContinueWith(static _ => { }, TaskScheduler.Default);
    }
}
