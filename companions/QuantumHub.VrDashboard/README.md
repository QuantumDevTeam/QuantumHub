# QuantumHub.VrDashboard

Панель в меню SteamVR, внешняя часть плагина [SteamVR Dashboard](../../plugins/QuantumHub.Plugin.VrDashboard/README.md).
Клиент хаба с id `qh.vr.dashboard.overlay`.

| Файл | Что внутри |
|---|---|
| `PanelLayout.cs` | Раскладка и попадание по кнопкам. Без VR, поэтому проверяется тестами |
| `PanelClient.cs` | Связь с плагинами через хаб |
| `PanelRenderer.cs` | Рисует панель в картинку на WPF |
| `OverlayHost.cs` | Вкладка SteamVR: картинка, луч контроллера, клавиатура |

Посмотреть панель без шлема:

```bash
QuantumHub.VrDashboard.exe --preview panel.png --language ru
```

Ошибки пишутся в `%APPDATA%\QuantumHub\logs\vr-dashboard.log`.
