using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using Microsoft.Win32;
using QuantumHub.Client;
using QuantumHub.Contracts.Bus;
using QuantumHub.Contracts.Links;
using QuantumHub.Contracts.Proto;
using QuantumHub.Contracts.Ui;
using QuantumHub.ProtoMemProtocol;

namespace QuantumHub.Cli;

/// <summary>Команды CLI. Это обычный клиент хаба, который умеет ровно то же, что и окно.</summary>
/// <remarks>Коды завершения: 0 успех, 1 ошибка, 2 неверная команда, 3 хаб не запущен.</remarks>
/// <param name="output">Куда писать результат.</param>
/// <param name="error">Куда писать ошибки.</param>
internal sealed class CliApp(TextWriter output, TextWriter error)
{
    /// <summary>Успех.</summary>
    public const int Ok = 0;
    /// <summary>Хаб или плагин вернул ошибку.</summary>
    public const int Failed = 1;
    /// <summary>Неверная команда.</summary>
    public const int Usage = 2;
    /// <summary>Хаб не запущен.</summary>
    public const int NoHub = 3;

    /// <summary>Сколько ждать запуска или завершения хаба.</summary>
    private static readonly TimeSpan HubWait = TimeSpan.FromSeconds(20);
    /// <summary>Сколько строк журнала показывать по умолчанию.</summary>
    private const int DefaultLogLines = 50;
    /// <summary>Ключ автозапуска.</summary>
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    /// <summary>Значение автозапуска хаба. То же, что в настройках GUI.</summary>
    private const string RunHubValue = "QuantumHub";

    private HubEndpoint _endpoint = HubEndpoint.Default;
    private bool _json;

    /// <summary>Выполняет команду.</summary>
    /// <param name="args">Аргументы командной строки.</param>
    /// <param name="token">Отмена по Ctrl+C.</param>
    /// <returns>Код завершения.</returns>
    public async Task<int> RunAsync(string[] args, CancellationToken token = default)
    {
        var words = new List<string>(args);
        _json = words.Remove("--json");
        if (Take(words, "--instance") is { } instance) _endpoint = new HubEndpoint(instance);

        if (words.Count == 0 || words[0] is "help" or "--help" or "-h" or "/?")
        {
            await output.WriteAsync(Help());
            return words.Count == 0 ? Usage : Ok;
        }

        var command = words[0];
        words.RemoveAt(0);
        try
        {
            return command switch
            {
                "version" or "--version" => await VersionAsync(),
                "status" => await WithHubAsync(StatusAsync),
                "plugins" => await WithHubAsync(client => PluginsAsync(client, words)),
                "config" => await WithHubAsync(client => ConfigAsync(client, words)),
                "sessions" => await WithHubAsync(SessionsAsync),
                "logs" => await WithHubAsync(client => LogsAsync(client, words, token)),
                "call" => await WithHubAsync(client => CallAsync(client, words)),
                "link" => await LinkAsync(words),
                "report" => await WithHubAsync(client => ReportAsync(client, words)),
                "start" => await StartAsync(token),
                "stop" => await StopAsync(token),
                "restart" => await RestartAsync(token),
                "gui" => LaunchGui(),
                "autostart" => Autostart(words),
                _ => Wrong(Loc.T($"Нет команды '{command}'.", $"There is no command '{command}'.")),
            };
        }
        catch (HubCallException ex)
        {
            await error.WriteLineAsync(ex.Message);
            return Failed;
        }
        catch (OperationCanceledException)
        {
            return Ok;
        }
    }

    /// <summary>Сообщает о неверной команде.</summary>
    private int Wrong(string message)
    {
        error.WriteLine(message);
        error.WriteLine(Loc.T("Список команд: quantumhub help", "List of commands: quantumhub help"));
        return Usage;
    }

    /// <summary>Подключается к хабу и выполняет команду.</summary>
    private async Task<int> WithHubAsync(Func<HubClient, Task<int>> action)
    {
        await using var client = await TryConnectAsync();
        if (client is null)
        {
            await error.WriteLineAsync(Loc.T("Хаб не запущен. Запустить его: quantumhub start", "The hub is not running. Start it with: quantumhub start"));
            return NoHub;
        }

        return await action(client);
    }

