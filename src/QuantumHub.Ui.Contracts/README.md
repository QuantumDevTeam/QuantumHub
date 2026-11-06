# QuantumHub.Ui.Contracts

Контракт нативного интерфейса плагина: `INativePluginPage` и `IPluginUiHost`.

Вынесен отдельно от `QuantumHub.Contracts`, потому что зависит от WinUI, а обычным плагинам и хабу она не нужна.

На этот проект ссылается только UI-часть плагина, с `Private=false`: сборку даёт GUI. Пример —
`QuantumHub.Plugin.Activity.Ui`.
