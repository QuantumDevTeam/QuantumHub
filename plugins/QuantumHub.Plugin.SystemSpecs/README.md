# System Specs

Id: `qh.system`. Вид: **одна DLL**, манифест встроен как ресурс.

Характеристики компьютера и его текущая загрузка. Запускается по требованию: не занимает память, пока никому не нужен.

**Что показывает как пример:** плагин из одного файла, `"activation": "onDemand"` и возможности, которым неважно, кто их
предоставляет.

## API

| Адрес | Метод | Ответ |
|---|---|---|
| `cap:system.specs` | `specs.get` | `{ machine, os, cpu, threads, ramGb, gpus }` |
| `cap:system.stats` | `stats.get` | `{ cpuPercent, ramUsedGb, ramTotalGb, gpuPercent, vramUsedGb, vramTotalGb }` |

`gpuPercent` и `vramUsedGb` равны `null`, если в системе нет счётчиков GPU. Замеры кешируются на секунду.

Всё читается через WinAPI, реестр и счётчики PDH, без тяжёлого WMI.
