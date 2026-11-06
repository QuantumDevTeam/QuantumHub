# Клиент на Python

Один файл без зависимостей: ни .NET, ни библиотеки protobuf. Несколько строк кодируют protobuf вручную.

Этот пример доказывает, что протокол не привязан к .NET. Плагин для VS Code или Rider устроен так же, только там
вместо ручного кодирования будет библиотека protobuf.

```bash
python hub_client.py
```

Адрес хаба читается из `%APPDATA%\QuantumHub\endpoint.json`. Можно передать другой файл и текст:

```bash
python hub_client.py path/to/endpoint.json "привет"
```
