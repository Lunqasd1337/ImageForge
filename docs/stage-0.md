# Этап 0 — минимальная основа

Статус: в работе. Ветка `stage0-foundation`.

## Сделано

- Решение с проектами для M1: Core, Engine, Engine.Host, App, Core.Tests.
- SDK в `global.json` (10.0.100, rollForward latestFeature), пакеты централизованно, lock-файлы, транзитивное закрепление.
- Core, по TDD:
  - `WriteScope` — лексическая граница записи: нормализация Windows-путей, запрет `..` за корень, UNC/`\\?\`/`\\.\`, ADS, зарезервированных имён и сегментов с точкой/пробелом в конце, сравнение без учёта регистра без ловушки префикса.
  - `RegistryPathMapper` — `HKLM\SOFTWARE` → `Windows\System32\config\SOFTWARE`; SAM/SECURITY/HARDWARE/HKCC запрещены; SYSTEM/HKCU/HKU/HKCR — «ещё не поддерживается»; без перехода к реестру хоста.
  - `ValidationMarker.M1` — типизированная запись REG_SZ `HKLM\SOFTWARE\ImageForge\Validation\Milestone = M1`.
  - `PipelinePlanner` — план load → set → unload офлайн-куста, превью с физическим назначением, отпечаток SHA-256 без PlanId.
  - `PlanStartValidator` — запуск только показанного плана: PlanId, ожидаемая ревизия, актуальность, целостность.
  - `StartRequestLedger` — идемпотентный повтор старта по ClientRequestId.
  - `EngineLaunchArguments`, `ProtocolHandshake`, контракт `IEngineService`.
- Engine.Host: pipe со случайным именем, одним экземпляром, DACL (SID инициатора из токена UI-процесса + SYSTEM, запрет NETWORK); проверка PID и времени старта UI, удержание его handle; остановка при выходе UI или обрыве pipe; лог Serilog в `%ProgramData%\ImageForge\logs`.
- App: минимальное окно (источник, рабочая папка, превью операции M1, подключение к движку, статус, журнал). Кнопка запуска появится в M1.
- CI: чистые тесты на Linux, сборка всего решения на Windows.

## Проверено вне VM

- 113 тестов Core проходят (Linux, SDK 10.0.113).
- Engine и Engine.Host компилируются под net10.0-windows.
- C#-код App проверен компилятором отдельно; XAML-часть на Linux не собирается, её проверит Windows-сборка в CI.

## Не проверено и известные пробелы

- Реальный запуск через UAC, подключение по pipe и DACL на Windows — не запускались.
- Ограничение размера RPC-сообщений — сделано после этапа 0 (`BoundedMessageHandler`, см. `docs/m1.md`).
- Фикстура 25H2 Pro и её SHA-256 ещё не зафиксированы.

## Дальше (к M1)

1. ~~Ограничение размера RPC-сообщений и транспортный тест IProgress/CancellationToken.~~ Сделано.
2. Эксперименты 1–3 до M1: ManagedDism (инвентаризация, mount/unmount, export), UAC/pipe, загрузка SOFTWARE и запись маркера. Это ручные проверки в VM.
3. Workspace: base/runs/state.json/journal, проверка неизменности источника, публикация результата.
