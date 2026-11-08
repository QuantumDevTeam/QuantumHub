# Протокол

Всё описано в `contracts/proto`. Сгенерируй protobuf под свой язык, и ты клиент.

## Подключение

1. Прочитай `%APPDATA%\QuantumHub\endpoint.json`. В `pipePath` лежит путь к pipe, а `tcpPort` и
   `tcpToken` появятся, только если TCP включён.
2. Открой pipe. В Node это `net.connect(pipePath)`, на JVM — `RandomAccessFile`, в Python — обычный
   `open(path, "r+b", buffering=0)`.
3. Отправь кадр с `Envelope`: `kind = REQUEST`, `method = "hub.hello"`, в `payload` лежит `HelloRequest`.
4. Получи кадр с `Envelope`. Если `status` не `OK`, хаб закроет соединение. В `error` будет причина,
   а в `payload`, если дело в зависимостях, `HelloResponse` с подробностями.
5. Если `HelloResponse.transport` равен `stream`, дальше всё идёт по этому же соединению.
   Если `shm`, подключись к памяти, как описано ниже, и **не закрывай pipe**: он сообщает хабу, что
   ты жив.

Кадр: `uint32` длина тела, little-endian, и за ним тело (сериализованный `Envelope`). Максимум 256 МБ.

## Сообщения

`REQUEST` ждёт `RESPONSE` с тем же `id`. `id` выбирает отправитель, уникальность нужна только в
пределах соединения. `EVENT` ответа не требует, в `method` лежит топик.

Запросы могут приходить и к тебе: если у тебя нет метода, верни `RESPONSE` со статусом `NOT_FOUND`.

`target`:

| | |
|---|---|
| `hub` | методы самого хаба |
| `<id>` | плагин или клиент по id |
| `cap:<имя>` | тот, кто предоставляет возможность |
| `session:<id>` | конкретное соединение |

## Методы хаба

| Метод | Запрос → ответ |
|---|---|
| `hub.ping` | эхо |
| `hub.status` | → `HubStatus` |
| `hub.resolve` | `ResolveRequest` → `ResolveResponse` |
| `hub.subscribe`, `hub.unsubscribe` | `SubscribeRequest` |
| `hub.config.get`, `hub.config.set` | `ConfigGetRequest` → `ConfigValue`, `ConfigSetRequest` |
| `hub.plugins.list` | → `PluginList` |
| `hub.plugins.enable` | `SetEnabledRequest` |
| `hub.plugins.restart`, `hub.plugins.uninstall` | `PluginRef` |
| `hub.plugins.install` | `InstallRequest` → `PluginInfo` |
| `hub.plugins.asset` | `AssetRequest` → `AssetResponse` |
| `hub.sessions.list` | → `SessionList` |
| `hub.sessions.history` | → `SessionHistory` |
| `hub.logs.get` | → `LogList` |
| `hub.links.run` | `LinkRunRequest` → ответ плагина |
| `hub.diagnostics.export` | `DiagnosticsRequest` → `DiagnosticsResponse` |
| `hub.ui.get` | `UiRequest` → `UiResponse` |
| `hub.ui.event` | `UiEvent` |
| `hub.shutdown` | |

Топики: `hub.stopping` (id того, кто попросил завершить хаб, текстом), `hub.plugin.state` (`PluginInfo`), `hub.plugin.removed` (id текстом), `hub.session.connected` и
`hub.session.disconnected` (`SessionInfo`), `hub.ui.state` (`UiStatePatch`), `hub.log` (`LogEntry`),
`config.<id>` (`ConfigChanged`). В подписке работает `префикс.*`.

Если хочешь показывать интерфейс, обрабатывай `ui.get` (ответ `UiResponse`) и `ui.event` (запрос `UiEvent`).

`client_id` не может быть пустым, равным `hub` или начинаться с `cap:` и `session:`: он становится адресом в роутере.

Тексты ошибок приходят на языке, выбранном в хабе. Разбирать их не надо, для этого есть `status`.

Внешний клиент узнаёт о перезапуске плагина из топика `hub.plugin.state`: подписки при этом не теряются.

## Shared memory

Имя секции приходит в `ShmInfo.name`, размер кольца — в `ring_size` (степень двойки). Все числа
little-endian.

```
0       u32 magic = 0x4D485148
4       u32 версия раскладки = 1
8       u32 ring_size
256     управление кольцом клиент -> хаб (256 байт)
512     управление кольцом хаб -> клиент (256 байт)
4096    данные кольца клиент -> хаб  (ring_size байт)
4096+R  данные кольца хаб -> клиент  (ring_size байт)
```

Управление кольцом:

```
0    i64 head               позиция читателя, пишет только читатель
64   i64 tail               позиция писателя, пишет только писатель
128  i32 consumer_sleeping  читатель спит на событии data
192  i32 producer_sleeping  писатель спит на событии space
```

Позиции только растут, индекс в данных — `position & (ring_size - 1)`. Занято `tail - head` байт.
В кольце лежит поток тех же кадров, кадр может переходить через край и быть больше кольца.

События Windows, auto-reset: `<name>.c2s.data`, `<name>.c2s.space`, `<name>.s2c.data`, `<name>.s2c.space`.

Писатель: скопировать байты, записать `tail` с release-семантикой, **полный барьер**, прочитать
`consumer_sleeping`, и если там не 0, выставить событие `data`.

Читатель перед сном: записать `consumer_sleeping = 1`, **полный барьер**, ещё раз сравнить `tail` и
`head`, и только потом ждать событие. Проснувшись, сбросить флаг. Со `space` всё то же самое
зеркально. Оба барьера обязательны: без них возможно потерянное пробуждение и вечное ожидание.

Если `tail - head` выходит за `ring_size`, память повреждена: закрой соединение.
