# Как написать плагин

## DLL-плагин

Проект:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0-windows</TargetFramework>
    <EnableDynamicLoading>true</EnableDynamicLoading>
  </PropertyGroup>
  <ItemGroup>
    <!-- Хаб даёт контракты сам. Своя копия сломала бы совпадение типов. -->
    <ProjectReference Include="..\QuantumHub.Contracts\QuantumHub.Contracts.csproj">
      <Private>false</Private>
      <ExcludeAssets>runtime</ExcludeAssets>
    </ProjectReference>
  </ItemGroup>
</Project>
```

Класс:

```csharp
[HubPlugin]
public sealed class AudioSwitcher : IHubPlugin, IUiProvider
{
    public Task StartAsync(IPluginContext context, CancellationToken token)
    {
        context.Bus.Handle("audio.switch", (request, _) =>
        {
            var device = request.Payload.AsText();
            context.Config.Set("lastDevice", device);
            context.Ui.Set("current", device);
            return ValueTask.FromResult(HubPayload.Empty);
        });
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken token) => Task.CompletedTask;

    public IEnumerable<UiPage> GetPages()
    {
        yield return Ui.Page("main", "Звук", "E767",
            Ui.KeyValue("Сейчас", "current"),
            Ui.Button("vr", "На VR-гарнитуру", accent: true));
    }

    public void OnUiEvent(UiEvent e) { /* e.NodeId == "vr" */ }
}
```

Всё, что даёт хаб, лежит в `IPluginContext`:

| | |
|---|---|
| `Bus` | вызовы, события, свои методы, разрешение зависимостей |
| `Config` | настройки: хранит хаб, значения по умолчанию из манифеста |
| `Paths` | папка установки, личные данные, кеш |
| `Logger` | журнал, виден в GUI |
| `Ui` | состояние, к которому привязан интерфейс |
| `Manifest` | свой манифест |

## Манифест

`plugin.jsonc` рядом с DLL. Обязательны только `id` и `version`.

```jsonc
{
  "id": "acme.audio",              // латиница, цифры, точки, дефисы
  "name": "Audio Switcher",
  "version": "1.0.0",
  "description": "Переключает звук на VR",
  "author": "Acme",
  "icon": "embedded:icon.png",     // или файл рядом: "icon.png"

  "hub": ">=1.0.0 <2.0.0",         // какие версии хаба подходят, по умолчанию любые

  "activation": "startup",         // или "onDemand": грузится, когда понадобится

  "service": { "assembly": "Acme.Audio.dll" },   // "type" не нужен, есть [HubPlugin]
  "ui": { "assembly": "Acme.Audio.Ui.dll" },     // необязательно, нативный интерфейс

  "provides": [ { "name": "audio.routing", "version": "1.0.0" } ],

  "dependencies": [
    { "id": "qh.activity", "version": ">=1.0.0" },
    { "capability": "system.specs", "optional": true, "when": "lazy" }
  ],

  // Этого достаточно, чтобы в GUI появилась страница настроек.
  "settings": [
    { "key": "autoSwitch", "type": "bool", "title": "Автопереключение", "default": true },
    { "key": "volume", "type": "number", "title": "Громкость", "default": 50, "min": 0, "max": 100 },
    { "key": "mode", "type": "choice", "title": "Режим", "options": ["a", "b"], "default": "a" },
    { "key": "apps", "type": "list", "group": "Приложения", "title": "Список", "default": ["chrome", "discord"] }
  ]
}
```

Типы настроек: `string`, `secret` (скрытый ввод), `bool`, `number` (с `min` и `max` станет ползунком), `choice` и `list`, по строке на значение. `group`
объединяет настройки под общим заголовком. Читаются они как обычно: `Config.Get<List<string>>("apps")`.

Настройки, API и внешние части необязательны. Плагин объявляет только то, что ему нужно.

Версии: `*`, `1.2.3`, `>=1.0.0 <2.0.0`, `^1.2.0`, `~1.2.0`.

## Перезапуск зависимости

Когда перезапускают плагин, от которого ты зависишь, твой плагин продолжает работать. Подписки на месте, а вызовы на это время просто ждут. Если ты передал зависимости что-то, что она держит в памяти, или запомнил её состояние, реализуй `IDependencyAware`:

```csharp
public Task OnDependencyRestartedAsync(string providerId, CancellationToken token) =>
    _context.Bus.CallAsync("cap:user.activity", "process.watch", names, token: token).AsTask();
```

Метод вызывается для всех зависимостей, включая необязательные.

## Ссылки

Плагин может объявить действия, которые запускаются ссылкой `quantumhub://run/<id>/<действие>?параметр=значение`:

```jsonc
"links": [ { "action": "mute", "title": "Микрофон", "description": "Переключает мьют." } ]
```

Тексты переводятся в блоке `locales`, в разделе `links`. Действие приходит в метод `link.run`:

```csharp
context.Bus.Handle(HubLink.RunMethod, async (request, _) =>
{
    var link = request.Payload.AsProto<LinkRunRequest>();
    await RunAsync(link.Action, link.Params);
    return HubPayload.Empty;
});
```

Ссылку может составить кто угодно, поэтому параметры нужно проверять, а опасные действия не объявлять вовсе. Подробнее в статье [Управление извне](AUTOMATION.md).

## В каком виде поставлять

