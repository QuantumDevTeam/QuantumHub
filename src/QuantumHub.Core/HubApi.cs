using System.Text.Json;
using System.Text.Json.Nodes;
using Google.Protobuf;
using QuantumHub.Contracts.Bus;
using QuantumHub.Contracts.Proto;
using QuantumHub.Contracts.Ui;
using QuantumHub.Core.Routing;
using QuantumHub.ProtoMemProtocol;

namespace QuantumHub.Core;

/// <summary>Собственные методы хаба, адрес "hub". Полный список — в docs/PROTOCOL.md.</summary>
/// <param name="hub">Хаб.</param>
public sealed class HubApi(Hub hub) : IEndpoint
{
    /// <summary>Префикс ключа состояния, привязанного к настройке.</summary>
    private const string ConfigBindPrefix = "config:";

    /// <inheritdoc/>
    public string Id => Envelopes.HubTarget;

    /// <summary>Хаб не подписывается на события.</summary>
    /// <param name="hubEvent">Событие.</param>
    public void DeliverEvent(Envelope hubEvent)
    {
    }

    /// <inheritdoc/>
    public async Task<Envelope> RequestAsync(Envelope request, CancellationToken token)
    {
        try
        {
            return Envelopes.Response(request, await DispatchAsync(request, request.GetPayload(), token));
        }
        catch (HubCallException ex)
        {
            return Envelopes.Error(request, ex.Status, ex.Message);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or InvalidProtocolBufferException)
        {
            return Envelopes.Error(request, StatusCode.FailedPrecondition, ex.Message);
        }
    }

    /// <summary>Выбирает обработчик по имени метода.</summary>
    private async ValueTask<HubPayload> DispatchAsync(Envelope request, HubPayload payload, CancellationToken token)
    {
        switch (request.Method)
        {
            case "hub.ping":
                return payload;

            case "hub.status":
                return HubPayload.Proto(hub.GetStatus());

            case "hub.shutdown":
                hub.RequestShutdown();
                return default;

            case "hub.plugins.list":
                return HubPayload.Proto(hub.Plugins.List());

            case "hub.plugins.enable":
                var enable = payload.AsProto<SetEnabledRequest>();
                await hub.Plugins.SetEnabledAsync(enable.Id, enable.Enabled);
                return default;

            case "hub.plugins.restart":
                await hub.Plugins.RestartAsync(payload.AsProto<PluginRef>().Id);
                return default;

            case "hub.plugins.install":
                return HubPayload.Proto(await hub.Plugins.InstallAsync(payload.AsProto<InstallRequest>().Path));

            case "hub.plugins.uninstall":
                await hub.Plugins.UninstallAsync(payload.AsProto<PluginRef>().Id);
                return default;

            case "hub.plugins.asset":
                return HubPayload.Proto(GetAsset(payload.AsProto<AssetRequest>()));

            case "hub.resolve":
                var response = new ResolveResponse();
                foreach (var dependency in payload.AsProto<ResolveRequest>().Dependencies)
                    response.Results.Add(await hub.Plugins.ResolveAsync(dependency));
                return HubPayload.Proto(response);

            case "hub.config.get":
                return HubPayload.Proto(GetConfig(request, payload.AsProto<ConfigGetRequest>()));

            case "hub.config.set":
                var set = payload.AsProto<ConfigSetRequest>();
                var owner = set.PluginId.Length > 0 ? set.PluginId : request.Source;
                hub.Config.Set(owner, set.Key, ParseJson(set.Json));
                return default;

            case "hub.sessions.list":
                return HubPayload.Proto(hub.Server.ListSessions());

            case "hub.sessions.history":
                return HubPayload.Proto(hub.Server.History());

            case "hub.logs.get":
                return HubPayload.Proto(new LogList { Entries = { hub.Log.Recent() } });

            case "hub.ui.get":
                return HubPayload.Proto(await GetUiAsync(payload.AsProto<UiRequest>().PluginId, token));

            case "hub.ui.event":
                await HandleUiEventAsync(payload.AsProto<UiEvent>(), token);
                return default;

            default:
                throw new HubCallException(StatusCode.NotFound, $"The hub has no method '{request.Method}'.");
        }
    }

