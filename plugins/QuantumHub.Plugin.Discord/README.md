# Discord

Id: `qh.discord`. Вид: папка с манифестом. Предоставляет возможность `discord.client`.

Клиент Discord без окон: подключается к запущенному приложению по локальному пайпу (`discord-ipc-N`) и отдаёт его другим плагинам.
Сам ничего не решает: это библиотека в виде плагина.

## Настройка

Два уровня, и первый работает без авторизации:

| Что работает | Что нужно |
|---|---|
| Связь, текущий пользователь, Rich Presence | Client ID |
| Мьют, канал, участники, пинг, серверы | Ещё Client Secret и кнопка «Авторизовать» |

1. Создай приложение на [discord.com/developers](https://discord.com/developers/applications) или возьми то, что есть для Rich Presence.
2. Скопируй Client ID. Пока это поле пусто, плагин к Discord вообще не подключается.
3. Для голоса: в разделе OAuth2 скопируй Client Secret и добавь Redirect `http://127.0.0.1`.
4. Нажми «Авторизовать» на странице плагина и подтверди в окне Discord. Токены сохранятся, и следующий запуск обойдётся без окна.

Окно разрешения появляется только по кнопке или вызову `authorize`, сам плагин его не показывает.

Discord считает голосовое RPC закрытым: работать с ним могут только владелец приложения и его тестеры. Для своего приложения этого хватает.

Токены лежат в конфиге зашифрованными средствами Windows, а Client Secret там же открытым текстом.

## API

Адрес `cap:discord.client`, формат JSON.

| Метод | Запрос | Что делает |
|---|---|---|
| `discord.state` | нет | Всё сразу: связь, пользователь, мьют, канал, пинг |
| `voice.get` | нет | `{ muted, deafened, voiceState, pingMs, averagePingMs }` |
| `voice.mute` | `{ "value": true }`, без значения переключает | Микрофон |
| `voice.deafen` | `{ "value": true }`, без значения переключает | Звук |
| `channel.current` | нет | `{ id, name, guildId, guildName, members }` или `null` |
| `channel.join` | `{ "channelId": "..." }` | Зайти в голосовой канал |
| `channel.leave` | нет | Выйти из канала |
| `guilds.list` | нет | Серверы |
| `channels.list` | `{ "guildId": "..." }` | Каналы сервера |
| `presence.set` | Объект activity из документации Discord | Rich Presence, без авторизации |
| `presence.clear` | нет | Убрать Rich Presence |
| `authorize` | нет | Показать окно разрешения |
| `rpc.command` | `{ "cmd": "...", "args": {}, "evt": "..." }` | Любая команда Discord RPC |

| Событие | Тело |
|---|---|
| `discord.state.changed` | Как у `discord.state`, при любом изменении |
| `discord.event` | `{ name, data }`: любое событие Discord как есть |

Команды голоса требуют авторизации, иначе вернётся ошибка с понятным текстом.

```csharp
await bus.CallAsync("cap:discord.client", "voice.mute", HubPayload.Json(new { value = true }));
```

## Важно

Реальным Discord этот плагин ещё не проверялся. Протокол покрыт тестами на поддельном сервере.
