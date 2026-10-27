# ImageForge

Десктопное приложение (C# / .NET 10, WinUI 3) для редактирования офлайн-образов Windows 11 x64.
Полный план: спецификация ImageForge v1 (версия 2.2). Текущий этап: **0 — минимальная основа**, цель — рубеж **M1**.

## Структура (этап 0)

| Проект | TFM | Назначение |
|---|---|---|
| `src/ImageForge.Core` | net10.0 | Чистая логика: граница записи, маппинг реестра, планировщик, RPC-контракты. Без P/Invoke, сети и WinUI. |
| `src/ImageForge.Engine` | net10.0-windows | Безопасность pipe, проверка UI-процесса, реализация RPC. Далее: DISM, кусты, workspace. |
| `src/ImageForge.Engine.Host` | net10.0-windows | Повышенный exe (requireAdministrator), Generic Host, Serilog, StreamJsonRpc по named pipe. |
| `src/ImageForge.App` | net10.0-windows10.0.26100.0 | WinUI 3, asInvoker. Запускает движок через `runas`, общается только по RPC. |
| `tests/ImageForge.Core.Tests` | net10.0 | xunit.v3 + Shouldly, только чистая логика. |

Catalog, Distribution и установщик появятся после M1.

## Сборка

```powershell
dotnet restore ImageForge.slnx
dotnet build ImageForge.slnx
dotnet test --project tests/ImageForge.Core.Tests
```

SDK закреплён в `global.json`, версии пакетов — в `Directory.Packages.props`, lock-файлы обязательны в CI.
Приложение (WinUI) собирается только на Windows. Движок кладётся рядом с приложением в папку `engine\`.

## Проверки

- CI выполняет только чистые тесты и сборку.
- Всё, что касается DISM, кустов реестра, UAC, установки Windows и WinRE, проверяет вручную владелец проекта в изолированной Hyper-V VM по пакетам испытаний в `docs/`.
