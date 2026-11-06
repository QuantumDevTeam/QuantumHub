# QuantumHub.Client

SDK для внешних плагинов на .NET. Им же пользуется GUI.

```csharp
await using var client = await HubClient.ConnectAsync(new HubClientOptions
{
    ClientId = "acme.linecounter.rider",
    Dependencies = [new Dependency { Id = "acme.linecounter", Version = ">=1.0.0" }],
});

await client.CallAsync("acme.linecounter", "lines.add", HubPayload.Json(new { count = 5 }));
```

| Файл | Что внутри |
|---|---|
| `HubClient.cs` | Подключение, вызовы, подписки, переподключение |
| `HubClientOptions.cs` | Настройки |

`HubClient` реализует `IHubBus`, то есть тот же интерфейс, что получает DLL-плагин.

## Как он устроен

- Поток чтения разбирает кадры и раздаёт их по типу. Ничего долгого там не делается.
- Ответы завершают ожидание в `_pending`, ключ — id запроса.
- Запросы к клиенту уходят в пул потоков.
- События идут через `EventPump`, по порядку.
- С `AutoReconnect` клиент сам возвращается после пропажи хаба и восстанавливает подписки.