    /// <summary>Разбирает JSON из запроса.</summary>
    private static JsonNode? ParseJson(string json)
    {
        try
        {
            return json.Length == 0 ? null : JsonNode.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new HubCallException(StatusCode.InvalidArgument, $"Invalid JSON: {ex.Message}");
        }
    }

    /// <summary>Читает настройку. Без явного плагина — настройку вызывающего.</summary>
    private ConfigValue GetConfig(Envelope request, ConfigGetRequest get)
    {
        var owner = get.PluginId.Length > 0 ? get.PluginId : request.Source;
        var node = get.Key.Length > 0 ? hub.Config.Get(owner, get.Key) : hub.Config.Snapshot(owner);
        return new ConfigValue { Found = node is not null, Json = node?.ToJsonString() ?? string.Empty };
    }

    /// <summary>Отдаёт файл или встроенный ресурс плагина. Проверяет, что путь не выходит за папку плагина.</summary>
    private AssetResponse GetAsset(AssetRequest request)
    {
        var manifest = hub.Plugins.GetManifest(request.PluginId)
                       ?? throw new HubCallException(StatusCode.NotFound, $"Plugin '{request.PluginId}' is not installed.");
        var dir = hub.Plugins.GetDirectory(request.PluginId)!;
        var name = request.Name.Length > 0 ? request.Name : manifest.Icon;
        if (name.Length == 0) throw new HubCallException(StatusCode.NotFound, "The plugin has no icon.");

        const string embedded = "embedded:";
        if (name.StartsWith(embedded, StringComparison.Ordinal))
        {
            var resource = name[embedded.Length..];
            var bytes = Plugins.ManifestReader.ReadEmbedded(Path.Combine(dir, manifest.Service.Assembly), resource)
                        ?? throw new HubCallException(StatusCode.NotFound, $"No embedded resource '{resource}'.");
            return new AssetResponse { Data = ByteString.CopyFrom(bytes), FileName = resource };
        }

        var root = Path.GetFullPath(dir) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(root, name));
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
            throw new HubCallException(StatusCode.NotFound, $"No asset '{name}'.");

