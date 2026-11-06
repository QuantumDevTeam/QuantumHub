# QuantumHub.VrDashboard

Панель в меню SteamVR, внешняя часть плагина [SteamVR Dashboard](../../plugins/QuantumHub.Plugin.VrDashboard/README.md).
Клиент хаба с id `qh.vr.dashboard.overlay`.

| Файл | Что внутри |
|---|---|
| `PanelLayout.cs` | Раскладка и попадание по кнопкам. Без VR, поэтому проверяется тестами |
| `PanelClient.cs` | Связь с плагинами через хаб |
| `PanelRenderer.cs` | Рисует панель в картинку на WPF |
| `OverlayHost.cs` | Логика вкладки: что показать и что сделать по нажатию. Проверяется тестами на поддельном SteamVR |
| `SteamVrRuntime.cs` | Вызовы OpenVR, больше ничего |

Посмотреть панель без шлема:

```bash
QuantumHub.VrDashboard.exe --preview panel.png --language ru
```

Проверить, что библиотека OpenVR загружается, не запуская SteamVR:

```bash
QuantumHub.VrDashboard.exe --check result.txt
```

Ошибки и координаты каждого нажатия пишутся в `%APPDATA%\QuantumHub\logs\vr-dashboard.log`.
