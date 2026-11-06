using System.Diagnostics;
using System.Text;
using QuantumHub.Client;
using QuantumHub.Contracts.Bus;
using QuantumHub.Contracts.Proto;
using QuantumHub.ProtoMemProtocol;

// Внешний плагин: отдельный процесс, кем угодно запущенный, который сам подключается к хабу.
// Так же выглядел бы плагин для Rider, VS Code или Discord.

const string BackendId = "qh.sample.uuid";
const string SpecsCapability = "system.specs";

Console.OutputEncoding = Encoding.UTF8;

var instance = Option("--instance") ?? HubEndpoint.DefaultInstance;
var transport = Option("--transport");
var tcpPort = Option("--tcp-port");

// Объявляем заранее, как и настоящий плагин: хаб ответит раньше, чем что-либо случится.
var specs = new Dependency { Capability = SpecsCapability, Version = ">=1.0.0", Optional = true, When = "lazy" };
var options = new HubClientOptions
{
    ClientId = "qh.sample.external",
    DisplayName = "External Console",
    Version = "1.0.0",
    Endpoint = new HubEndpoint(instance),
    Transports = transport is null ? ["shm", "stream"] : [transport],
    TcpPort = tcpPort is null ? null : int.Parse(tcpPort),
    TcpToken = Option("--tcp-token"),
    Dependencies =
    [
        new Dependency { Id = BackendId, Version = ">=1.0.0", When = "init" },
        specs,
    ],
};

HubClient client;
try
{
    client = await HubClient.ConnectAsync(options);
}
catch (HubRejectedException ex)
{
    Console.WriteLine($"Хаб отказал в подключении: {ex.Message}");
    PrintDependencies(ex.Dependencies);
    return 2;
}
catch (Exception ex) when (ex is TimeoutException or IOException)
{
    Console.WriteLine("Хаб не запущен.");
    return 3;
}

await using var _ = client;
Console.WriteLine($"Подключено к хабу {client.HubVersion}, транспорт: {client.Transport}, сессия {client.SessionId[..8]}");
PrintDependencies(client.Dependencies);

await client.SubscribeAsync("hub.plugin.state", e =>
{
    var info = e.Payload.AsProto<PluginInfo>();
    Console.WriteLine($"  [событие] {info.Manifest.Id}: {info.State}");
});

if (Option("--script") is { } script)
{
    foreach (var command in script.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
    {
        Console.WriteLine($"> {command}");
        await ExecuteAsync(command);
    }

    return 0;
}

Console.WriteLine("Команды: uuid | text <сообщение> | specs | deps | bench [n] | quit");
while (Console.ReadLine() is { } line)
{
    if (line.Trim() is "quit" or "exit") break;
    await ExecuteAsync(line.Trim());
}

return 0;

async Task ExecuteAsync(string line)
{
    var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
    var argument = parts.Length > 1 ? parts[1] : string.Empty;
    try
    {
        switch (parts.FirstOrDefault())
        {
            case "uuid":
                await SubmitAsync("uuid", Guid.NewGuid().ToString());
                break;
            case "text":
                await SubmitAsync("text", argument);
                break;
            case "specs":
                await PrintSpecsAsync();
                break;
            case "deps":
                PrintDependencies(await client.ResolveAsync(options.Dependencies));
                break;
            case "bench":
                await BenchAsync(int.TryParse(argument, out var count) ? count : 100_000);
                break;
            case "wait":
                await Task.Delay(int.Parse(argument));
                break;
            case null:
                break;
            default:
                Console.WriteLine("Неизвестная команда.");
                break;
        }
    }
    catch (HubCallException ex)
    {
        Console.WriteLine($"Ошибка ({ex.Status}): {ex.Message}");
    }
}

async Task SubmitAsync(string kind, string value)
{
    Console.WriteLine($"Отправляю: {value}");
    var response = await client.CallAsync(BackendId, "value.submit", HubPayload.Json(new { kind, value }));
    Console.WriteLine($"Бэкенд сохранил: {response.AsText()}");
}

async Task PrintSpecsAsync()
{
    // Необязательная ленивая зависимость: спрашиваем только сейчас, а если её нет, есть фолбэк.
    var resolution = await client.ResolveAsync(specs);
    if (resolution.State == DependencyState.Resolved)
    {
        Console.WriteLine($"Спеки от {resolution.ProviderId} {resolution.ProviderVersion}:");
        var payload = await client.CallAsync("cap:" + SpecsCapability, "specs.get");
        Console.WriteLine(payload.AsText());
        return;
    }

    Console.WriteLine($"Плагин спеков недоступен ({resolution.State}: {resolution.Detail}). Фолбэк:");
    Console.WriteLine($"{Environment.MachineName}, {Environment.ProcessorCount} потоков");
}

async Task BenchAsync(int count)
{
    foreach (var size in new[] { 64, 4096 })
    {
        var payload = HubPayload.Bytes(new byte[size]);
        for (var i = 0; i < 20_000; i++) await client.CallAsync("hub", "hub.ping", payload);

        var latencies = new long[count];
        var allocatedBefore = GC.GetTotalAllocatedBytes(true);
        var total = Stopwatch.StartNew();
        for (var i = 0; i < count; i++)
        {
            var start = Stopwatch.GetTimestamp();
            await client.CallAsync("hub", "hub.ping", payload);
            latencies[i] = Stopwatch.GetTimestamp() - start;
        }

        total.Stop();
        var allocated = (GC.GetTotalAllocatedBytes(true) - allocatedBefore) / count;
        Array.Sort(latencies);
        var toMicroseconds = 1e6 / Stopwatch.Frequency;
        Console.WriteLine($"{client.Transport}, {size,5} B: p50 {latencies[count / 2] * toMicroseconds:F1} мкс, " +
                          $"p99 {latencies[count * 99 / 100] * toMicroseconds:F1} мкс, {count / total.Elapsed.TotalSeconds:F0} вызовов/с, {allocated} Б аллокаций на вызов");
    }
}

static void PrintDependencies(IEnumerable<DependencyResolution> dependencies)
{
    foreach (var result in dependencies)
    {
        var name = result.Dependency.Id.Length > 0 ? result.Dependency.Id : result.Dependency.Capability;
        var kind = result.Dependency.Optional ? "необязательная" : "обязательная";
        var provider = result.ProviderId.Length > 0 ? $" <- {result.ProviderId} {result.ProviderVersion}" : string.Empty;
        Console.WriteLine($"  [{result.State}] {name} ({kind}, {(result.Dependency.When == "lazy" ? "lazy" : "init")}){provider} {result.Detail}");
    }
}

string? Option(string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}