        return new AssetResponse { Data = ByteString.CopyFrom(File.ReadAllBytes(path)), FileName = Path.GetFileName(path) };
    }

    /// <summary>
    /// Собирает всё для страницы плагина: страницы самого плагина, автоматическую страницу настроек,
    /// путь к нативному UI и текущее состояние.
    /// </summary>
    private async Task<UiResponse> GetUiAsync(string pluginId, CancellationToken token)
    {
        var response = new UiResponse();
        var manifest = hub.Plugins.GetManifest(pluginId);

        // Плагин по запросу запускается, когда его страницу открыли: это и есть запрос.
        if (manifest?.Activation == "onDemand" && !hub.Plugins.IsRunning(pluginId))
        {
            try
            {
                await hub.Plugins.EnsureStartedAsync(pluginId).WaitAsync(token);
            }
            catch (HubCallException ex)
            {
                hub.Log.Warning($"{pluginId} did not start for its page: {ex.Message}");
            }
        }

        // Интерфейс может отдавать и подключённый клиент, поэтому спрашиваем всех, кто жив.
        if (hub.Plugins.IsRunning(pluginId) || hub.Router.IsRegistered(pluginId))
        {
            var request = Envelopes.Request(Envelopes.HubTarget, pluginId, "ui.get", default);
            var answer = await hub.Router.RequestAsync(request, token);
            if (answer.Status == StatusCode.Ok && !answer.Payload.IsEmpty)
                response.MergeFrom(answer.GetPayload().AsProto<UiResponse>());
        }

        if (manifest is not null)
        {
            if (manifest.Settings.Count > 0) response.Pages.Add(BuildSettingsPage(manifest));

            if (manifest.Ui is { Assembly.Length: > 0 } ui)
            {
                response.NativeAssembly = Path.Combine(hub.Plugins.GetDirectory(pluginId)!, ui.Assembly);
                response.NativeType = ui.Type;
            }
        }

        foreach (var (key, value) in hub.UiState.Get(pluginId)) response.State[key] = value;
        foreach (var (key, value) in hub.Config.Snapshot(pluginId))
            response.State[ConfigBindPrefix + key] = FormatConfigValue(value);

        return response;
    }

    /// <summary>Показывает значение настройки как текст для интерфейса.</summary>
    /// <param name="value">Значение.</param>
    public static string FormatConfigValue(JsonNode? value) => value switch
    {
        null => string.Empty,
        JsonValue text when text.GetValueKind() == JsonValueKind.String => text.GetValue<string>(),
        JsonArray list => string.Join('\n', list.Select(item => FormatConfigValue(item))),
        _ => value.ToJsonString(),
    };

    /// <summary>Строит страницу, которую плагин с объявленными настройками получает без единой строки кода.</summary>
    private static UiPage BuildSettingsPage(PluginManifest source)
    {
        var manifest = Plugins.ManifestLocalizer.Apply(source, Loc.Language);
        var page = Ui.Page("settings", Loc.T("Настройки", "Settings"), "E713");
        var group = string.Empty;
        foreach (var setting in manifest.Settings)
        {
            if (setting.Group.Length > 0 && setting.Group != group)
            {
                group = setting.Group;
                page.Root.Children.Add(Ui.Text(group, "subtitle"));
            }

            var control = setting.Type switch
            {
                "bool" => Ui.Node("toggle", setting.Key),
                "number" when setting.Max > setting.Min => Ui.Node("slider", setting.Key)
                    .Prop("min", setting.Min.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    .Prop("max", setting.Max.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                "number" => Ui.Node("number", setting.Key),
                "choice" => Ui.Node("choice", setting.Key)
                    .Prop("options", string.Join('\n', setting.Options))
                    .Prop("labels", string.Join('\n', setting.Options.Select(option =>
                        setting.OptionLabels.TryGetValue(option, out var label) ? label : option))),
                "list" => Ui.Node("textbox", setting.Key).Prop("multiline", "true"),
                "secret" => Ui.Node("password", setting.Key),
                _ => Ui.Node("textbox", setting.Key),
            };
            control.Bind("value", ConfigBindPrefix + setting.Key);
            page.Root.Children.Add(Ui.Card(setting.Title.Length > 0 ? setting.Title : setting.Key, setting.Description, control));
        }

        return page;
    }

    /// <summary>Обрабатывает действие в GUI. Привязанное к настройке записывается сразу, остальное уходит плагину.</summary>
    private async Task HandleUiEventAsync(UiEvent uiEvent, CancellationToken token)
    {
        if (uiEvent.Bind.StartsWith(ConfigBindPrefix, StringComparison.Ordinal))
        {
            var key = uiEvent.Bind[ConfigBindPrefix.Length..];
            hub.Config.Set(uiEvent.PluginId, key, ConvertSetting(uiEvent.PluginId, key, uiEvent.Value));
            return;
        }

        var request = Envelopes.Request(Envelopes.HubTarget, uiEvent.PluginId, "ui.event", HubPayload.Proto(uiEvent));
        (await hub.Router.RequestAsync(request, token)).Unwrap();
    }

    /// <summary>GUI присылает текст, а настоящий тип значения известен из манифеста.</summary>
    private JsonNode? ConvertSetting(string pluginId, string key, string value)
    {
        var type = hub.Plugins.GetManifest(pluginId)?.Settings.FirstOrDefault(setting => setting.Key == key)?.Type;
        return type switch
        {
            "bool" => JsonValue.Create(bool.TryParse(value, out var flag) && flag),
            "number" => JsonValue.Create(double.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var number) ? number : 0),
            "list" => new JsonArray(value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(line => (JsonNode?)JsonValue.Create(line)).ToArray()),
            _ => JsonValue.Create(value),
        };
    }
}