    /// <summary>Подключается к хабу.</summary>
    /// <returns>Клиент или <see langword="null"/>, если хаб не запущен.</returns>
    private async Task<HubClient?> TryConnectAsync()
    {
        try
        {
            return await HubClient.ConnectAsync(new HubClientOptions
            {
                ClientId = $"quantumhub.cli.{Environment.ProcessId}",
                DisplayName = "QuantumHub CLI",
                Version = typeof(CliApp).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
                Role = "cli",
                Endpoint = _endpoint,
                ConnectTimeout = TimeSpan.FromSeconds(1),
                Transports = ["stream"],
            });
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or OperationCanceledException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Показывает версию CLI и хаба, если он запущен.</summary>
    private async Task<int> VersionAsync()
    {
        await output.WriteLineAsync($"quantumhub {typeof(CliApp).Assembly.GetName().Version?.ToString(3)}");
        await using var client = await TryConnectAsync();
        if (client is not null) await output.WriteLineAsync($"hub {client.HubVersion}");
        return Ok;
    }

    private async Task<int> StatusAsync(HubClient client)
    {
        var status = await client.CallAsync<HubStatus>(Envelopes.HubTarget, "hub.status", new PluginRef());
        if (Print(status)) return Ok;

        var plugins = await client.CallAsync<PluginList>(Envelopes.HubTarget, "hub.plugins.list", new PluginRef());
        var uptime = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeMilliseconds(status.StartedUnixMs);
        await output.WriteLineAsync($"QuantumHub {status.Version}, PID {status.ProcessId}, {(int)uptime.TotalHours:00}:{uptime.Minutes:00}");
        await output.WriteLineAsync(Loc.T($"Плагины: работают {plugins.Plugins.Count(plugin => plugin.State == PluginState.Running)} из {plugins.Plugins.Count}",
            $"Plugins: {plugins.Plugins.Count(plugin => plugin.State == PluginState.Running)} of {plugins.Plugins.Count} running"));
        await output.WriteLineAsync(Loc.T($"Транспорты: {string.Join(", ", status.Transports)}", $"Transports: {string.Join(", ", status.Transports)}"));
        await output.WriteLineAsync(Loc.T($"Программа: {status.RootDir}", $"Program: {status.RootDir}"));
        await output.WriteLineAsync(Loc.T($"Данные: {status.DataDir}", $"Data: {status.DataDir}"));
        return Ok;
    }

    private async Task<int> PluginsAsync(HubClient client, List<string> words)
    {
        var action = words.Count > 0 ? words[0] : "list";
        if (action == "list")
        {
            var list = await client.CallAsync<PluginList>(Envelopes.HubTarget, "hub.plugins.list", new PluginRef());
            if (Print(list)) return Ok;

            var width = list.Plugins.Select(plugin => plugin.Manifest.Id.Length).DefaultIfEmpty(0).Max();
            foreach (var plugin in list.Plugins)
            {
                var line = $"{plugin.Manifest.Id.PadRight(width)}  {plugin.Manifest.Version,-8}  {plugin.State}";
                await output.WriteLineAsync(plugin.Error.Length > 0 ? $"{line}: {plugin.Error}" : line);
            }

            return Ok;
        }

        if (words.Count != 2) return Wrong(Loc.T($"Нужен один аргумент: plugins {action} <id>", $"One argument is needed: plugins {action} <id>"));
        var target = words[1];
        switch (action)
        {
            case "enable" or "disable":
                await client.CallAsync(Envelopes.HubTarget, "hub.plugins.enable", HubPayload.Proto(new SetEnabledRequest { Id = target, Enabled = action == "enable" }), Long);
                return Ok;
            case "restart":
                await client.CallAsync(Envelopes.HubTarget, "hub.plugins.restart", HubPayload.Proto(new PluginRef { Id = target }), Long);
                return Ok;
            case "remove":
                await client.CallAsync(Envelopes.HubTarget, "hub.plugins.uninstall", HubPayload.Proto(new PluginRef { Id = target }), Long);
                return Ok;
            case "install":
                var installed = (await client.CallAsync(Envelopes.HubTarget, "hub.plugins.install", HubPayload.Proto(new InstallRequest { Path = Path.GetFullPath(target) }), Long)).AsProto<PluginInfo>();
                if (!Print(installed)) await output.WriteLineAsync($"{installed.Manifest.Id} {installed.Manifest.Version}: {installed.State}");
                return Ok;
            default:
                return Wrong(Loc.T($"Нет действия '{action}'.", $"There is no action '{action}'."));
        }
    }

    /// <summary>Сколько ждать команд, которые останавливают и запускают плагины.</summary>
    private static readonly TimeSpan Long = TimeSpan.FromSeconds(90);

    private async Task<int> ConfigAsync(HubClient client, List<string> words)
    {
        switch (words.Count > 0 ? words[0] : string.Empty)
        {
            case "get" when words.Count is 2 or 3:
                var value = await client.CallAsync<ConfigValue>(Envelopes.HubTarget, "hub.config.get",
                    new ConfigGetRequest { PluginId = words[1], Key = words.Count == 3 ? words[2] : string.Empty });
                if (!value.Found)
                {
                    await error.WriteLineAsync(Loc.T("Такой настройки нет.", "No such setting."));
                    return Failed;
                }

                await output.WriteLineAsync(value.Json);
                return Ok;

            case "set" when words.Count == 4:
                await client.CallAsync(Envelopes.HubTarget, "hub.config.set",
                    HubPayload.Proto(new ConfigSetRequest { PluginId = words[1], Key = words[2], Json = AsJsonValue(words[3]) }));
                return Ok;

            default:
                return Wrong(Loc.T("Нужно: config get <плагин> [ключ] или config set <плагин> <ключ> <значение>", "Expected: config get <plugin> [key] or config set <plugin> <key> <value>"));
        }
    }

    /// <summary>Значение настройки: готовый JSON остаётся как есть, а всё остальное становится строкой, чтобы не возиться с кавычками.</summary>
    /// <param name="text">Значение из командной строки.</param>
    public static string AsJsonValue(string text) => IsJson(text) ? text : JsonSerializer.Serialize(text);

    private static bool IsJson(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private async Task<int> SessionsAsync(HubClient client)
    {
        var sessions = await client.CallAsync<SessionList>(Envelopes.HubTarget, "hub.sessions.list", new PluginRef());
        if (Print(sessions)) return Ok;

        foreach (var session in sessions.Sessions)
            await output.WriteLineAsync($"{session.ClientId} {session.Version}  {session.Role}  {session.Transport}  PID {session.ProcessId}");
        return Ok;
    }

    private async Task<int> LogsAsync(HubClient client, List<string> words, CancellationToken token)
    {
        var follow = words.Remove("-f") | words.Remove("--follow");
        var lines = DefaultLogLines;
        if (Take(words, "-n") is { } count && (!int.TryParse(count, out lines) || lines < 0)) return Wrong(Loc.T($"'{count}' не число.", $"'{count}' is not a number."));

        var list = await client.CallAsync<LogList>(Envelopes.HubTarget, "hub.logs.get", new PluginRef());
        foreach (var entry in list.Entries.TakeLast(lines)) await output.WriteLineAsync(Format(entry));
        if (!follow) return Ok;

        var written = new SemaphoreSlim(1, 1);
        using var subscription = await client.SubscribeAsync("hub.log", e =>
        {
            written.Wait();
            try { output.WriteLine(Format(e.Payload.AsProto<LogEntry>())); }
            finally { written.Release(); }
        }, token);
        await Task.Delay(Timeout.Infinite, token);
        return Ok;
    }

    /// <summary>Запись журнала одной строкой.</summary>
    private string Format(LogEntry entry) => _json
        ? JsonFormatter.Default.Format(entry)
        : $"{DateTimeOffset.FromUnixTimeMilliseconds(entry.UnixMs).LocalDateTime:HH:mm:ss} {entry.Level,-7} [{entry.Source}] {entry.Message}";

    private async Task<int> CallAsync(HubClient client, List<string> words)
    {
        if (words.Count is < 2 or > 3) return Wrong(Loc.T("Нужно: call <адрес> <метод> [json или текст]", "Expected: call <target> <method> [json or text]"));

        var payload = words.Count == 2 ? default
            : IsJson(words[2]) ? new HubPayload(HubPayload.JsonType, Encoding.UTF8.GetBytes(words[2]))
            : HubPayload.Text(words[2]);
        var reply = await client.CallAsync(words[0], words[1], payload);
        if (reply.ContentType is HubPayload.JsonType or HubPayload.TextType) await output.WriteLineAsync(reply.AsText());
        else if (!reply.IsEmpty) await output.WriteLineAsync($"<{reply.ContentType}, {reply.Data.Length} bytes>");
        return Ok;
    }

    /// <summary>Открывает ссылку. Действие выполняется сразу: команду набрал сам пользователь, а окно спрашивает только про ссылки извне.</summary>
    private async Task<int> LinkAsync(List<string> words)
    {
        if (words.Count != 1 || !HubLink.TryParse(words[0], out var link))
            return Wrong(Loc.T("Нужна одна ссылка вида quantumhub://…", "Expected one link like quantumhub://…"));
        if (link.Kind != HubLinkKind.Run) return LaunchGui("--link", words[0]);

        return await WithHubAsync(async client =>
        {
            var request = new LinkRunRequest { PluginId = link.PluginId!, Action = link.Action! };
            foreach (var (key, value) in link.Params) request.Params[key] = value;
            var reply = await client.CallAsync(Envelopes.HubTarget, "hub.links.run", HubPayload.Proto(request));
            if (reply.ContentType is HubPayload.JsonType or HubPayload.TextType) await output.WriteLineAsync(reply.AsText());
            return Ok;
        });
    }

    private async Task<int> ReportAsync(HubClient client, List<string> words)
    {
        var request = new DiagnosticsRequest { Path = words.Count > 0 ? Path.GetFullPath(words[0]) : string.Empty };
        var report = await client.CallAsync<DiagnosticsResponse>(Envelopes.HubTarget, "hub.diagnostics.export", request);
        await output.WriteLineAsync(report.Path);
        return Ok;
    }

    private async Task<int> StartAsync(CancellationToken token)
    {
        await using (var running = await TryConnectAsync())
        {
            if (running is not null)
            {
                await output.WriteLineAsync(Loc.T("Хаб уже запущен.", "The hub is already running."));
                return Ok;
            }
        }

        if (Find("QuantumHub.Service.exe") is not { } service)
        {
            await error.WriteLineAsync(Loc.T("Не найден QuantumHub.Service.exe.", "QuantumHub.Service.exe was not found."));
            return Failed;
        }

        var start = new ProcessStartInfo(service) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(service) };
        start.ArgumentList.Add("--watch");
        if (_endpoint.Instance != HubEndpoint.DefaultInstance)
        {
            start.ArgumentList.Add("--instance");
            start.ArgumentList.Add(_endpoint.Instance);
        }

        Process.Start(start)?.Dispose();

        var deadline = DateTime.UtcNow + HubWait;
        while (DateTime.UtcNow < deadline)
        {
            await using var client = await TryConnectAsync();
            if (client is not null) return Ok;
            await Task.Delay(200, token);
        }

        await error.WriteLineAsync(Loc.T("Хаб не ответил. Причина может быть в logs\\crash.log.", "The hub did not answer. The reason may be in logs\\crash.log."));
        return Failed;
    }

    private async Task<int> StopAsync(CancellationToken token)
    {
        await using var client = await TryConnectAsync();
        if (client is null) return Ok;

        var gone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Disconnected += () => gone.TrySetResult();
        try
        {
            await client.CallAsync(Envelopes.HubTarget, "hub.shutdown");
        }
        catch (HubCallException)
        {
            // Он вполне может отключиться раньше, чем ответит.
        }

        await gone.Task.WaitAsync(HubWait, token);
        return Ok;
    }

    private async Task<int> RestartAsync(CancellationToken token)
    {
        var code = await StopAsync(token);
        return code != Ok ? code : await StartAsync(token);
    }

    /// <summary>Открывает окно программы.</summary>
    private int LaunchGui(params string[] arguments)
    {
        if (Find(Path.Combine("gui", "QuantumHub.Gui.exe")) is not { } gui)
        {
            error.WriteLine(Loc.T("Не найден QuantumHub.Gui.exe.", "QuantumHub.Gui.exe was not found."));
            return Failed;
        }

        var start = new ProcessStartInfo(gui) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(gui) };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        Process.Start(start)?.Dispose();
        return Ok;
    }

    /// <summary>Показывает или меняет запуск хаба при входе в Windows.</summary>
    private int Autostart(List<string> words)
    {
        var value = words.Count > 0 ? words[0] : string.Empty;
        if (value is not ("" or "on" or "off")) return Wrong(Loc.T("Нужно: autostart [on|off]", "Expected: autostart [on|off]"));

        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        switch (value)
        {
            case "on":
                if (Find("QuantumHub.Service.exe") is not { } service)
                {
                    error.WriteLine(Loc.T("Не найден QuantumHub.Service.exe.", "QuantumHub.Service.exe was not found."));
                    return Failed;
                }

                key.SetValue(RunHubValue, $"\"{service}\" --watch");
                break;
            case "off":
                key.DeleteValue(RunHubValue, false);
                break;
            default:
                output.WriteLine(key.GetValue(RunHubValue) is null ? "off" : "on");
                break;
        }

        return Ok;
    }

    /// <summary>Ищет файл программы: рядом с папкой cli, потом в собранной программе внутри репозитория.</summary>
    private static string? Find(string relative)
    {
        var baseDir = AppContext.BaseDirectory;
        var near = Path.GetFullPath(Path.Combine(baseDir, "..", relative));
        if (File.Exists(near)) return near;

        for (var dir = new DirectoryInfo(baseDir); dir is not null; dir = dir.Parent)
        {
            if (!File.Exists(Path.Combine(dir.FullName, "QuantumHub.sln"))) continue;
            var published = Path.Combine(dir.FullName, "artifacts", "QuantumHub", relative);
            return File.Exists(published) ? published : null;
        }

        return null;
    }

    /// <summary>Печатает сообщение как JSON, если просили.</summary>
    /// <returns><see langword="true"/>, если напечатано.</returns>
    private bool Print(IMessage message)
    {
        if (_json) output.WriteLine(JsonFormatter.Default.Format(message));
        return _json;
    }

    /// <summary>Забирает из аргументов параметр со значением.</summary>
    private static string? Take(List<string> words, string name)
    {
        var index = words.IndexOf(name);
        if (index < 0 || index + 1 >= words.Count) return null;

        var value = words[index + 1];
        words.RemoveRange(index, 2);
        return value;
    }

    /// <summary>Текст справки.</summary>
    private static string Help() => Loc.T("""
        Управление QuantumHub из командной строки.

          status                         состояние хаба
          start | stop | restart         запустить, завершить, перезапустить хаб
          gui                            открыть окно
          plugins [list]                 список плагинов
          plugins enable|disable <id>    включить или выключить плагин
          plugins restart <id>           перезапустить плагин
          plugins install <файл>         установить плагин
          plugins remove <id>            удалить плагин
          config get <плагин> [ключ]     прочитать настройку
          config set <плагин> <ключ> <значение>
          sessions                       кто подключён
          logs [-n 50] [-f]              журнал, -f продолжает показывать новые записи
          call <адрес> <метод> [тело]    вызвать метод плагина
          link <ссылка>                  открыть ссылку quantumhub://
          report [файл]                  собрать архив для отчёта об ошибке
          autostart [on|off]             запуск хаба при входе в Windows
          version                        версия

        Общие параметры: --json выводит JSON, --instance <имя> выбирает экземпляр хаба.

        """, """
        Control QuantumHub from the command line.

          status                         hub status
          start | stop | restart         start, stop or restart the hub
          gui                            open the window
          plugins [list]                 list plugins
          plugins enable|disable <id>    turn a plugin on or off
          plugins restart <id>           restart a plugin
          plugins install <file>         install a plugin
          plugins remove <id>            remove a plugin
          config get <plugin> [key]      read a setting
          config set <plugin> <key> <value>
          sessions                       who is connected
          logs [-n 50] [-f]              the log, -f keeps showing new entries
          call <target> <method> [body]  call a plugin method
          link <link>                    open a quantumhub:// link
          report [file]                  build an archive for a bug report
          autostart [on|off]             start the hub when Windows starts
          version                        version

        Common options: --json prints JSON, --instance <name> picks a hub instance.

        """);
}
