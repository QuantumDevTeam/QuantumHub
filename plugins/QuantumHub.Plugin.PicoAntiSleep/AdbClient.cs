namespace QuantumHub.Plugin.PicoAntiSleep;

/// <summary>Результат запуска adb.</summary>
/// <param name="ExitCode">Код завершения. Отрицательный, если adb не удалось запустить или он завис.</param>
/// <param name="Output">Вывод adb.</param>
public sealed record AdbResult(int ExitCode, string Output);

/// <summary>Запуск adb. Отделён от логики, чтобы тесты не трогали настоящий шлем.</summary>
public interface IAdbRunner
{
    /// <summary>Поднимает фоновый сервер adb.</summary>
    /// <param name="token">Отмена.</param>
    Task StartServerAsync(CancellationToken token);

    /// <summary>Выполняет команду adb.</summary>
    /// <param name="arguments">Аргументы.</param>
    /// <param name="token">Отмена.</param>
    Task<AdbResult> RunAsync(string arguments, CancellationToken token);
}

/// <summary>Устройство из вывода adb devices.</summary>
/// <param name="Serial">Серийный номер, у сетевых устройств это ip:порт.</param>
/// <param name="State">Состояние, рабочее — "device".</param>
public sealed record AdbDevice(string Serial, string State)
{
    /// <summary>Готово ли устройство принимать команды.</summary>
    public bool IsReady => State == "device";

    /// <summary>Подключено ли устройство кабелем.</summary>
    public bool IsUsb => !Serial.Contains(':');
}

/// <summary>Команды adb, нужные для работы со шлемом по Wi-Fi.</summary>
/// <param name="runner">Запуск adb.</param>
/// <param name="delay">Пауза. Подменяется в тестах.</param>
public sealed class AdbClient(IAdbRunner runner, Func<TimeSpan, CancellationToken, Task> delay)
{
    private const int DefaultPort = 5555;

    /// <summary>Приводит адрес к виду ip:порт. Порт в настройке необязателен.</summary>
    /// <param name="address">IP с портом или без него.</param>
    public static string NormalizeAddress(string address)
    {
        var trimmed = address.Trim();
        return trimmed.Contains(':') ? trimmed : $"{trimmed}:{DefaultPort}";
    }

    /// <summary>Разбирает вывод adb devices.</summary>
    /// <param name="output">Вывод команды.</param>
    public static IReadOnlyList<AdbDevice> ParseDevices(string output) => output
        .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        .Where(parts => parts.Length == 2 && parts[0] != "List")
        .Select(parts => new AdbDevice(parts[0], parts[1]))
        .ToList();

    private async Task<IReadOnlyList<AdbDevice>> DevicesAsync(CancellationToken token) =>
        ParseDevices((await runner.RunAsync("devices", token)).Output);

    /// <summary>
    /// Подключается к шлему по Wi-Fi. Если не вышло, а шлем подключён кабелем, включает на нём ADB
    /// по сети и пробует снова: после перезагрузки шлема этот режим выключен.
    /// </summary>
    /// <param name="address">Адрес шлема.</param>
    /// <param name="token">Отмена.</param>
    /// <returns>Серийный номер для команд или <see langword="null"/>, если шлем недоступен.</returns>
    public async Task<string?> ConnectAsync(string address, CancellationToken token)
    {
        await runner.StartServerAsync(token);

        var target = NormalizeAddress(address);
        var devices = await DevicesAsync(token);
        if (devices.Any(device => device.Serial == target && device.IsReady)) return target;

        if (await TryConnectAsync(target, token)) return target;

        var usb = devices.FirstOrDefault(device => device is { IsUsb: true, IsReady: true });
        if (usb is null) return null;

        // Кабель заодно подсказывает настоящий адрес: роутер мог выдать шлему другой.
        target = NormalizeAddress(await WifiAddressAsync(usb.Serial, token) ?? target);
        await runner.RunAsync($"-s {usb.Serial} tcpip {DefaultPort}", token);
        await delay(TimeSpan.FromSeconds(2), token);

        return await TryConnectAsync(target, token) ? target : null;
    }

    private async Task<bool> TryConnectAsync(string target, CancellationToken token)
    {
        await runner.RunAsync($"connect {target}", token);
        return (await DevicesAsync(token)).Any(device => device.Serial == target && device.IsReady);
    }

    private async Task<string?> WifiAddressAsync(string serial, CancellationToken token)
    {
        var result = await runner.RunAsync($"-s {serial} shell ip -f inet addr show wlan0", token);
        foreach (var word in result.Output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var slash = word.IndexOf('/');
            if (slash > 0 && System.Net.IPAddress.TryParse(word[..slash], out _)) return word[..slash];
        }

        return null;
    }

    /// <summary>Посылает WAKEUP, то же самое, что keyevent 224 в рабочем скрипте.</summary>
    /// <param name="serial">Серийный номер. Без него adb откажется работать, если подключён ещё и кабель.</param>
    /// <param name="token">Отмена.</param>
    /// <returns><see langword="true"/>, если шлем принял команду.</returns>
    public async Task<bool> WakeAsync(string serial, CancellationToken token) =>
        (await runner.RunAsync($"-s {serial} shell input keyevent 224", token)).ExitCode == 0;

    /// <summary>Разрывает сетевое подключение.</summary>
    /// <param name="serial">Серийный номер.</param>
    /// <param name="token">Отмена.</param>
    public Task DisconnectAsync(string serial, CancellationToken token) => runner.RunAsync($"disconnect {serial}", token);
}
