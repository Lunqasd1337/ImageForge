# Ручная проверка S2-DRIVERS-UI: драйверы в окне, в плане и в запуске

Статус: **Ожидает проверки**.
Проверяет драйверы в окне, в плане и в запуске (план, раздел 12).
- «Прочитать образ» показывает сторонние драйверы образа.
- «Найти пакеты» ищет INF в выбранной папке и проверяет каждый пакет, DISM при этом не вызывается.
- В плане пакеты с одинаковым содержимым идут одним добавлением. Пакет без каталога проходит только с ForceUnsigned, драйвер, нужный для загрузки, удаляется только с отдельным разрешением.
- Запуск сверяет список драйверов образа и хеш каждого пакета, затем удаляет и добавляет через DISM API и пишет в журнал новые имена `oemN.inf`.

Цель пока одна: образ 1 в `install.wim`. Загрузочные образы (boot.wim) и WinRE будут отдельно.

## Сборка

- Из одной сборки CI этого PR: `ImageForge.App-<sha>` и `ImageForge.Experiments-<sha>`.
- `C:\IF\app` удалить и распаковать туда новый `ImageForge.App`; `ImageForge.Experiments.exe` положить в `C:\IF`.

## Требования

Сервисная VM с контрольной точки `fixture-ready` ([Hyper-V](HYPERV.md)), фикстура `C:\IF\fixture\media`, свободно не меньше 40 ГБ, вход под администратором с UAC. ISO virtio-win подключён (см. [S2-DRIVERS-INVENTORY](S2-DRIVERS-INVENTORY.md)), ниже он на `E:`.

PowerShell от администратора. Команды готовят тестовую папку драйверов:
- `netkvm` и `netkvm-copy`: два одинаковых подписанных пакета;
- `viostor`: драйвер хранилища, нужный для загрузки;
- `nocat`: netkvm без каталога;
- `arm64`: пакет без x64.
```powershell
$E = "C:\IF\ImageForge.Experiments.exe"; $X = { & $E @args | Out-Host }; $D = "E:"
Start-Transcript C:\IF\s2-drivers-ui.txt
& $X version
& $X mounts
$P = "C:\IF\drv"; Remove-Item $P -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory $P\netkvm, $P\netkvm-copy, $P\viostor, $P\nocat, $P\arm64 -Force | Out-Null
Copy-Item $D\NetKVM\w11\amd64\* $P\netkvm; Copy-Item $D\NetKVM\w11\amd64\* $P\netkvm-copy; Copy-Item $D\NetKVM\w11\amd64\* $P\nocat
Copy-Item $D\viostor\w11\amd64\* $P\viostor; Copy-Item $D\NetKVM\w11\ARM64\* $P\arm64
Get-ChildItem $P -Recurse -File | ForEach-Object { $_.IsReadOnly = $false }
Remove-Item $P\nocat\*.cat -Force
(Get-Content $P\nocat\netkvm.inf) | Where-Object { $_ -notmatch '^\s*CatalogFile' } | Set-Content $P\nocat\netkvm.inf
& $X driver-scan $P
```
Ожидается: `mounts` → `none`; `driver-scan` находит 5 INF. У `arm64\netkvm.inf` есть `PROBLEM: … нет раздела для x64`, у `nocat\netkvm.inf` — `catalog (none: UNSIGNED)`, а `netkvm` и `netkvm-copy` попадают в одну строку `SAME INF CONTENT`.

## Шаги

### 1. Подключение и чтение образа

Запустить `C:\IF\app\ImageForge.App.exe`, «Подключить движок», в UAC — «Да». Источник `C:\IF\fixture\media`, рабочая папка `C:\IF\ws-drv`, папка результата `C:\IF\result-drv`. «Прочитать образ».
Ожидается: в журнале `протокол 7`; в разделе «Драйверы» — «Сторонних драйверов в образе нет.».

### 2. Поиск пакетов

«Папка драйверов» — кнопкой «Выбрать...» выбрать `C:\IF\drv` (или ввести путь), «Найти пакеты».
Ожидается:
- «Найдено INF: 5, можно добавить: 4, отмечено: 0»;
- в «Найденные пакеты» у `arm64\netkvm.inf` нельзя поставить отметку, предупреждение «В [Manufacturer] нет раздела для x64»;
- у `nocat\netkvm.inf` во второй строке «без подписи», предупреждение про ForceUnsigned;
- у `netkvm\netkvm.inf` и `netkvm-copy\netkvm.inf` предупреждение «Такое же содержимое ещё у 1 INF…»;
- у `viostor\viostor.inf` класс `SCSIAdapter`.

Снимок окна со списком.

### 3. Отказ без ForceUnsigned

Отметить `nocat\netkvm.inf`, «Разрешить пакеты без подписи» не ставить, «Подготовить».
Ожидается: «План не построен…», в превью «У пакета nocat\netkvm.inf нет каталога (подписи); добавить его можно только с ForceUnsigned.».

### 4. Превью

Отметить ещё `netkvm\netkvm.inf`, `netkvm-copy\netkvm.inf` и `viostor\viostor.inf`, поставить «Разрешить пакеты без подписи», «Подготовить».
Ожидается:
- статус «План готов… Удаляемых драйверов: 0, добавляемых: 3. …»;
- в превью строки «Добавить драйвер netkvm-copy\netkvm.inf: Net, Red Hat, Inc., 100.103.104.30200» (из двух одинаковых остаётся первый по списку), затем «То же содержимое, добавляется один раз: netkvm\netkvm.inf», «Добавить драйвер nocat\netkvm.inf … (без подписи, ForceUnsigned)», «Добавить драйвер viostor\viostor.inf: SCSIAdapter, …»;
- «Источник: C:\IF\drv» и «Внимание: Пакет nocat\netkvm.inf без подписи добавляется с ForceUnsigned…».

