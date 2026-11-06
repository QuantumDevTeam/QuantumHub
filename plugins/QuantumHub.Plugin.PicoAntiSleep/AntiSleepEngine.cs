namespace QuantumHub.Plugin.PicoAntiSleep;

/// <summary>Настройки, которые движок читает на каждом шаге.</summary>
/// <param name="Address">Адрес шлема.</param>
/// <param name="Interval">Как часто посылать сигнал.</param>
public sealed record AntiSleepOptions(string Address, TimeSpan Interval);

/// <summary>
/// Цикл из рабочего скрипта PicoWakeupWiFi.ps1: послать WAKEUP, а если не вышло, отключиться,
/// подождать, подключиться заново. Он не сдаётся: шлем мог просто выйти из зоны Wi-Fi.
/// </summary>
/// <param name="adb">Команды adb.</param>
/// <param name="options">Текущие настройки.</param>
/// <param name="delay">Пауза. Подменяется в тестах.</param>
/// <param name="log">Куда писать смену статуса.</param>
public sealed class AntiSleepEngine(
    AdbClient adb,
    Func<AntiSleepOptions> options,
    Func<TimeSpan, CancellationToken, Task> delay,
    Action<string> log)
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(10);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _stop;
    private Task? _loop;

    /// <summary>Включён ли режим.</summary>
    public bool IsRunning => _loop is not null;

    /// <summary>Что сейчас происходит, для показа пользователю.</summary>
    public string Status { get; private set; } = "Выключен";

    /// <summary>Сменился статус.</summary>
    public event Action? StatusChanged;

    /// <summary>Включает режим. Повторный вызов ничего не делает.</summary>
    public async Task StartAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_loop is not null) return;
            _stop = new CancellationTokenSource();
            var token = _stop.Token;
            _loop = Task.Run(() => RunAsync(token), CancellationToken.None);
        }
        finally
        {
            _gate.Release();
        }

        StatusChanged?.Invoke();
    }

    /// <summary>Выключает режим и дожидается конца цикла.</summary>
    public async Task StopAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_loop is null) return;
            await _stop!.CancelAsync();
            await _loop;
            _stop.Dispose();
            _stop = null;
            _loop = null;
        }
        finally
        {
            _gate.Release();
        }

        SetStatus("Выключен");
    }

    private async Task RunAsync(CancellationToken token)
    {
        string? serial = null;
        try
        {
            while (!token.IsCancellationRequested)
            {
                var settings = options();
                try
                {
                    serial ??= await adb.ConnectAsync(settings.Address, token);
                    if (serial is null)
                    {
                        SetStatus($"Нет связи с {settings.Address}. Если шлем перезагружался, подключи его кабелем один раз");
                        await delay(RetryDelay, token);
                        continue;
                    }

                    if (await adb.WakeAsync(serial, token))
                    {
                        SetStatus($"Включён: {serial}, раз в {settings.Interval.TotalSeconds:0} с");
                        await delay(settings.Interval, token);
                        continue;
                    }

                    SetStatus("Связь потеряна, подключаемся заново");
                    await adb.DisconnectAsync(serial, token);
                    serial = null;
                    await delay(TimeSpan.FromSeconds(2), token);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    serial = null;
                    SetStatus(ex.Message);
                    await delay(RetryDelay, token);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Режим выключили.
        }

        if (serial is not null)
        {
            try
            {
                await adb.DisconnectAsync(serial, CancellationToken.None);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                log($"Не удалось отключиться от шлема: {ex.Message}");
            }
        }
    }

    private void SetStatus(string status)
    {
        if (status == Status) return;
        Status = status;
        log(status);
        StatusChanged?.Invoke();
    }
}