| Вид | Как | Пример |
|---|---|---|
| Папка | `plugins/<id>/` с `plugin.jsonc` | UUID Backend |
| Одна DLL | манифест встроен как ресурс с `LogicalName="plugin.jsonc"` | System Specs |
| Архив | `.qhp` или `.zip` с содержимым папки | `artifacts/release/plugins` |

Пользователю удобнее всего `.qhp`: установщик связывает этот тип файлов с QuantumHub, и плагин ставится двойным щелчком.
Ещё файл можно перетащить в окно на страницу «Плагины», нажать там кнопку, вызвать `hub.plugins.install` или положить
его в папку `plugins` перед запуском хаба. Перезапускать хаб не надо. Если не хватает зависимости, плагин встанет и подождёт её.

Установленный так плагин переживает обновление программы, а встроенные заменяются версиями из установщика.

## Язык

Программа говорит на русском и английском: берётся язык системы, а в настройках его можно сменить. Текст в коде плагина пишется парой:

```csharp
Ui.Button("start", Loc.T("Включить", "Turn on"));
```

`GetPages` вызывается при каждом открытии страницы, поэтому страницы переводятся сами. Если плагин уже показал текст через
`Ui.Set`, он может отправить его заново в `OnLanguageChanged`. Иначе текст сменится при следующем обновлении.

Текст манифеста переводится в блоке `locales`. Что в нём не указано, показывается как в манифесте:

```jsonc
"locales": {
  "en": {
    "description": "Keeps a headset awake.",
    "groups": { "Шлем": "Headset" },
    "settings": {
      "mode": { "title": "Mode", "description": "How it works.", "options": { "fast": "Fast" } }
    }
  }
}
```

Подписи вариантов типа `choice` задаются полем `optionLabels`, а в конфиг всегда пишется значение.

## Декларативный UI

Помощники лежат в `QuantumHub.Contracts.Ui.Ui`.

| Узел | Свойства | Заметки |
|---|---|---|
| `stack` | `orientation`, `spacing` | контейнер |
| `card` | `title`, `description` | контрол из первого потомка встанет справа |
| `text` | `text`, `style`: `title`, `subtitle`, `body`, `caption`, `mono` | |
| `keyvalue` | `key`, `value` | |
| `button` | `label`, `style`: `accent` | событие `click` |
| `toggle`, `textbox`, `number`, `slider`, `choice` | `value`, `placeholder`, `min`, `max`, `options` | событие `changed` |
| `infobar` | `title`, `message`, `severity` | |
| `progress` | `value` | без значения крутится бесконечно |
| `separator` | | |

Любое свойство можно привязать к ключу состояния: `Ui.BoundText("value")` меняется от
`context.Ui.Set("value", ...)`. Привязка к `config:<ключ>` читает и пишет настройку напрямую, и код
плагина для этого не нужен вообще.

## Нативный UI

Отдельная сборка с классом `INativePluginPage`, ссылка на `QuantumHub.Ui.Contracts` с
`Private=false`. Пример: `plugins/QuantumHub.Plugin.Activity.Ui`. Страницы собирай кодом:
скомпилированный XAML в динамически загруженной сборке рассчитывает на ресурсы приложения.

## Обновление, пока страница открыта

Узел `Ui.Live("id", 2000)` не виден на странице, но сообщает плагину, что её смотрят: приходят события `opened`, `tick` через
каждый интервал и `closed`. Пока окно свёрнуто в трей, `tick` не приходит. Так данные обновляются сами и только когда нужны:

```csharp
public void OnUiEvent(UiEvent uiEvent)
{
    if (uiEvent.NodeId == "live" && uiEvent.Event != "closed") Refresh();
}
```

## Окна из плагина

Хаб работает без окон, поэтому окно живёт в отдельном приложении. Плагин запускает его через `CompanionProcess`, а приложение
подключается к хабу через `HubClient` и получает имя экземпляра в аргументе `--instance`. Пример: `companions/QuantumHub.VrChatPanel`.

Если нужно только сообщить что-то пользователю, своё окно не нужно: вызови `cap:ui.notifications`.

## SDK

Вместе с релизом выходят пакеты NuGet, папка `sdk`: `QuantumHub.Contracts` для DLL-плагина и `QuantumHub.Client` для внешнего. Что в них меняется и как, описано в [политике версий](Versioning.md).

## Внешний плагин на .NET

```csharp
var client = await HubClient.ConnectAsync(new HubClientOptions
{
    ClientId = "acme.linecounter.rider",
    Version = "1.0.0",
    AutoReconnect = true,
    Dependencies =
    [
        new Dependency { Id = "acme.linecounter", Version = ">=1.0.0" },
        new Dependency { Capability = "system.specs", Optional = true, When = "lazy" },
    ],
});

await client.CallAsync("acme.linecounter", "lines.add", HubPayload.Json(new { file, count }));
```

`HubRejectedException` приходит, если обязательной зависимости нет, а в `Dependencies` указано, чего
именно. `client` — такой же `IHubBus`, как `context.Bus` у DLL-плагина.

Внешний плагин тоже может показывать интерфейс: для этого достаточно обработать `ui.get` и `ui.event`.

## Внешний плагин на другом языке

См. [PROTOCOL.md](PROTOCOL.md) и рабочий пример `samples/python/hub_client.py`.