Снимок окна с превью.

### 5. Запуск

«Запустить», окно не закрывать.
Ожидается в журнале:
- шаг `drivers...`;
- `add driver netkvm-copy\netkvm.inf`, `add driver nocat\netkvm.inf (ForceUnsigned)`, `add driver viostor\viostor.inf`, у каждого строка `DismAddDriver …`;
- после каждого добавления только его имя: `driver netkvm-copy\netkvm.inf -> oemN.inf (Signed)`, `driver nocat\netkvm.inf -> oemN.inf (Unsigned)`, `driver viostor\viostor.inf -> oemN.inf (Signed)`;
- «Готово. Результат сохранён.», `Итог: None Published …`.

Перепишите имена `oemN.inf`.

### 6. Удаление из нового результата

Источник поменять на `C:\IF\result-drv`, рабочая папка `C:\IF\ws-drv2`, результат `C:\IF\result-drv2`. «Прочитать образ».
Ожидается: «Сторонних драйверов: 3, нужных для загрузки: 1». В «Сторонние драйверы образа» три строки, у viostor предупреждение «Нужен для загрузки…».

Отметить viostor, «Разрешить удаление драйверов, нужных для загрузки» не ставить, «Подготовить».
Ожидается: «План не построен…», в превью «Драйвер oemN.inf (viostor.inf) нужен для загрузки; удалить его можно только с отдельным подтверждением.».

Отметить ещё неподписанный netkvm (тот, где `Unsigned`), поставить «Разрешить удаление…», «Подготовить».
Ожидается: в превью «Удалить драйвер oemN.inf: viostor.inf, SCSIAdapter, … (нужен для загрузки)», «Удалить драйвер oemN.inf: netkvm.inf …» и «Внимание: Удаляется драйвер, нужный для загрузки…». «Запустить».
Ожидается в журнале: `remove driver oemN.inf: viostor.inf … (boot-critical)`, `DismRemoveDriver oemN.inf` для обоих, «Готово. Результат сохранён.».

### 7. Сверка результатов

```powershell
New-Item -ItemType Directory C:\IF\mount -Force | Out-Null
& $X mount C:\IF\result-drv\sources\install.wim 1 C:\IF\mount
& $X driver-list C:\IF\mount third-party
& $X unmount C:\IF\mount discard
& $X mount C:\IF\result-drv2\sources\install.wim 1 C:\IF\mount
& $X driver-list C:\IF\mount third-party
& $X unmount C:\IF\mount discard
& $X mounts
& $X host-check
Stop-Transcript
```
Ожидается:
- в `result-drv` три сторонних драйвера: netkvm `Signed`, netkvm `Unsigned` и viostor `Signed | BOOT-CRITICAL`;
- в `result-drv2` только подписанный netkvm;
- `mounts` → `none`, `host-check` → `RESULT: OK`.

### 8. Журнал движка

В `engine-*.log` есть строки:
- `inspection revision …: … third-party drivers`;
- `plan add driver nocat\netkvm.inf from C:\IF\drv (ForceUnsigned allowed)`;
- `plan warning: …`;
- для второго запуска `plan remove driver … (boot-critical allowed)`.

## Что прислать

- файл `C:\IF\s2-drivers-ui.txt`;
- снимки окна для шагов 2 и 4;
- журнал окна после шагов 5 и 6;
- строки журнала движка из шага 8.

## Результаты

| BuildId | VM | Шаги | Итог |
|---|---|---|---|
| 3f7b4b71 | Hyper-V | 0–5 | Пройдены. Протокол 7; в образе фикстуры сторонних драйверов нет (чтение 2 с). Поиск: 5 INF, 4 можно добавить, arm64 без x64, nocat без подписи, оба netkvm — одно содержимое. Без ForceUnsigned — отказ «нет каталога». Превью: netkvm-copy с «То же содержимое: netkvm», nocat с ForceUnsigned и предупреждением, viostor. Запуск добавил три пакета (oem0 Signed, oem1 Unsigned, oem2 viostor Signed) и опубликовал результат. Найдено: строки журнала обоих netkvm показывали оба новых имени (сопоставление по имени INF); исправлено — список читается после каждого добавления. |
| 3f7b4b71 | Hyper-V | 6 | Пройден. Чтение result-drv: три сторонних драйвера. Удаление viostor без подтверждения — отказ «…нужен для загрузки…». С подтверждением удалены oem1 (netkvm Unsigned) и oem2 (viostor, boot-critical), после удаления в образе один драйвер; результат опубликован в result-drv2. Найдено: отметки «Найденных пакетов» сохраняются при смене образа, и в план попадает прежний пакет; превью при этом предупреждает «В образе уже есть oem2.inf…». |
| 3f7b4b71 | Hyper-V | 7 | Пройден. result-drv: oem0 netkvm Signed, oem1 netkvm Unsigned, oem2 viostor Signed | BOOT-CRITICAL (чтение 2,3 с). result-drv2: только oem0 netkvm Signed. `mounts` → none, `host-check` → OK. |
