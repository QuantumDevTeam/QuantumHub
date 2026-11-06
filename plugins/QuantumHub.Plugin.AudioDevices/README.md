# Audio Devices

Id: `qh.audio`. Вид: папка с манифестом и зависимостью NAudio. Предоставляет возможность `audio.devices`.

Звуковые устройства и приложения со звуком как сервис. Про VR ничего не знает: им может пользоваться любой плагин или внешняя программа.

## API

Адрес `cap:audio.devices`, формат JSON.

| Метод | Запрос | Ответ |
|---|---|---|
| `devices.current` | нет | `{ "playback": { "id", "name" }, "recording": { "id", "name" } }` |
| `devices.list` | нет | `{ "playback": [ ... ], "recording": [ ... ] }` |
| `devices.set` | `{ "id": "..." }` | нет |
| `sessions.list` | нет | `[ { "process", "volume", "peak", "muted" } ]` |
| `sessions.volume` | `{ "process", "volume", "fadeMs" }` | нет |

Событие `audio.default.changed` приходит с телом как у `devices.current`, кто бы ни сменил устройство: плагин, другая
программа или пользователь в Windows. Изменение замечается в течение двух секунд.

```csharp
var current = await client.CallAsync("cap:audio.devices", "devices.current");
Console.WriteLine(current.AsText());
```

`sessions.list` показывает приложения, в которых сейчас идёт звук, по всем устройствам. `peak` — настоящий уровень сигнала:
больше нуля значит, что приложение правда звучит, а не просто держит сессию открытой. Приложение с несколькими
процессами сводится в одну запись, поэтому работать с ним нужно по имени, а не по PID.

Переключение идёт на все три роли Windows: Console, Multimedia и Communications.
