# scripts

## publish.ps1

Собирает портативную установку в `artifacts/QuantumHub`: хаб, GUI, CLI, плагины, пример и `.qhp`-архивы, вместе с
лицензией и уведомлениями. Папку можно положить куда угодно.

```bash
powershell -ExecutionPolicy Bypass -File scripts/publish.ps1
```

`-NoGui` пропускает GUI, `-Output` меняет папку, `-Configuration` меняет конфигурацию.

## docs.ps1

Генерирует сайт документации в `artifacts/docs` из комментариев в коде и статей в `docs`. Используется DocFX,
он ставится локально для репозитория через `dotnet tool restore`.

```bash
powershell -ExecutionPolicy Bypass -File scripts/docs.ps1 -Serve
```

С `-Serve` сайт откроется на `http://localhost:8080`. Открыть `index.html` просто как файл не получится: поиск и
навигации нужен веб-сервер.
