# Notifications

Id: `qh.ui.notify`. Вид: папка с манифестом и внешней частью [`companions/QuantumHub.Notifier`](../../companions/QuantumHub.Notifier/README.md).
Предоставляет возможность `ui.notifications`.

Небольшие окошки поверх экрана, которыми может пользоваться любой плагин. Они не забирают фокус и пропускают щелчки сквозь себя,
если в них нет кнопок.

## Почему есть внешняя часть

Хаб — фоновый процесс без окон, а уведомления должны работать и при закрытом GUI. Поэтому плагин запускает маленькое
приложение при первом уведомлении, и оно подключается к хабу обычным клиентом. Если хаб пропал, оно закрывается само.

## API

Адрес `cap:ui.notifications`, формат JSON.

```csharp
await bus.CallAsync("cap:ui.notifications", "notify.show", HubPayload.Json(new
{
    key = "music",        // окошко с тем же ключом обновится, а не появится новое
    title = "Заголовок",
    subtitle = "Вторая строка",
    imageBase64 = "...",  // необязательно
    badge = "pause",      // необязательно: значок паузы поверх картинки
}));
```

## Кнопки

Окошко может предложить действие. Нужен `key`: по нему ты узнаешь своё окошко в событии.

```csharp
await bus.SubscribeAsync("notify.action", e =>
{
    // Тело: { "key": "mic", "action": "switch" }. Событие видят все, чужие ключи надо пропускать.
});

await bus.CallAsync("cap:ui.notifications", "notify.show", HubPayload.Json(new
{
    key = "mic",
    title = "Сигнал на другом микрофоне",
    durationSeconds = 12,  // вместо обычной настройки, от 1 до 120: кнопку надо успеть нажать
    actions = new[] { new { id = "switch", label = "Переключить" }, new { id = "dismiss", label = "Не сейчас" } },
}));

// Если предложение устарело раньше времени:
await bus.CallAsync("cap:ui.notifications", "notify.hide", HubPayload.Json(new { key = "mic" }));
```

Окошко с кнопками ловит щелчки, но фокус всё равно не забирает. После нажатия оно закрывается. Метод `notify.action` вызывает
внешняя часть, плагинам он не нужен.
