# User Activity

Id: `qh.activity`. Вид: папка с двумя DLL, сервис и [UI](../QuantumHub.Plugin.Activity.Ui/README.md). Предоставляет
возможность `user.activity`.

Мини-плагин, который знает, чем занят пользователь. Сам ничего не делает с этим знанием: им пользуются другие плагины.

## API

Адрес `cap:user.activity`, формат JSON.

| Метод | Запрос | Ответ |
|---|---|---|
| `window.current` | нет | `{ process, title, processId, unixMs }` |
| `idle.get` | нет | `{ idleSeconds, isIdle }` |
| `vr.get` | нет | `{ active }` |
| `process.list` | `{ "names": ["VRChat"] }` | Те из них, что запущены |
| `process.watch` | `{ "names": ["VRChat"] }` | Начать следить, события приходят ниже |

| Событие | Тело |
|---|---|
| `activity.window.changed` | Как у `window.current` |
| `activity.process.changed` | `{ name, running }` |
| `activity.idle.changed` | Как у `idle.get` |
| `activity.vr.changed` | Как у `vr.get` |

Наблюдение живёт, пока работает плагин. Если клиент подключается заново, он повторяет `process.watch`.

## Как это работает

- Смену окна сообщает сама Windows через `SetWinEventHook`, без опроса.
- Простой считается через `GetLastInputInfo`. Глобальных хуков клавиатуры нет, поэтому ввод плагин не видит.
- Процессы проверяются раз в две секунды и только те, за которыми попросили следить.
- VR определяется по процессам из настройки, по умолчанию SteamVR. Другой рантайм добавляется одной строкой.

Настройка `includeTitle` отключает передачу заголовков: в них бывает личное.
