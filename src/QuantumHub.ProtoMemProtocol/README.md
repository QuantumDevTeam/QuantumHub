# QuantumHub.ProtoMemProtocol

Транспорты: как байты попадают из процесса в процесс. Общий код хаба и клиента. Выше этого слоя никто
не знает, память это, pipe или сокет.

| Файл | Что внутри |
|---|---|
| `Shm/ShmRing.cs` | Кольцевой буфер. Самое быстрое и самое хитрое место проекта |
| `Shm/ShmFrameChannel.cs` | Канал поверх двух колец, поток чтения, линия жизни |
| `StreamFrameChannel.cs` | Канал поверх pipe или TCP |
| `FrameCodec.cs` | Формат кадра |
| `Envelopes.cs` | Фабрика конвертов |
| `PipeListener.cs` | Приём подключений к pipe |
| `HubEndpoint.cs` | Имена pipe и мьютекса, `endpoint.json` |
| `EventPump.cs` | Упорядоченная доставка событий |

## Начни с

`IFrameChannel` в `Contracts`, затем `StreamFrameChannel` как самая простая реализация, и только потом
`ShmFrameChannel` и `ShmRing`.

Перед `ShmRing` стоит прочитать про барьеры памяти в статье «Приёмы в коде» (`docs/TECHNIQUES.md`).

## Как добавить транспорт

Реализуй `IFrameChannel` и добавь выбор в `IpcServer.OpenChannel`. Если транспорт потоковый, например
WebSocket, можно просто обернуть его в `Stream` и отдать в `StreamFrameChannel`.
