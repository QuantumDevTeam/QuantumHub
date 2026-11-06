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

## Окна из плагина

Хаб работает без окон, поэтому окно живёт в отдельном приложении. Плагин запускает его через `CompanionProcess`, а приложение
подключается к хабу через `HubClient` и получает имя экземпляра в аргументе `--instance`. Пример: `companions/QuantumHub.VrChatPanel`.

Если нужно только сообщить что-то пользователю, своё окно не нужно: вызови `cap:ui.notifications`.

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
