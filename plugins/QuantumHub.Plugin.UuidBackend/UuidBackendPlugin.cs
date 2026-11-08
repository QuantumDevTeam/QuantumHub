using QuantumHub.Contracts.Links;
using QuantumHub.Contracts.Bus;
using QuantumHub.Contracts.Plugin;
using QuantumHub.Contracts.Proto;
using QuantumHub.Contracts.Ui;

namespace QuantumHub.Plugin.UuidBackend;

/// <summary>Что присылает внешняя часть плагина.</summary>
/// <param name="Kind">"uuid" или "text".</param>
/// <param name="Value">Значение.</param>
public sealed record SubmitRequest(string Kind, string Value);

/// <summary>Что возвращается отправителю.</summary>
/// <param name="Total">Сколько значений получено за всё время.</param>
/// <param name="Stored">Значение так, как оно показано.</param>
public sealed record SubmitResponse(int Total, string Stored);

/// <summary>
/// Хабовая половина разделённого плагина. Значения живут здесь, в конфиге хаба, а внешняя половина только
/// присылает их: именно так плагин для Rider присылал бы счётчик строк.
/// </summary>
[HubPlugin]
public sealed class UuidBackendPlugin : IHubPlugin, IUiProvider
{
    /// <summary>Защищает чтение и запись истории: запросы могут идти параллельно.</summary>
    private readonly object _gate = new();
    /// <summary>Контекст от хаба.</summary>
    private IPluginContext _context = null!;

    /// <inheritdoc/>
    public Task StartAsync(IPluginContext context, CancellationToken token)
    {
        _context = context;
        context.Bus.Handle("value.submit", (request, _) =>
        {
            var submit = request.Payload.AsJson<SubmitRequest>()
                         ?? throw new HubCallException(StatusCode.InvalidArgument, Loc.T("Пустой запрос.", "Empty request."));
            return ValueTask.FromResult(HubPayload.Json(Store(submit.Kind, submit.Value, request.Source)));
        });
        context.Bus.Handle(HubLink.RunMethod, (request, _) =>
        {
            var link = request.Payload.AsProto<LinkRunRequest>();
            if (!link.Params.TryGetValue("value", out var value) || value.Length == 0)
                throw new HubCallException(StatusCode.InvalidArgument, Loc.T("В ссылке нет значения.", "The link has no value."));
            return ValueTask.FromResult(HubPayload.Json(Store("link", value, request.Source)));
        });
        context.Bus.Handle("value.latest", (_, _) =>
            ValueTask.FromResult(HubPayload.Text(context.Config.Get("lastValue", string.Empty)!)));

        context.Config.Changed += key =>
        {
            if (key is "uppercase" or "historySize") PushState();
        };

        PushState();
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken token) => Task.CompletedTask;

    /// <summary>Запоминает значение, обновляет интерфейс и сообщает подписчикам.</summary>
    private SubmitResponse Store(string kind, string value, string source)
    {
        var config = _context.Config;
        lock (_gate)
        {
            var size = (int)config.Get("historySize", 5d);
            var history = config.Get("history", new List<string>())!;
            history.Insert(0, $"{DateTime.Now:HH:mm:ss}  {kind}  {value}");
            if (history.Count > size) history.RemoveRange(size, history.Count - size);

            var total = config.Get("total", 0) + 1;
            config.Set("history", history);
            config.Set("total", total);
            config.Set("lastValue", value);
            config.Set("lastKind", kind);
            config.Set("lastSource", source);

            PushState();
            _ = _context.Bus.PublishAsync("uuid.updated", HubPayload.Text(value));
            _context.Logger.Info(Loc.T($"Сохранено {kind} от {source}", $"Stored {kind} from {source}"));
            return new SubmitResponse(total, Display(value));
        }
    }

    /// <summary>Значение с учётом настройки регистра.</summary>
    private string Display(string value) => _context.Config.Get("uppercase", false) ? value.ToUpperInvariant() : value;

    /// <summary>Передаёт текущие значения в интерфейс.</summary>
    private void PushState()
    {
        var config = _context.Config;
        var ui = _context.Ui;
        ui.Set("value", Display(config.Get("lastValue", Loc.T("ещё ничего не приходило", "nothing received yet"))!));
        ui.Set("kind", config.Get("lastKind", "—")!);
        ui.Set("source", config.Get("lastSource", "—")!);
        ui.Set("total", config.Get("total", 0).ToString());
        ui.Set("history", string.Join('\n', config.Get("history", new List<string>())!.Select(Display)));
    }

    /// <inheritdoc/>
    public IEnumerable<UiPage> GetPages()
    {
        yield return Ui.Page("main", Loc.T("Значения", "Values"), "E8D2",
            Ui.Card(Loc.T("Последнее значение", "Latest value"), Loc.T("То, что прислала внешняя программа.", "What the external program sent."),
                Ui.BoundText("value", "mono")),
            Ui.KeyValue(Loc.T("Тип", "Type"), "kind"),
            Ui.KeyValue(Loc.T("Отправитель", "Sender"), "source"),
            Ui.KeyValue(Loc.T("Всего получено", "Total received"), "total"),
            Ui.Row(
                Ui.Button("generate", Loc.T("Сгенерировать здесь", "Generate here"), accent: true),
                Ui.Button("clear", Loc.T("Очистить историю", "Clear history"))),
            Ui.Text(Loc.T("История", "History"), "subtitle"),
            Ui.BoundText("history", "mono"));
    }

    /// <inheritdoc/>
    public void OnUiEvent(UiEvent uiEvent)
    {
        switch (uiEvent.NodeId)
        {
            case "generate":
                Store("uuid", Guid.NewGuid().ToString(), "GUI");
                break;
            case "clear":
                lock (_gate)
                {
                    _context.Config.Set("history", new List<string>());
                    PushState();
                }

                break;
        }
    }
}
