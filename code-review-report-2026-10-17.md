# Ревью кода после рефакторинга runtime

**Дата:** 2026-10-17
**Объект:** рабочее дерево (незакоммиченный рефакторинг runtime) относительно `HEAD 3297bf3` (опубликованная v0.9.4).
**Метод:** статическое чтение кода без сборки и запуска; 13 параллельных направлений по подсистемам + сквозной срез. Каждое направление проверяло свои находки по коду и вызывающим местам. Отдельные выводы подкреплены изолированными расчётами: пересчёт фикстур на Python, проверка G17 на CLR 4.8, разбор IL Google.Apis.Auth. Повторной независимой верификации каждой находки не было; находки, подтверждённые несколькими направлениями, отмечены.
**Статус:** итоговый. Завершены все 13 направлений и сквозной срез. Дубликаты между направлениями объединены — в тексте указано, что с чем объединено. Идентификаторы находок сохранены с промежуточных версий; изменилась только серьёзность U-04 и U-05 (повышены до Высокой по совокупности подтверждений).
**Ничего не исправлялось.** Предыдущий отчёт `code-review-report.md` (2026-08-29) не изменён.

Шкала серьёзности: **Критично** — потеря/порча данных, падение или отказ сервиса, дыра безопасности; **Высокая** — ошибка в реалистичном сценарии, сломанный путь восстановления, неограниченный рост, существенная ложная уверенность тестов; **Средняя** — краевой случай, вводящий в заблуждение статус, рискованный дизайн с конкретными последствиями; **Низкая** — мелочи, мёртвый код, «запахи».
Достоверность: **Подтверждено** (по коду) / **Вероятно** / **Гипотеза**.
Префиксы находок:

| Префикс | Раздел |
|---|---|
| S | сквозные |
| F | загрузка и сервер |
| P | дефекты production, найденные через тесты |
| T | тесты |
| R | реплей и графики |
| C | координаторы записи |
| E | форматы Core и геометрия |
| U | WPF-оболочка |
| A | runtime и IPC |
| B | durable-хранение |
| N | нативные источники |
| M | численный анализ |
| G | Google Drive |
| H | launcher, updater, CI |
| K | история и архивы |

## Сводка на текущий момент

| Раздел | Критично | Высокая | Средняя | Низкая | К проверке |
|---|---|---|---|---|---|
| 1. Сквозные (архитектура, сборка, репозиторий) | — | — | 5 | 1 | 1 |
| 2. Загрузка телеметрии и сервер | 1 | — | 2 | 6 | — |
| 3. Дефекты production, выявленные при анализе тестов | — | — | 2 | — | — |
| 4. Тесты и инструменты | — | 3 | 6 | 4 | — |
| 5. Контролы реплея и графиков | — | 2 | 5 | 8 | — |
| 6. Координаторы записи | — | 2 | 6 | 6 | — |
| 7. Форматы данных Core и геометрия самолёта | — | 2 | 4 | 4 | — |
| 8. WPF-оболочка и настройки | — | 3 | 5 | 7 | — |
| 9. Композиция runtime, supervisor, IPC | — | 1 | 3 | 7 | — |
| 10. Durable-хранение: журнал, эпизоды, outbox, квота | 1 | 6 | 4 | 5 | — |
| 11. Нативные источники (SimConnect, WinMM) | — | — | 1 | 5 | — |
| 12. Численный анализ касания | — | — | 1 | 7 | — |
| 13. Google Drive | — | 2 | 7 | 7 | — |
| 14. Launcher, guardian, updater, сборка и CI | — | 4 | 6 | 8 | — |
| 15. Локальная история и архивы | — | — | 4 | 5 | — |
| **Итого (169 находок)** | **2** | **25** | **61** | **80** | **1** |

Самое важное — что исправлять в первую очередь:
1. **F-01** — сервер приёма: один небольшой архив может исчерпать память контейнера. В опубликованной версии проверки метода сжатия нет вовсе, а новая проверка обходится.
2. **B-01** — журнал записи навсегда становится неоткрываемым после штатного восстановления оборванного хвоста и следующей ротации. Блокируются запись, cold recovery и закрытие приложения.
3. **A-01** — переполнение окна неподтверждённых сообщений (ACK только после fsync) роняет процесс источника вместо записи gap-маркера. Заход превращается в `approach_interrupted`, а затор даёт цикл рестартов SimConnect.
4. **B-02, C-01, C-02, C-05** — пути восстановления записи, которые ломаются навсегда:
   - cold recovery и живой путь дают разный `InputHash`;
   - устаревший checkpoint остаётся после restore;
   - NaN-координаты и любое исключение в редьюсере «отравляют» журнал, quarantine нет.
5. **B-03, B-08, K-02** — данные runtime растут без ограничения внутри квоты 8 GiB: эпизоды, сессии, полная копия кеша аэропортов в каждом эпизоде. Все файлы опрашиваются каждую секунду. Когда квота заполнится, запись остановится, а способа освободить место нет.
6. **B-04, B-05, B-07** — «ядовитые» элементы:
   - одна битая запись outbox останавливает все загрузки;
   - один проблемный эпизод роняет владельца по кругу;
   - полная очередь автоархивов навсегда держит эпизоды.
7. **E-01** — при кратком отказе опроса контроллера в CSV попадает `NaN`, и сервер отклоняет весь архив посадки.
8. **E-02** — история нового формата нечитаема для 0.9.4: при откате и на втором ПК через Drive. Проверка отката этого не покрыла.
9. **U-04, H-03** — сбой старта runtime (повреждённый engine, облачные метаданные, полный диск) отключает запись, настройки и самообновление. Guardian при этом молча перезапускает приложение бесконечно.
10. **H-01, H-02, H-04** — обновление и guardian:
    - сбой updater после commit оставляет приложение закрытым;
    - commit может потеряться;
    - после сна или гибернации guardian убивает приложение как зависшее.
11. **U-01, R-02, U-05** — UI:
    - после рестарта cloud-owner история перезагружается на каждом снимке runtime, и каждый раз сбрасывается реконструкция;
    - ожидающая синхронизация навсегда блокирует Drive после Disconnect.
12. **G-01, G-02** — Google Drive:
    - долгая локальная фаза синхронизации эскалируется в перезапуск всего приложения;
    - одна «плохая» посадка навсегда обрывает синхронизацию.
13. **U-02, U-03** — завершение сеанса Windows и таймаут подтверждения guardian обходят drain локальных записей.
14. **T-01, T-02, T-03** — страховочная сеть:
    - тесты протокола IPC не запускаются в CI;
    - тестовый фасад скрыл регрессию P-01;
    - связка runtime ↔ guardian и production-параметры восстановления тестами не покрыты.

Расчёты посадок: падений анализа и ошибок в первичных метриках (`InertialVerticalFpm`, `LatchedNormalFpm`, `PeakG`, `Impact`) не найдено, рефакторинг путь вычислений не изменил. Единственная содержательная численная ошибка — знак в datum-калибровке v1 (M-01); в текущих фикстурах её эффект меньше 1 fpm.

---

## 1. Сквозные архитектурные наблюдения

### S-01. Воркеры загружают WPF-EXE без его binding redirects
- **Серьёзность:** Средняя · **Категория:** Архитектура / Сборка · **Достоверность:** расхождение конфигураций подтверждено; отказ в runtime — гипотеза
- **Где:** `src/LandingStats.WorkerHost/Program.cs:87`; `src/LandingStats.App/LandingStats.App.csproj` (target `CopyWorkerHost`); сгенерированные `MSFS-Landing-Stats.exe.config` и `LandingStats.WorkerHost.exe.config`
- **Описание:** все роли (simconnect, controllers, upload, cloud, analysis) исполняются внутри `LandingStats.WorkerHost.exe`, который делает `Assembly.LoadFrom("MSFS-Landing-Stats.exe")`. Конфигурацией AppDomain при этом служит `LandingStats.WorkerHost.exe.config`, в котором нет ни одного binding redirect. У самого приложения в `MSFS-Landing-Stats.exe.config` есть redirect `System.Runtime.CompilerServices.Unsafe 0.0.0.0–6.0.1.0 → 6.0.1.0`, нужный цепочке ZstdSharp / System.Memory / System.Threading.Tasks.Extensions. В воркерах он не действует.
- **Сценарий:** код роли затрагивает сборку, ссылающуюся на младшую версию `Unsafe` (например, upload с Zstd-контейнером или cloud с зависимостями Google.Apis). Это даёт `FileLoadException` в процессе воркера, роль падает, supervisor перезапускает её с backoff, и компонент «вечно восстанавливается». По плану изолированный codec job проходил, то есть как минимум один путь работает. Остальные пути не доказаны: процессные тесты в основном гоняют bootstrap-фикстуры, которые `MSFS-Landing-Stats.exe` не загружают (см. S-04).
- **Направление:** генерировать для WorkerHost тот же набор redirects (или общий config) и проверять загрузку каждой реальной роли в упакованном runtime.
- **Уточнено направлениями launcher и нативных источников:** ролям simulator и controllers redirect не нужен. Под риском роли, в которых связывается ссылка на `System.Runtime.CompilerServices.Unsafe` — через ZstdSharp или `System.Threading.Tasks.Extensions`.

### S-02. Runtime и все роли воркеров живут в сборке WPF-приложения
- **Серьёзность:** Средняя · **Категория:** Архитектура · **Достоверность:** Подтверждено
- **Где:** `src/LandingStats.App/Runtime/` (~70 файлов, ~10 тыс. строк), `src/LandingStats.App/Runtime/WorkerRoleFactory.cs`, `src/LandingStats.WorkerHost/Program.cs:85-91`
- **Описание:** по плану (раздел 3) `Runtime` должен был стать отдельным модулем без WPF/SDK, а WorkerHost — минимальным bootstrap, который инициализирует только модуль своей роли. Фактически supervisor, журнал, outbox, координаторы и все `*WorkerRole` скомпилированы в `MSFS-Landing-Stats.exe`, и каждый воркер загружает этот EXE целиком. Изоляция зависимостей держится только на ленивой загрузке сборок CLR. «Сохранённый движок» анализа (retained engine) фактически совпадает со всем UI-приложением.
- **Последствия:** сильная связность UI ↔ runtime. Любое изменение UI меняет хеш движка анализа. Если роль случайно затронет тип с WPF/SimConnect-зависимостью или его статический инициализатор, эти зависимости попадут в воркер. Тестировать runtime отдельно от WPF сложнее. Это уже происходит: `Models/LandingReplayDiagnostics` зависит от `Controls`, и ради диагностики высот и шасси воркер анализа строит WPF-объекты (`ReplayLandingGear`, меши, `DrawingBrush` номеров ВПП) на MTA-потоках пула (см. R-15).
- **Направление:** выделить `LandingStats.Runtime` (без WPF) и отдельные сборки ролей, загружать в воркер только их.

### S-03. Исходники в `LandingStats.Core` исключены `.gitignore`, но компилируются локально; инструменты устарели
- **Серьёзность:** Средняя · **Категория:** Сборка / Сопровождаемость · **Достоверность:** Подтверждено
- **Где:** `.gitignore:16-18, 25-27` — `src/LandingStats.Core/ContactPointAnalysis.cs`, `ContactPointResult.cs`, `TelemetryDownsampler.cs`; проекты `src/LandingStats.Analyzer/`, `src/LandingStats.Probe/`, `src/LandingStats.Probe.Launcher/`, `MsfsLandingStats.sln`
- **Описание:**
  - Три файла физически лежат в папке проекта Core, и SDK-проект включает их в компиляцию по glob. В git их нет, поэтому `LandingStats.Core.dll` на машине разработчика отличается от сборки CI или чистого checkout. Используются они только игнорируемым Analyzer. `TelemetryDownsampler` в production не используется вовсе (LS-007 реализован в `LandingRecordFactory`). Кроме того, он не сохраняет кадры вокруг смены `OnGround` и зацикливается при |t| ≳ 5,6·10¹⁴ с: `target += 0.05` перестаёт менять значение. В `ContactPointAnalysis` тоже есть ошибки: `FindSequence` берёт первое совпадение по `Sequence` (при дублях находит не тот контакт), а пики инициализируются −∞ и так и публикуются.
  - Инструменты исключены из репозитория и устарели:
    - Probe не компилируется с v0.5.0: использует около 30 удалённых свойств `TelemetrySample`, у него собственная устаревшая раскладка `SimFrameData`. Поэтому `MsfsLandingStats.sln` не собирается.
    - Конвейер Analyzer не делает `SplitPhysicalSegments` и не передаёт geometry options (в отличие от `EpisodeProcessor.cs:53-78`). Его `SelfTests` исключены из компиляции.
    - Probe.Launcher дублирует извлечение App.Launcher, но без `BundlePayload`, staging и проверки длины. Пишет он в `%LOCALAPPDATA%\…\Runtime\<hash>` — общий корень с новым runtime.
- **Последствия:**
  - Локальные корпусные сравнения и хеши движков сделаны на другой сборке, чем поставляемая.
  - Если tracked-код начнёт ссылаться на эти типы, локально всё соберётся, а CI упадёт.
  - Результаты Analyzer не совпадают с production-конвейером.
- **Направление:** перенести исследовательский код в Analyzer или исключить его через `<Compile Remove>`; инструменты обновить или удалить.

### S-04. Тестовые фикстуры и test-only API в production-сборках; мёртвый код в контрактах
- **Серьёзность:** Средняя · **Категория:** Архитектура · **Достоверность:** Подтверждено (активация в production-путях не обнаружена)
- **Где:** `src/LandingStats.WorkerHost/Program.cs:24, 61, 82, 110-159` (режимы `crash` → `Environment.Exit(73)`, `hang`, `descendant`, `source-flood`, `landing`, `legacy-quota`; `--fixture-child` с бесконечным sleep); `src/LandingStats.App/Runtime/WorkerRoleFactory.cs:38-85` (публичный `CreateFixture` с поддельными HTTP и OAuth, сохраняющий токены); `src/LandingStats.App/Runtime/CloudApiFixture.cs`, `CloudSyncFixture.cs`; параметр `fixture` в `LandingRuntime.cs:44` и `ProcessWorkerHost.cs:78`; `src/LandingStats.App/GoogleDrive/GoogleDriveBackupService.cs:126` (`CancelLandingDeletionAsync` вызывается только из тестов); `src/LandingStats.Runtime.Contracts/OrderedSourceIngress.cs` (в production не используется)
- **Описание:**
  - Тестовое поведение входит в подписываемые бинарники и в allowlist пакета: искусственные падения, зависания, порождение потомков, фальшивые посадки и поддельные облачные/HTTP-ответы. Тестовые режимы идут тем же путём reflection (`CreateFixture`), что и боевые роли.
  - `OrderedSourceIngress` тестируется, хотя в production не используется вовсе — это мёртвый код.
  - При режиме `normal` процессные тесты не загружают `MSFS-Landing-Stats.exe`. Поэтому проверка «SimConnect.dll не загружен» для всех ролей пустая.
- **Направление:** вынести fixtures в тестовую сборку или включать их условной компиляцией; удалить неиспользуемые типы.
- **Дополнительно (направления runtime и Google Drive):**
  - Fencing ingress реализован заново в трёх местах, с разными проверками.
  - В `RuntimeComponentCatalog` поля `Boundary`, `Checkpoint` и `Name` читают только тесты. Компоненты ControllerJoiner, Diagnostics, Supervision и Application не подключены, а имена не совпадают с именами supervisor'ов (например, «commands» отображён на Facilities).
  - При ошибочной конфигурации cloud-fixture бэкап «успешно» уйдёт в локальный `fixture-server.json`, а режим `cloud-invalid` стирает токен.

### S-05. Битые абсолютные ссылки в документации
- **Серьёзность:** Низкая · **Категория:** Сопровождаемость · **Достоверность:** Подтверждено
- **Где:** `docs/runtime-refactoring-plan.md`, раздел 12; `BACKLOG.md`, раздел «План рефакторинга runtime»
- **Описание:** ссылки ведут на `D:/Users/ezaytsev/Documents/...`, а репозиторий лежит в `D:\Users\azzazel\...`. Часть ссылок указывает на удалённый `SimConnectLandingRecorder.cs`.
- **Направление:** перейти на относительные ссылки.

### S-06. Частные данные и посторонние файлы в рабочем дереве без `.gitignore`
- **Серьёзность:** Средняя · **Категория:** Гигиена репозитория / Приватность · **Достоверность:** содержимое и статус в git подтверждены; происхождение части каталогов — вероятно
- **Где:** корень репозитория: `t/` (≈251 МБ), `output/`, `AirVirtuaTracker-1.17.1-win-x64.zip` (43 МБ, сторонний бинарник); `tests/LandingStats.App.RegressionTests/StartupHistoryProbe.cs:28-33`
- **Описание:** `t/rp-*` и `t/startup-*` содержат копии реальной истории пользователя: около 110 посадок с координатами (`Landings/*.landing.json.gz`) и `airports.gz`. Там же остатки упавших runtime-тестов и временные файлы MSBuild/NuGet — похоже, TEMP был перенаправлен в репозиторий. Ни один из этих путей не отслеживается и не игнорируется, поэтому обычный `git add -A` перед коммитом рефакторинга захватит частные данные и чужой 43-мегабайтный архив, что создаёт ещё и лицензионный риск.
- **Направление:** удалить или добавить в `.gitignore` до коммита; вынести временные каталоги тестов за пределы репозитория.
- Подтверждено также направлением launcher и CI.

### S-07. Кандидаты из статического среза (ожидают подтверждения профильными направлениями)
- **Категория:** К проверке
- **`async void` (14):** `App.xaml.cs:20`; `MainWindow.History.cs:332`; `MainWindow.Shutdown.cs:19`; `MainWindow.TelemetryReports.cs:31`; `MainWindow.xaml.cs:480, 646, 700, 719, 735, 935, 1002, 1018, 1118, 1749`. У каждого нужен полный `try/catch`, иначе необработанное исключение роняет приложение.
- **Синхронные ожидания (22), самые подозрительные:**
  - `Runtime/RecordingRuntimeState.cs:197` и `Runtime/RecordingRuntimeOwner.cs:195` — блокирующий `GetResult()` в редьюсере и владельце записи;
  - `Telemetry/FlightModelGeometryResolver.cs:263, 307`;
  - `TelemetryUpload/TelemetryUploadClient.cs:507` — Dispose ждёт до 5 с;
  - `App.Guardian.cs:81` — ожидание при завершении сессии ОС;
  - `Telemetry/EpisodeFacilityCoordinator.cs:212` и `.Checkpoint.cs:30` — `Task.Result`.
- **Пустые `catch` (10):** `Runtime/ComponentSupervisor.cs:199, 257`; `Runtime/EpisodeWorkCoordinator.cs:61` (молча теряется сбой записи самой ошибки эпизода); `Runtime/IsolatedGoogleDriveApi.cs:196, 206`; `Telemetry/SimConnectSource.cs:991`; `ProcessControl/GuardianClient.cs:51`; `ProcessControl/GuardianGeneration.cs:137, 220`; `WorkerHost/Program.cs:38`.
- **Очистка каталогов launcher** (`src/LandingStats.App.Launcher/Program.cs:336-476`, `Directory.Delete` в `%LOCALAPPDATA%\MSFS Landing Stats\Runtime`): в том же корне теперь лежат durable-данные runtime, а тестов на эту очистку нет. Нужно проверить, что она не может удалить данные runtime (направление launcher).
- **Статус проверки (итог):**
  - `.Result` в `MainWindow.History.cs` безопасен: вызывается после `await`.
  - Блокирующие `GetResult()` в `RecordingRuntimeState`, `RecordingRuntimeOwner` и `EpisodeFacilityCoordinator` профильные направления дефектами не признали: все `TaskCompletionSource` завершаются на каждом пути.
  - Подтверждённые проблемы: `OnWindowClosing` (U-03); `OnDispatcherUnhandledException` не ставит `Handled` (U-11); `App.Guardian.cs:81` блокирует UI до 5 с при завершении сеанса (U-02).
  - Пустой catch в `EpisodeWorkCoordinator.cs:61` вошёл в B-16.
  - Очистка каталогов launcher: удаление durable-данных runtime не подтверждено, но найдены другие дефекты очистки (H-14).

---

## 2. Загрузка телеметрии: клиент и сервер приёма

### F-01. Ограничение методов сжатия ZIP на сервере не применяется к фактически читаемым записям — риск исчерпания памяти
- **Серьёзность:** Критично · **Категория:** Безопасность (DoS) · **Достоверность:** Подтверждено (по коду; агент дополнительно подтвердил изолированной локальной проверкой)
- **Где:** `server/telemetry-ingest/app/capture_archive.py:29-67` (`_directory_bound`, проверка метода на `:53`), `:181-187` (после неё архив заново разбирает `zipfile.ZipFile`; `compress_type` записей нигде не проверяется); `server/telemetry-ingest/app/validation.py:384-402`; `server/telemetry-ingest/app/dashboard_index.py:251`
- **Описание:**
  - Allowlist методов {stored, deflate} применяется только к результату собственного разбора центрального каталога в `_directory_bound`.
  - Читает архив независимый парсер `zipfile`, который учитывает в том числе структуры ZIP64. Если два разбора расходятся, в обработку попадают записи с другими методами сжатия, и для них `zipfile` не ограничивает объём, распаковываемый за один вызов.
  - Проверка коэффициента сжатия (25:1) опирается на объявленные клиентом размеры и выполняется только для `format 1`; `_Verified` замечает превышение уже после выделения памяти.
  - Контейнер ingress ограничен `mem_limit 256m`.
  - **В опубликованном серверном коде (HEAD) проверки метода сжатия нет вообще** — `zipfile.ZipFile` открывается напрямую. Риск касается и развёрнутой версии, а не только нового кода P10.
- **Сценарий:** любая самостоятельно зарегистрированная установка (по умолчанию `REGISTRATION_MODE="open"`) отправляет небольшой подписанный архив, распаковка которого вызывает OOM воркера; при `restart: unless-stopped` отказ повторяем.
- **Направление:** разбирать архив один раз; проверять `compress_type ∈ {0, 8}` у записей `zipfile.infolist()`; явно отвергать ZIP64; ограничивать объём распаковки за вызов. В BACKLOG отсутствует.
- **Дополнительно (направление истории и архивов):** два разбора могут разойтись не одним способом, в том числе при выборе записи конца центрального каталога. В Python это ещё и даёт `RuntimeError` → HTTP 500 вместо 4xx. Та же слабость есть в C#-читателе (F-07). Направление дополняется: отвергать неоднозначные архивы — с несколькими записями конца каталога или с непустым комментарием архива.

### F-02. Гонка дедупликации по SHA-256 даёт 500 вместо `already_received`
- **Серьёзность:** Средняя · **Категория:** Гонка · **Достоверность:** Подтверждено
- **Где:** `server/telemetry-ingest/app/main.py:362-364` (предпроверка), `:409-429` (вставка; `except Exception` удаляет файл и пробрасывает исключение); `server/telemetry-ingest/app/store.py:303-318` (`add_capture` не ловит `sqlite3.IntegrityError`), UNIQUE на `sha256` — `store.py:52`
- **Описание:** два параллельных запроса с одинаковым SHA оба проходят предпроверку; второй INSERT нарушает UNIQUE, и клиент получает 500. Повреждения БД нет, при повторе клиент получит `already_received`, но инвариант «параллельный дубликат обрабатывается атомарно» не выполнен.
- **Направление:** перехватывать `IntegrityError` и отвечать `already_received`.

### F-03. Открытая регистрация позволяет исчерпать реестр установок
- **Серьёзность:** Средняя · **Категория:** Безопасность (DoS) / Архитектура · **Достоверность:** Вероятно
- **Где:** `server/telemetry-ingest/app/main.py:39` (по умолчанию open), `:248-252`, `:55/72` (`MAX_INSTALLATIONS` 100 000); `server/telemetry-ingest/app/store.py:178-215`
- **Описание:** подпись доказывает лишь владение самостоятельно сгенерированным ключом. При глобальном лимите 1000 регистраций в час потолок 100 000 можно заполнить примерно за 100 часов, после чего новые легитимные установки получат 503. Самовосстановление — только 30-дневная чистка неактивных записей. `.env.example` рекомендует режим invite, но код по умолчанию работает в open.
- **Направление:** мониторинг заполнения реестра, более агрессивная чистка неактивных записей, invite для публичного экземпляра.

### F-04. Файл-сирота в `accepted/` при падении между `os.replace` и записью в БД
- **Серьёзность:** Низкая · **Категория:** Утечка диска · **Достоверность:** Вероятно
- **Где:** `server/telemetry-ingest/app/main.py:404-431` (`os.replace` на `:408` раньше `add_capture` на `:410`); `server/telemetry-ingest/app/store.py:320-336` (`prune` обходит только строки БД)
- **Описание:** файл без строки в БД никогда не удаляется и не учитывается в квоте 20 ГБ. Свободное место защищено отдельной проверкой `MINIMUM_FREE_BYTES`, поэтому худший исход — медленная утечка вплоть до ответов 507.
- **Направление:** периодически сверять `accepted/` с БД или записывать строку БД до перемещения файла.

### F-05. Конкурентный `prune()`: TOCTOU на `unlink`
- **Серьёзность:** Низкая · **Категория:** Гонка · **Достоверность:** Вероятно
- **Где:** `server/telemetry-ingest/app/store.py:320-336` (`unlink` без `missing_ok` на `:333`, транзакция не `immediate`)
- **Описание:** два параллельных `prune` выбирают одни и те же строки. Второй `unlink` бросает `FileNotFoundError` уже после успешного приёма, и клиент получает 500.
- **Направление:** `unlink(missing_ok=True)`; сериализовать `prune`.

### F-06. Клиент считает любой HTTP 2xx успешной загрузкой и удаляет локальный архив
- **Серьёзность:** Низкая · **Категория:** Потеря данных (краевой случай) · **Достоверность:** Вероятно
- **Где:** `src/LandingStats.App/TelemetryUpload/TelemetryUploadClient.cs:777-781` (`IsSuccessStatusCode` → `Uploaded`), `:674-680` (`File.Delete`)
- **Описание:** тело ответа (`status: accepted / already_received`) не проверяется. Если промежуточный узел вернёт 200 с HTML, архив будет удалён без фактического приёма. Риск снижен фиксированным HTTPS-хостом.
- **Направление:** подтверждать `status` в JSON-теле.

### F-07. `CaptureArchiveReader` на клиенте: тот же двойной разбор, результат проверки метода выбрасывается
- **Серьёзность:** Низкая · **Категория:** Сопровождаемость / defense-in-depth · **Достоверность:** Подтверждено
- **Где:** `src/LandingStats.Archives/CaptureArchive.cs:45, 49` (поле `_methods` присваивается и нигде не читается), `:119-157` (`CheckDirectoryBound`, проверка метода на `:145`), `:50` (`new ZipArchive` разбирает каталог заново)
- **Описание:** та же архитектурная слабость, что в F-01, плюс мёртвый код. Клиент читает собственные файлы, поэтому практический риск мал.
- **Направление:** применять карту методов к записям `ZipArchive` или удалить поле; исправлять вместе с F-01.
- **Дополнительно (направление истории и архивов):**
  - На net48 `.zst`-член читается через поток, не ограниченный заявленным размером; в Python такое ограничение есть.
  - `DecoderFallbackException` не превращается в `InvalidDataException`, хотя `TelemetryUploadClient.cs:712` ловит только его.

### F-08. Задача применения политики загрузки запускается без наблюдения
- **Серьёзность:** Низкая · **Категория:** Сопровождаемость · **Достоверность:** Вероятно
- **Где:** `src/LandingStats.App/MainWindow.TelemetryReports.cs:19` (`_runtimePolicyTask = ApplyRuntimePolicyAsync(...)` без `await` и продолжения)
- **Описание:** исключение при долговременной фиксации opt-in/opt-out останется незамеченным. Барьер opt-out в памяти срабатывает сразу, так что сама запрещающая граница не нарушается.
- **Направление:** наблюдать задачу и показывать сбой сохранения политики.

### F-09. Бюджет декодирования 10 с считается по wall-clock и превращается в постоянный 422
- **Серьёзность:** Низкая · **Категория:** Баг · **Достоверность:** код подтверждён; частота — гипотеза
- **Где:** `server/telemetry-ingest/app/capture_archive.py:147-154`; `server/telemetry-ingest/app/main.py:399-401`; `src/LandingStats.Archives/CaptureArchive.cs:169-173`; `src/LandingStats.App/Runtime/DurableOutbox.cs:211-222`
- **Описание:** в бюджет попадают GIL, I/O и параллельные валидации в threadpool. Превышение даёт `ValueError` → 422, и клиент навсегда отправляет валидный архив в карантин, где тот занимает бюджет очереди (B-07).
- **Направление:** считать CPU-время или отвечать 503.

### Проверено в этом направлении, проблем не найдено
- Канонические строки подписи однозначны; проверка RSA (PKCS#1 v1.5 / SHA-256, модуль 2048–4096) согласована между C# и Python.
- Защита от повтора: `UNIQUE(install_id, nonce)`, nonce хранится 24 ч при окне свежести ±15 мин.
- Приём тела потоковый с лимитом; `Content-Length` сверяется с подписанным размером.
- Zstd (format 2): граница кадра, окно ≤ 128 МБ, проверка длины и SHA, бюджет времени; формат согласован с C#-писателем.
- Dashboard: только `textContent`, строгий CSP, только GET/HEAD, IP-allowlist по реальному адресу сокета. LS-019 уже известен.
- Пути: идентификаторы и имена генерирует сервер, имена членов архива ограничены фиксированным набором.
- DPAPI CurrentUser + entropy; секреты не попадают в логи.
- Барьер opt-out и fencing поздних результатов (`UploadRuntimeOwner.cs:118-139`); воркер не удаляет файлы.
- Согласование возможностей: старые серверы получают только format 1 v5/6/7.
- Docker: non-root, read-only rootfs, `cap_drop ALL`, лимиты, образы запинены по digest; зависимости запинены.

---

## 3. Дефекты production, выявленные при анализе тестов

> Будут сверены с отчётами профильных направлений (нативные источники, хранилище), когда те завершатся.

### P-01. Сброс контекста управления (elevator / guard AGL) потерян в production после reset/reconnect
- **Серьёзность:** Средняя · **Категория:** Баг (регрессия рефакторинга) · **Достоверность:** Подтверждено (по коду)
- **Где:** `src/LandingStats.App/Telemetry/SimConnectSource.cs:126-129, 593-628, 704-708`; `src/LandingStats.App/Runtime/RecordingRuntimeState.cs:109-120`; `src/LandingStats.App/Runtime/SimulatorWorkerRole.cs:55-72, 126`; эталон v0.9.4 — `SimConnectLandingRecorder.cs:1152-1163` в HEAD; запись в историю — `src/LandingStats.App/Storage/LandingRecordFactory.cs:197-199`
- **Описание:** в v0.9.4 `ResetFlightDetection` сбрасывал `_lastGuardAglFeet`, `_axisElevatorSetPercent` и `_axisElevatorSetReceivedHostSeconds`. В новом коде этот сброс выполняется только в тестовом фасаде (`DetectionReset → _source.ResetControlContext()`). В production `DetectionReset` никто не слушает, `ResetControlContext` никто не вызывает, а `CloseSimConnect` эти поля тоже не трогает.
- **Сценарий:** после сброса полёта или переподключения кадры несут `AxisElevatorSetValid=true` со значением из предыдущего контекста, и оно сохраняется в историю и архивы. Влияние на пользователя невысокое (диагностические поля), но данные неверны.
- **Направление:** выполнять сброс контекста управления в production-пути reset/reconnect; покрыть тестом без фасада.
- **Подтверждено направлением нативных источников:** поля переживают и reconnect, и flight boundary внутри процесса; устаревшее значение после reconnect выдаёт только age.

### P-02. Неизвестное значение enum в записи истории ломает загрузку всей истории
- **Серьёзность:** Средняя · **Категория:** Баг / Совместимость · **Достоверность:** механизм подтверждён независимо тремя направлениями (тесты; форматы Core; история); частота срабатывания — вероятно
- **Где:** `src/LandingStats.Core/ReplaySourceEvidence.cs:23-27, 54-55` (`ReplaySourceTimelineCodec.Decode` и конструкторы бросают `ArgumentException`); `src/LandingStats.App/Storage/LandingRecordFile.cs:202`; `src/LandingStats.App/Storage/LandingRepository.cs:414-443, 525-543, 665-668`; `src/LandingStats.App/Runtime/RuntimeRetentionOwner.cs:137`
- **Описание:** фильтры хранилища считают повреждением только `IOException`, `InvalidDataException` и `SerializationException`. `ArgumentException` из декодера вылетает из `ReadColumnarRecord` и ломает `LoadDetail` и пересборку индекса в `LoadAll`, хотя должна быть пропущена одна запись.
- **Сценарий:** запись, созданная более новой версией с новым значением `SourceState` и пришедшая через Google Drive, или повреждение, прошедшее CRC gzip, — после этого история не загружается.
- **Дополнительно (направление форматов Core):**
  - Усечённый GUID (`new Guid(reader.ReadBytes(16))`, `ReplaySourceEvidence.cs:99-107`) тоже даёт `ArgumentException`.
  - `Decode`/`DecodeText` (`ReplaySourceTimelineCodec.cs:25-45`) оборачивают только `FormatException`.
  - При старте `ReconcileIndexWithDetailFiles` падает целиком, а `DeserializeBackup` срывает синхронизацию с Google Drive.
  - На IPC-проводе (`RuntimeWire.cs:57`) та же ситуация обработана правильно.
- **Дополнительно (направление истории):**
  - Мимо фильтра проходят также:
    - `NullReferenceException` на `null`-элементе `engines`/`contacts`;
    - `ArgumentException` из `LandingDeletionLedger.PathFor` для пустого Id — прямо в предикате `RemoveAll`;
    - `UnauthorizedAccessException` из `File.OpenRead`.
  - `EnsureIndexLoaded` присваивает `_summaries` до сверки. Первый `LoadAll` падает, а следующие вызовы (Retry, runtime presentation, Drive) молча возвращают недосверенный список, и ближайший `SaveIndex` его сохраняет.
  - Исключение посреди `List.RemoveAll` оставляет дубликаты и пропуски, после чего падает `ToDictionary` в `MainWindow.History.cs:208`.
  - Нечитаемые detail пропускаются, пользователь об этом не узнаёт.
  - Направление дополняется: сверять на локальной копии и присваивать `_summaries` только после успеха; показывать число нечитаемых записей.
- **Направление:** в декодерах переводить ошибки разбора в `InvalidDataException`; в тестах ожидать именно его (см. T-05).

---

## 4. Тесты и инструменты

Раннер `tests/LandingStats.App.RegressionTests/Program.cs`: «280 PASS» в плане соответствует 280 вызовам `Run` (`Program.cs:66-345`), каждый из которых — группа сценариев.

### T-01. `LandingStats.Runtime.RegressionTests` собирается в CI, но не запускается
- **Серьёзность:** Высокая · **Категория:** Тесты · **Достоверность:** Подтверждено
- **Где:** `.github/workflows/build.yml:160-167`; `tests/LandingStats.Runtime.RegressionTests/Program.cs:12-18`; `RuntimeProtocolRegressionTests.cs:38-151, 153-173`
- **Описание:** CI запускает только App (net48) и Archives (net48/net10). Только в проекте Runtime есть:
  - проверки строгого протокола `RuntimeWire`: обрезанные кадры, мусорные length-поля, лишний хвост, невалидные bool/UTF-8, фрагментированное чтение;
  - отказы `WorkerHandshake`: чужие token/build/capabilities/clock/generation, повторный hello;
  - fencing;
  - прогон binary-кодеков на net10.

  Запись плана «protocol net48/net10 PASS» — результат ручного запуска.
- **Последствие:** регрессия валидации IPC (например, принятие чужого token или кадра без ограничения длины) или расхождение binary64-fingerprint между CLR проходит CI зелёным. Связано с LS-002; сам пробел в BACKLOG не записан.
- **Направление:** добавить запуск net48 и net10 в CI.
- **Подтверждено направлением сборки и CI:** настоящий запуск launcher → guardian → App есть только в подписанных сборках, а в PR и на main guardian проверяется лишь fixture'ами. Нужен unsigned smoke-запуск bundle.

### T-02. Тестовый фасад `SimConnectLandingRecorder` склеивает компоненты иначе, чем production
- **Серьёзность:** Высокая · **Категория:** Тесты · **Достоверность:** Подтверждено
- **Где:** `tests/LandingStats.App.RegressionTests/SimConnectLandingRecorder.cs:35, 38-46, 59, 93, 117`; `RecorderTestAccess.cs:12-17`; пустые проверки в `Program.cs:2078, 2143-2148, 4313`
- **Описание:**
  - Фасад используют 15 файлов тестов, около 41 вызова (PauseRecorder, TwoStageRecorder, RecorderConnection, LandingCaptureGate, PhysicsContinuity, Runway*, TelemetryDecisionJournal и др.). Они гоняют настоящие `SimConnectSource` и `LandingEpisodeCoordinator`, но через склейку, которой в production нет.
  - Только в фасаде есть:
    - `DetectionReset → ResetControlContext`;
    - обновление джойстиков WinMM с `ResetPreRoll()`;
    - чтение локального джойстика в каждом кадре;
    - `HasContact` из координатора (в worker он всегда `false`).
  - `LegacyControllerPathIsAbsent` проверяет `typeof(SimConnectLandingRecorder)` — тестовый класс — и упасть не может. Так же устроена проверка `GetEvent("AircraftGroundStateChanged")`.
  - `SimulatorExitCompletesLanding` проверяет порядок событий фасада, а не production-путь `SourceLifecycle.SimulatorQuit` → `RecordingRuntimeState` → `MainWindow.Runtime.cs:134-138`.
  - `RecorderTestAccess.Owner` выбирает владельца по имени члена; имя `Disconnect` есть и у source, и у coordinator.
- **Последствие:** поведение, которое фасад реализует сам, тестами не защищено; регрессия P-01 уже прошла незамеченной.
- **Направление:** фикстура поверх `RecordingRuntimeState` с подачей `RuntimeEnvelope`; пустые проверки убрать или направить на production-типы.
- **Подтверждено направлением нативных источников:**
  - Production использует `constructOnOwner: true` (синхронный `TryConnect` на owner). Фасад и fake-native тесты LS-011 гоняют путь `ConnectAsync` с `_pendingConnectionMessages`, который в production мёртв (N-06).
  - Фасад подключает отложенное перечисление контроллеров на время записи, `SeedAirportFacilities` и `RunwayFacilitiesUpdated` — в production этого нет.
  - Ветка `catch { CloseSimConnect(); throw; }` покрыта только opt-in live-пробами.
  - Нужны тесты `SimulatorWorkerRole` и `ControllersWorkerRole` с fake SDK/API.

### T-03. Связка runtime ↔ guardian на уровне App и production-параметры восстановления не тестируются
- **Серьёзность:** Высокая · **Категория:** Тесты · **Достоверность:** Подтверждено (отсутствие тестов)
- **Где:** `src/LandingStats.App/MainWindow.Runtime.cs:27-41`; `src/LandingStats.App/App.Guardian.cs:19-80`; `src/LandingStats.App/Runtime/LandingRuntime.cs:125-133`; `RuntimeComponentCatalog.cs:26-45`; `ComponentSupervisor.cs:34-42, 227-233`; `src/LandingStats.ProcessControl/GuardianPolicy.cs:11`; тесты `GuardianRegressionTests.cs:174-235`, `ComponentSupervisorRegressionTests.cs:16-22, 30`
- **Описание:** guardian-сценарии гоняют `GuardianSupervisor` против fixture-процесса, который сам придумывает `GuardianStatus`.
  - Не покрыты:
    - `ReadGuardianHealth` (RecoveryRequests → NeedsRecovery → IsPresentationDependency);
    - `ReadObservationQpc` и `SafeToRestart`;
    - `ConnectGuardianAsync` (`Environment.Exit(72)` при потере канала);
    - `AcknowledgeExitAsync` и `PrepareGuardianRecoveryAsync`;
    - путь `App.RequestRuntimeRecovery`.
  - Все тесты supervisor и runtime используют свои миллисекундные `ComponentRecoveryOptions`. Production-каталог (OperationDeadline 10–180 с) проходит только `Validate()`.
  - Backoff не проверяется. Неявное правило «MaximumBackoff 30 с < ControlStall 60 с» нигде не зафиксировано.
- **Последствие:** ошибка в переводе здоровья runtime в `GuardianStatus` или в дефолтах приведёт к циклическим перезапускам App либо к зависшему runtime, который guardian не восстанавливает. Обнаружится это только живым прогоном.
- **Направление:** тест `LandingRuntime` + `ReadGuardianHealth` с принудительной эскалацией и зависанием; тест совместимости дефолтов с `GuardianPolicy`.

### T-04. Проверки внутри callback'ов под supervisor превращаются в перезапуски
- **Серьёзность:** Средняя · **Категория:** Тесты · **Достоверность:** Подтверждено
- **Где:** `ComponentSupervisorRegressionTests.cs:51` (с `ComponentSupervisor.cs:211-226`); `RuntimeSourceDispatchRegressionTests.cs:28, 35, 38-39, 46-47`; `DurableDispatcherRegressionTests.cs:45, 47, 64`; `GuardianRegressionTests.cs:222` (с `GuardianGeneration.cs:216`); `LiveRuntimeRecoveryProbe.cs:354, 359`
- **Описание:**
  - `Check(active == 0, "Retirement preceded owner exit.")` стоит внутри retire-callback. `RetireAsync` ловит любое исключение и повторяет вызов, а `retired++` считается только при успехе. Поэтому итоговое `retired == 21` выполняется даже при нарушении — проверка полностью проглатывается.
  - В RuntimeSourceDispatch провал проверки рушит диспетчер, журнал перезапускается, вход при replay пропускается. Нарушение порядка в лучшем случае видно как 20-секундный таймаут с посторонним сообщением.
  - У guardian-fixture exit code не проверяется.
- **Последствие:** регрессии supervisor или упорядочивания источников проходят как PASS либо дают ложный таймаут.
- **Направление:** собирать нарушения в очередь и проверять после теста; проверять `Snapshot.Failures/Generation`.

### T-05. `Reject`-хелперы засчитывают любое `ArgumentException` как корректный отказ
- **Серьёзность:** Средняя · **Категория:** Тесты · **Достоверность:** Подтверждено
- **Где:** `ReplaySourceCoreRegressionTests.cs:36, 64`; `ControllerRuntimeRegressionTests.cs:142`; `CoordinatorRecoveryRegressionTests.cs:162`; `SimulatorWorkerRegressionTests.cs:168`
- **Описание:** 5 из 8 испорченных смещений (10, 69–72) в тесте кодека «проходят» за счёт `ArgumentException` из конструкторов. Production-хранилище это исключение не обрабатывает (см. P-02), то есть тест маскирует дефект.
- **Направление:** принимать только `InvalidDataException`.

### T-06. «Эталонные» fingerprint и ID не зафиксированы: проверки сравнивают код сам с собой
- **Серьёзность:** Средняя · **Категория:** Тесты · **Достоверность:** Подтверждено
- **Где:** `BinaryTelemetryRegressionTests.cs:46-54, 73-80, 83`; `tests/LandingStats.Runtime.RegressionTests/Program.cs:14`; `RuntimeProtocolRegressionTests.cs:172`; `CorpusFingerprintCheck.cs:47-71`
- **Описание:**
  - `GoldenFingerprint()` сравнивается только сам с собой (под культурой tr-TR).
  - Хеш v1 считается той же функцией `TelemetryCsv.Format`, что и в production.
  - `StableRuntimeIdentity` и fingerprint только печатаются в консоль.
  - `CorpusFingerprintCheck` печатает «PASS», ничего не сравнивая.
- **Последствие:** изменение `TelemetryBinaryCodec`, `TelemetryCsv.Format` или `StableRuntimeIdentity` без смены версии формата ломает сохранённые `AnalysisGeometrySnapshot` v1/v2 и ID при cold recovery, а тесты остаются зелёными (LS-002).
- **Направление:** фиксированные фикстуры «вход → ожидаемый хеш/ID».

### T-07. Совместимость реального .NET-writer с Python-receiver в CI не проверяется
- **Серьёзность:** Средняя · **Категория:** Тесты · **Достоверность:** Подтверждено
- **Где:** `CaptureArchiveRegressionTests.cs:63`; `AutomaticTelemetrySettingsRegressionTests.cs:72`; `server/telemetry-ingest/tests/test_capture_archive.py:90`, `test_controller_provenance.py:93`, `test_source_observations.py:63`; `docs/capture-archive-format.md:117-123`
- **Описание:**
  - Python-тесты «actual net48 writer» стоят под `skipUnless(env)`. CI-задачи windows и ubuntu независимы, поэтому в CI эти тесты всегда пропускаются.
  - Имена переменных окружения не совпадают: C# пишет `LANDINGSTATS_ARCHIVE_FIXTURE`, Python читает `MSFSLS_ARCHIVE_FIXTURE`. У `MSFSLS_TEST_ARCHIVE_OUTPUT` нет потребителя.
  - `File.Copy` без overwrite падает при повторном запуске.
  - Документ при этом утверждает, что покрытие есть.
- **Последствие:** изменение формата Zstd-v2, provenance или source-state на клиенте сделает архивы нечитаемыми для сервера (422 → карантин) при зелёном CI.
- **Направление:** передавать артефакт writer'а в серверную задачу CI и согласовать имена переменных.

### T-08. Нестабильность из-за реального времени и отсутствие таймаута на тест
- **Серьёзность:** Средняя · **Категория:** Тесты · **Достоверность:** Вероятно
- **Где:**
  - короткие таймауты: `ComponentSupervisorRegressionTests.cs:16-22` (OwnerStall 80 мс, Probe 50 мс);
  - проверки вида «за N мс ничего не произошло»: `ComponentSupervisorRegressionTests.cs:78, 105, 125`, `LandingRuntimeRegressionTests.cs:126`, `UploadWorkerRegressionTests.cs:181`, `CloudWorkerRegressionTests.cs:99`;
  - `ProcessWorkerHostRegressionTests.cs:27, 67, 90-93, 151`;
  - верхние границы времени: `AsyncHistoryRegressionTests.cs:66`, `RunwayMetadataPersistenceRegressionTests.cs:134`, `Program.cs:1209` (< 100 мс);
  - раннер: `Program.cs:4363-4380`.
- **Описание:** в раннере нет таймаута на тест и строки START. При зависании весь 30-минутный бюджет CI-задачи (`build.yml:21`) уходит без указания, какой тест завис. Тяжёлые процессные тесты (около 200 реальных процессов) ни разу не запускались на hosted runner, поскольку код не закоммичен, а pipe-handshake зависит от среды выполнения.
- **Последствие:** нестабильный CI, на который перестают обращать внимание, или job timeout.
- **Направление:** таймаут и START-лог на тест; заменить ожидания по стенным часам на явные сигналы.

### T-09. Непокрытые пути отказов в новых компонентах
- **Серьёзность:** Средняя · **Категория:** Тесты · **Достоверность:** Подтверждено
- **Где:** `src/LandingStats.App/Runtime/ProcessWorkerHost.cs:94-104` (+ `ReceiveAsync`); `RuntimeCommandQueue.cs:88, 96-117`; `RecordingRuntimeState.cs:211-218`; `src/LandingStats.WorkerHost/Program.cs:69-80, 160-176`; `OwnerCadence.cs:67-75`; `src/LandingStats.App.Launcher/Program.cs:336-476`
- **Описание:** нет тестов на:
  - нарушения протокола со стороны worker в родителе (чужой sequence или identity, сверхдлинный кадр, неверный token, PID клиента pipe);
  - `RuntimeCommandQueue`: переполнение на 1024, битый batch/progress, повтор после потерянного ответа;
  - отрицательные входы `RecordingRuntimeState`: нарушение порядка или поколения, отсутствие hello;
  - разбор аргументов WorkerHost и 15-секундный таймаут активации;
  - fallback `OwnerCadence` без high-res таймера;
  - очистку каталогов launcher в корне, где теперь лежат durable-данные runtime.

### T-10. Нет статуса SKIP; глобальное состояние восстанавливается не всегда
- **Серьёзность:** Низкая · **Категория:** Тесты · **Достоверность:** Подтверждено
- **Где:** `Program.cs:4363-4380, 898-927`; `LegacyEngineQuotaRegressionTests.cs:20-21`
- **Описание:** проверка на реальном старом engine без `LANDINGSTATS_LEGACY_ENGINE` молча пропускается и засчитывается как PASS. `LocalizationManager.Apply("ru")` и похожие изменения без `finally` при падении протекают в следующие тесты.

### T-11. Слабые негативные проверки в PowerShell-тестах
- **Серьёзность:** Низкая · **Категория:** Тесты · **Достоверность:** Подтверждено
- **Где:** `tests/UpdateCompatibility.Tests.ps1:62-79`; `tests/GuardianBundle.Tests.ps1:63, 92-96`; `tests/BundlePayload.Tests.ps1:2`
- **Описание:**
  - `Assert-Rejected` засчитывает любую ошибку, не проверяя причину.
  - GuardianBundle различает только exit 0 / не 0 и компилирует пробу через устаревший `csc.exe` из Framework64.
  - BundlePayload тестирует исходник, а не поставляемый бинарь.

### T-12. Зависимость тестов от машины
- **Серьёзность:** Низкая · **Категория:** Тесты · **Достоверность:** Подтверждено
- **Где:** `Program.cs:1951-1985, 1023`; `Review089UiRegressionTests.cs:168-179`; `ControllerRuntimeRegressionTests.cs:110-132`; `RuntimeSourceDispatchRegressionTests.cs:65`; `LoadingIndicatorRegressionTests.cs:37, 71-79`; `GuardianUpdateRegressionTests.cs:77`
- **Описание:**
  - Тест ищет `artifacts/MSFS-Landing-Stats.exe` вверх от bin и принимает устаревший артефакт.
  - XAML читается из исходного дерева.
  - Используются реальные WinMM-устройства.
  - Показывается настоящее окно и используется рефлексия по внутренностям WPF `MediaContext`.
  - `Exited(pid)` подвержен повторному использованию PID.

### T-13. Хрупкие отдельные проверки
- **Серьёзность:** Низкая · **Категория:** Тесты · **Достоверность:** Подтверждено
- **Где:** `EpisodeReportRuntimeRegressionTests.cs:73-75`; `Program.cs:1141-1146`; `GoogleDriveBackupRegressionTests.cs:165-173`
- **Описание:**
  - Отказ анализа внедряется через `EpisodeProcessor(null!)` в расчёте на `NullReferenceException`: null-guard в конструкторе сломает тест.
  - `drain.IsCompleted` проверяется сразу после `Task.Run`, из-за гонки проверка почти пустая.
  - Тест отмены удаления в Google Drive переписан под новый ledger, и восстановление на том же устройстве больше не проверяется.

### Покрытие новых runtime-компонентов (только пробелы)

| Компонент | Оценка |
|---|---|
| `RuntimeComponentCatalog`, `WorkerRoleFactory.Create` | нет / только `Validate()` и happy path |
| `App.Guardian.cs`, очистка каталогов в App.Launcher | нет / только распаковка |
| `RuntimeWire`, `WorkerHandshake`, `RuntimeIdentity` | негативные тесты есть, но в CI не запускаются (T-01) |
| `OrderedSourceIngress` | мёртвый код (в production не используется) |
| `RuntimeCommandQueue` | только happy path |
| `ComponentSupervisor` | backoff и production-опции не проверены |
| `ProcessWorkerHost`, bootstrap WorkerHost | ошибки протокола и аргументы не проверены |
| `RecordingRuntimeState` | отрицательные входы не проверены |
| `LandingRuntime`, `RuntimePresentation` | `SafeToRestart`, `ObservationQpc`, `ReadGuardianHealth` не проверены |
| `CloudWorkerRole`, `IsolatedGoogleDriveApi` | реальный Drive-клиент в воркере не проверен |
| `SimulatorWorkerRole` | реальный SDK — только ручные Live-пробы |
| `ControllersWorkerRole` / `OwnerCadence` | fallback без high-res таймера не проверен |
| Координаторы записи | покрыты, но в основном через фасад (T-02) |

Хорошо покрыты: `RecoveryJournal`, `RollingRuntimeJournal`, `DurableRuntimeDispatcher`, `EpisodeTransactionStore`, `DurableOutbox`, retained analysis, cold recovery, RAW export, квота/retention, `LandingStats.Archives`, ledger/catalog/replay-source archives.

### Проверено в этом направлении, проблем не найдено
- Раннер: exit-коды 0/1/2; `--filter` без совпадений возвращает 2; ошибки уходят в stderr с полным стеком.
- Ручные и live-пробы запускаются только по явным аргументам и в CI не попадают; без MSFS возвращают 3 (INCONCLUSIVE).
- Тесты не пишут в реальные `%LOCALAPPDATA%`/`%APPDATA%`, реестр и `exe.xml`; используются временные корни.
- Реальных обращений к Google или серверу телеметрии нет.
- Имена mutex, pipe и job уникальны (GUID); удаление временных каталогов защищено проверкой префикса TEMP.
- Журналы и транзакции: обрыв на каждом байте, порча в середине, ошибки write/flush, крах между этапами, tombstone — проверки содержательные.
- Восстановление по checkpoint сверяется побайтно после каждого входа.
- Хелперы `Throws`/`Equal`/`Near` корректны, включая NaN.

---

## 5. Контролы реплея и графиков

Ни одна из находок R-01…R-15 в BACKLOG не описана. R-01 и R-08 относятся к LS-013, R-03–R-05 и R-10 — к LS-018.

### R-01. Линия «pitch raw» строится по слоту WinMM без учёта каталога и поколения контроллера
- **Серьёзность:** Высокая · **Категория:** Баг (атрибуция органов управления) · **Достоверность:** Подтверждено (по коду); частота зависит от hotplug и перенумерации в окне записи
- **Где:** `src/LandingStats.App/Controls/LandingChart.cs:558, 615` (используется в 835–836 и 1112); корень — `src/LandingStats.App/Models/LandingRecord.cs:494-509`, `src/LandingStats.App/Storage/LandingRecordFactory.cs:228, 244-318`; подпись — `src/LandingStats.App/MainWindow.xaml.cs:1488-1492`
- **Описание:**
  - `LandingChart.PitchInput` берёт `LandingRecord.PitchInputPercent`, а тот читает `RawControllerYAxisPercent[слот]` по одному на всю запись `RawPitchInputSourceIndex`.
  - Слот выбирается корреляцией Y-оси по всей серии (`RefreshRawPitchInputSelection`). Если слот переиспользован, корреляция смешивает разные устройства.
  - `ControllerTimeline` не используется, хотя 3D-оверлей (`ResolveCatalogs`) те же кадры разделяет по поколениям.
  - Там, где сырой оси нет или она устарела, молча подставляется обработанная команда `PilotPitchPercent`, а линия всё равно подписана «pitch raw».
- **Сценарий:** в окне записи устройство переподключили или WinMM перенумеровал устройства, и слот джойстика заняли педали. Корреляция ≥ 0,75 набрана на заходе, поэтому после смены каталога линия рисует Y педалей. 3D-оверлей в это же время показывает другое устройство или «нет оси».
- **Направление:** для записей с `ControllerTimeline` строить линию по сегментам каталога, как это делает `ReplayControlInputs`; вне исходного поколения — разрыв линии вместо подстановки.

### R-02. Повторная подача той же посадки сбрасывает реконструкцию
- **Серьёзность:** Высокая · **Категория:** Баг (теряется состояние просмотра) · **Достоверность:** Подтверждено
- **Где:** `src/LandingStats.App/Controls/LandingReplayView.cs:140-161` (143–144, 160); триггеры — `src/LandingStats.App/MainWindow.Runtime.cs:76-108` → `MainWindow.History.cs:254-301` → `MainWindow.xaml.cs:241-271`; `MainWindow.History.cs:290-295`
- **Описание:**
  - `SetRecord` не отличает новую запись от обновления той же (тот же Id). Он всегда вызывает `StopPlayback`, ставит `_resetCameraPending=true`, возвращает время в 0 и заново выполняет `BuildScene` и ранжирование осей.
  - `ApplyRuntimeSnapshotAsync` на любое изменение эпизода (новый эпизод, смена `ReportReady`) и на смену облачной ревизии истории перечитывает сводки и вызывает `InvalidateLoadedHistoryDetails`.
  - `SelectRecordAsync` получает новый экземпляр той же посадки, `ReferenceEquals` ложно, вызывается `PresentRecord` → `SetRecord`. Заодно сбрасываются зум и изоляция графиков.
  - Ветка «сменилась только геометрия ВПП» восстанавливает время, но камеру всё равно сбрасывает и воспроизведение останавливает — вопреки комментарию в `MainWindow.History.cs:286`.
- **Сценарий:** сразу после посадки пользователь открывает реконструкцию, запускает воспроизведение и вращает камеру. Через несколько секунд завершается `FinalizeReport`, приходит снимок runtime: время прыгает в 0, камера сбрасывается, воспроизведение останавливается. То же происходит при каждой следующей посадке и при синхронизации с облаком.
- **Независимо подтверждено направлением WPF-оболочки:**
  - На каждый эпизод приходят два уведомления (history saved и report ready), в том числе для diagnostic-only заходов.
  - У всех, кто хоть раз синхронизировал Drive, при каждом старте примерно через 2 с происходит лишняя полная перезагрузка с повторным показом (`CloudSyncRuntimeOwner.cs:143`).
  - Если начальный `LoadHistoryAsync` ещё не закончился, он отбрасывается по версии, и `StoragePathText` остаётся пустым.
  - В 0.9.4 `AddLandingRecords` кеш деталей не инвалидировал.
  - Ситуацию усугубляет U-01.
- **Направление:** путь «обновить метаданные той же записи» с сохранением времени, камеры и воспроизведения; не пересоздавать представление при том же Id и тех же сериях.

### R-03. Каждое изменение hover-индекса полностью перерисовывает график
- **Серьёзность:** Средняя · **Категория:** Производительность · **Достоверность:** Подтверждено (по коду); величина — оценка
- **Где:** `src/LandingStats.App/Controls/LandingChart.cs:274-294, 313-370, 617-749` (`SelectMany` с `new[]` в 643, 691–698, 721), `948-979, 1005-1052`; рассылка — `src/LandingStats.App/MainWindow.xaml.cs:273-289`
- **Описание:** `SetHoverTime` вызывает `InvalidateVisual`, и `OnRender` каждый раз:
  - копирует видимые точки;
  - считает диапазоны через LINQ с массивом на каждую точку (во FlightControls — 6 значений на точку);
  - строит `StreamGeometry` по всем n точкам каждой серии;
  - создаёт `FormattedText` для всех подписей сетки.

  При этом от времени зависят только курсор и флажки. `OnChartHoverTimeChanged` рассылает время на все 4 графика; в реконструкции полоса G перерисовывается на каждый seek и тик.
- **Сценарий:** запись на 2–6 тыс. точек, вкладка «Управление», мышь движется по графику — 4 полные перерисовки на каждое событие и около 6n мелких массивов, отсюда давление на GC.
- **Направление:** кешировать диапазоны и геометрию серий или вынести статический слой в отдельный `DrawingVisual`; курсор и флажки — в лёгкий оверлей.

### R-04. Оверлеи 3D на каждое изменение времени перерисовывают всё и создают незамороженные ресурсы
- **Серьёзность:** Средняя · **Категория:** Производительность · **Достоверность:** Подтверждено (по коду); вклад в рывки — Вероятно
- **Где:** `src/LandingStats.App/Controls/ReplayOverlays.cs:24-31, 104-129, 164-197, 221-244`; вызов — `LandingReplayView.cs:264-270`
- **Описание:** `UpdateReadouts` инвалидирует все три оверлея, и их `OnRender` за кадр создаёт:
  - до ~44 новых незамороженных `Pen`;
  - ~25–30 `FormattedText`, включая неизменные подписи;
  - 6 `RectangleGeometry` и 6 слоёв `PushOpacity`;
  - 4 разбора цвета через `ColorConverter`.

  Комментарий к `ReplayMetricsOverlay` («only the cursor and current values change») коду не соответствует. На восстановленном синхронном пути (LS-018) всё это выполняется на каждый seek и тик.
- **Направление:** замороженные статические перья и кисти; статику — в кешируемый `DrawingGroup` или отдельный визуал; перерисовывать только курсор и значения.

### R-05. В 3D-сцене используются приёмы, дорогие для render-потока WPF
- **Серьёзность:** Средняя · **Категория:** Производительность · **Достоверность:** Вероятно (по документированному поведению WPF; не измерено)
- **Где:** `src/LandingStats.App/Controls/LandingReplaySurface.cs:103-118`; `src/LandingStats.App/Controls/LandingReplayView.cs:23, 347-361`
- **Описание:**
  - Номера ВПП — `MaterialGroup` из двух `DrawingBrush` без `RenderOptions.CachingHint`. Некешированная `DrawingBrush` на 3D-поверхности перерисовывается каждый кадр при движении, то есть ровно при вращении и перемотке.
  - У `Viewport3D` стоит `ClipToBounds=true` — медленный сглаженный клип, хотя родительский Grid и так клипует.
  - Разметка, след, маркеры и самолёт — группы из двух материалов, у всех моделей есть `BackMaterial`.
- **Направление:** `CachingHint=Cache` до `Freeze` или растеризованный `ImageBrush`; `ClipToBounds=false`; убрать лишние `BackMaterial` и группы материалов; затем повторить замеры LS-018.

### R-06. `SetRecord` синхронно в UI-потоке выполняет тяжёлые модельные расчёты
- **Серьёзность:** Средняя · **Категория:** Производительность · **Достоверность:** Вероятно (оценка по коду)
- **Где:** `src/LandingStats.App/Controls/LandingReplayView.cs:140-161, 307-335`; `src/LandingStats.App/Models/ReplayControlInputs.cs:209-252, 281-320`; `ReplayControlInputs.Catalogs.cs:15-64`; `LandingReplayContacts.cs:44, 67`
- **Описание:**
  - Ранжирование осей: 3 роли × каждая меняющаяся ось × 17 лагов × `Correlation` по n точкам с бинарным поиском на точку. Это порядка 10⁷–10⁸ операций, то есть десятки–сотни миллисекунд.
  - Каталожный путь для каждого уникального каталога заново декодирует таблицу (`Validate`), копирует всю серию (`ForCatalog`) и повторяет ранжирование.
  - Плюс клиппинг террейна, индекс высот (R-07) и двойное проецирование трека каждого соседнего контакта.
  - Всё это повторяется при каждой повторной подаче записи (R-02). Кеша нет, хотя те же расчёты уже выполняются в воркере для диагностики.
- **Сценарий:** длинная многоконтактная посадка с HOTAS даёт фриз UI при выборе записи и на снимках runtime. После рестартов controller-воркера (новые каталоги) — кратно дольше.
- **Направление:** считать трек, привязки и поверхность вне UI-потока и кешировать по Id; валидировать timeline один раз.

### R-07. Индекс высот копирует 112-байтную структуру треугольника в каждую ячейку его bounding box
- **Серьёзность:** Средняя · **Категория:** Производительность / Память · **Достоверность:** структура — Подтверждено; объём — оценка
- **Где:** `src/LandingStats.App/Controls/LandingReplaySurface.cs:37-59, 151-197`
- **Описание:** треугольники террейна охватывают всю ширину полосы (200 м). `IndexTerrainHeights` кладёт копию `HeightTriangle` во все ячейки 20×20 м прямоугольника охвата, не проверяя пересечение: при курсе около 0/90° это 11–22 ячейки, около 45° — 60–80. Клиппинг вокруг ВПП увеличивает число треугольников в 2–5 раз. Итого — десятки МБ на типичную запись с линейным ростом по длине. Память удерживается через `_gearRegistration`; индекс строится заново при каждом `SetRecord`, а в воркере — для каждого контакта.
- **Направление:** хранить индексы треугольников (int) и растеризовать треугольник только в пересекаемые ячейки, либо индексировать по станции вдоль трека.

### R-08. Подписи источников в подсказке берутся из изменяемого состояния
- **Серьёзность:** Низкая · **Категория:** Баг · **Достоверность:** Подтверждено
- **Где:** `src/LandingStats.App/Controls/ReplayOverlays.cs:141-154`; `src/LandingStats.App/Models/ReplayControlInputs.Catalogs.cs:45-46, 76-87`
- **Описание:** `Pitch`/`Roll`/`Yaw`/`BindingRevision` меняются побочным эффектом `Sample()`, а `ReplayControlOverlay.Update` вызывает `Sample` только при наличии позы. В разрывах трека и до первой позы подсказка показывает устройства прошлого интервала, что противоречит LS-013 («Tooltip показывает источник текущего интервала»).
- **Направление:** выбирать сегмент по времени чистой функцией, независимо от наличия позы.

### R-09. Диагностический журнал теряет выбор осей поздних поколений
- **Серьёзность:** Низкая · **Категория:** Сопровождаемость · **Достоверность:** Подтверждено
- **Где:** `src/LandingStats.App/Models/LandingReplayDiagnostics.cs:21-29, 45-53, 59`; `ReplayControlInputs.Catalogs.cs:37`
- **Описание:** `Append` отбрасывает записи `replay.inputs` всех контактов, кроме первого («владельца»). Каталожный путь не пишет `episode_*`, поэтому ранжирование каталогов, которых нет в первом контакте, пропадает, хотя `binding_diagnostics_reference` утверждает обратное. Записи ранжирования не содержат идентификатора каталога.
- **Направление:** ключ владельца = (канонический контакт, каталог); добавить индекс каталога в details.

### R-10. Нативный animation clock тикает, пока вкладка реконструкции видима, в том числе в свёрнутом окне
- **Серьёзность:** Низкая · **Категория:** Производительность · **Достоверность:** Подтверждено; нагрузка — гипотеза
- **Где:** `src/LandingStats.App/Controls/LandingReplayView.cs:118-125, 227-252`; `src/LandingStats.App/MainWindow.xaml.cs:1901-1904`
- **Описание:** часы гасятся только по `IsVisible` и наличию трека. При `WindowState.Minimized` `IsVisible` остаётся true, и тики на частоте кадров идут весь полёт в MSFS. Это лучше, чем в 0.9.4 (там часы тикали всегда), но нагрузка остаётся.
- **Направление:** гасить часы при минимизации или неактивном окне либо включать их только на время взаимодействия.

### R-11. Нет обработки `LostMouseCapture` — `_isSelecting` залипает
- **Серьёзность:** Низкая · **Категория:** Баг · **Достоверность:** Подтверждено
- **Где:** `src/LandingStats.App/Controls/LandingChart.cs:399-403, 419-427, 429-482`
- **Описание:** если захват мыши потерян во время выделения (Alt+Tab, диалог), прямоугольник выделения дальше следует за курсором без нажатой кнопки, а `OnMouseLeave` не сбрасывает hover — до следующего щелчка.
- **Направление:** переопределить `OnLostMouseCapture` со сбросом выделения.

### R-12. `PhysicalInputStrip` не используется в production и не знает о каталогах
- **Серьёзность:** Низкая · **Категория:** Сопровождаемость · **Достоверность:** Подтверждено
- **Где:** `src/LandingStats.App/Controls/PhysicalInputStrip.cs:15-140` (87, 136–139)
- **Описание:** контрол используется только тестами. Имена устройств он берёт из `ControlInputSources` без `ControllerTimeline` и на каждый ярлык создаёт незамороженные кисти.
- **Направление:** удалить вместе с тестом или перевести на каталожную модель.

### R-13. При разборе XAML `MainWindow` строятся две полные модели самолёта
- **Серьёзность:** Низкая · **Категория:** Производительность · **Достоверность:** Подтверждено
- **Где:** `src/LandingStats.App/Controls/ReplayAircraftModel.cs:13-16, 122-130`
- **Описание:** статическая инициализация строит `Shared` (с шасси, нужна только тестам) и `SharedAirframe`. `Radius()` на каждый вызов пересчитывает весь `FuselageProfile()` — около 2,5 тыс. раз на модель. Это путь ускоряемого запуска, хотя эффект небольшой.
- **Направление:** `Lazy<>` или удалить `Shared`; кешировать профиль.

### R-14. Часы реконструкции всегда показывают «s»
- **Серьёзность:** Низкая · **Категория:** Локализация · **Достоверность:** Подтверждено
- **Где:** `src/LandingStats.App/Controls/LandingReplayView.cs:209`
- **Описание:** суффикс зашит в код. В остальных местах используется `Unit.SecondSuffix`, который в русском UI даёт «с».

### R-15. Монолитный `LandingChart`, дублированная математика, модели зависят от 3D-рендеринга
- **Серьёзность:** Низкая · **Категория:** Архитектура · **Достоверность:** Подтверждено
- **Где:**
  - `src/LandingStats.App/Controls/LandingChart.cs` (1362 строки);
  - бинарные поиски: `LandingChart.cs:1272-1300`, `PhysicalInputStrip.cs:123-134`, `MainWindow.xaml.cs:1827-1862`, `LandingGearSeriesBuilder.cs:266-299`, `LandingReplayTrack.cs:80-85`, `ReplayControlInputs.cs:325-338`;
  - `LandingReplayDiagnostics.cs:4, 81-84`.
- **Описание:**
  - `LandingChart` совмещает диапазоны, кеш шасси, легенду и hit-test, жесты и рисование. Поэтому нельзя кешировать слои (R-03) и тестировать без WPF.
  - Есть не меньше 6 рукописных бинарных поисков с разной политикой ничьих: `ClosestIndex` выбирает ранний индекс, `PhysicalInputStrip.Nearest` — поздний. Дублируются `Brush()`, `Finite()` и свёртка курса.
  - `Models/LandingReplayDiagnostics` зависит от `Controls`: в воркере на MTA-потоках пула строятся WPF-объекты только ради высот и шасси. Любая будущая STA- или UIElement-зависимость молча превратится в `diagnostics_failed` (см. S-02).
- **Направление:** расчёт поверхности и шасси — в чистую модель без `MeshGeometry3D`; один общий helper поиска по времени.

### Проверено в этом направлении, проблем не найдено
- Жизненный цикл presentation clock и `DispatcherTimer`: снимаются при скрытии и Unloaded; `CompositionTarget.Rendering` не используется; утечек представления не найдено.
- `ReplayInteractionDiagnostics` включается только переменной окружения; очередь ограничена 8192 строками, запись идёт пачками вне UI-потока, сети нет.
- Каталожная привязка осей 3D-оверлея: точный ключ на сегмент, reuse WinMM ID даёт новый ключ, интерполяции через границу поколения нет; обратная перемотка корректна; старые записи идут прежним путём.
- Координаты: ENU, истинные курсы, кватернионы и знак бокового смещения согласованы.
- NaN/Infinity: нефинитные позы отбрасываются, нулевые интервалы защищены.
- Разметка ВПП: обозначения 1–36 с L/R/C, ICAO/FAA; H1/N/NE отклоняются; цепочка ILS → PAPI → без зоны касания; узкие ВПП; отрицательных `Rect` нет.
- Поиск по времени при hover и seek логарифмический; статические кеши не растут неограниченно.
- UI-объекты трогаются только из UI-потока; ошибки `SetRecord` ловятся в `SelectRecordAsync`.

---

## 6. Координаторы записи

В BACKLOG ни одна из находок не зафиксирована; связи с LS-015/LS-017 указаны. Совпадающая находка направления форматов Core о стоимости checkpoint объединена с C-07.

### C-01. Устаревший запрос checkpoint после restore навсегда ломает журнал
- **Серьёзность:** Высокая · **Категория:** Баг / Гонка · **Достоверность:** Вероятно (механизм подтверждён по коду; нужен тайминг)
- **Где:** `src/LandingStats.App/Runtime/RecordingRuntimeOwner.cs:95, 103, 107-114, 164-171, 190-197`; на стыке — `DurableRuntimeDispatcher.cs:126, 174-188`, `RollingRuntimeJournal.cs:211`
- **Описание:**
  - Ожидающий checkpoint (`_checkpoint`) хранится в полях владельца и переживает и restart, и restore; `lastCheckpointAt` при restore не сбрасывается.
  - После восстановления на C0 старый запрос C1 > C0 остаётся в `DurableRuntimeDispatcher._checkpoint`: при `_consumerFailure` dispatcher падает раньше, чем успевает его обработать. После перезапуска dispatcher сохраняет C1.
  - Владелец тем временем разбирает backlog (до 2048 входов) с cursor c < C1. Интервал 5 с уже истёк, и он сразу запрашивает checkpoint(c); проверки `state.Cursor > _lastCheckpoint` нет.
  - `RollingRuntimeJournal.SaveCheckpoint` бросает «Checkpoint cannot move backwards». Запрос c из `_checkpoint` не удаляется, и каждый следующий запуск dispatcher падает на нём.
- **Сценарий:** запрошен checkpoint C1 → в пределах ~250 мс падает владелец или dispatcher (например, I/O-ошибка при append) → restore с C0 → C1 записан → первый вход из backlog с cursor < C1 → бесконечный crash-loop. Новые входы не коммитятся, барьеры не завершаются, буферы воркеров переполняются.
- **Направление:** при restore отбрасывать или дожидаться чужого ожидающего checkpoint; не запрашивать checkpoint с cursor ≤ последнего сохранённого; dispatcher должен завершать или отбрасывать устаревший запрос.

### C-02. NaN или выход за диапазон координат «отравляет» журнал записи
- **Серьёзность:** Высокая (последствия критические) · **Категория:** Баг · **Достоверность:** Подтверждено
- **Где:** `src/LandingStats.App/Runtime/RemoteSceneryQueryPort.cs:57-63`; `src/LandingStats.Runtime.Contracts/SimulatorMessages.cs:13-15`; вызовы — `src/LandingStats.App/Telemetry/EpisodeFacilityCoordinator.cs:103, 121`; legacy-проверка — `SimConnectLandingRecorder.Runways.cs:130-135` в HEAD
- **Описание:**
  - `Request` без проверки создаёт `SceneryCommandMessage(..., latitude, longitude)`, а его конструктор бросает `ArgumentException` при NaN, |lat| > 90 или |lon| > 180.
  - Legacy `RequestRunwaysAt` такие координаты отсеивал и мягко завершал стадию пустыми facilities. Core тоже считает их возможными (`TelemetryKinematicDiscontinuity.PositionAvailable`), а воркер координаты не санитизирует.
  - Исключение возникает внутри `Apply` для durable-кадра, и каждый replay детерминированно его повторяет: владелец → `InvalidateConsumer` → restore → тот же кадр.
  - Запись мертва до конца сессии; `ColdRecordingRecovery.Recover` тоже падает на этом входе.
- **Сценарий:** кадр начала захода или последний кадр контакта с NaN-позицией (сбой самолёта или аддона) → `RequestPreliminary`/`RequestFinal` → `ArgumentException` → бесконечный цикл восстановления.
- **Направление:** проверять координаты в `Request` и при невалидных завершать стадию пустым результатом, как в legacy; по возможности санитизировать в воркере. Общая проблема — C-05.

### C-03. Эпизоду присваивается самолёт предыдущего полёта (регрессия к legacy)
- **Серьёзность:** Средняя · **Категория:** Баг · **Достоверность:** механизм подтверждён; частота зависит от порядка ответов SDK — Вероятно
- **Где:** `src/LandingStats.App/Telemetry/LandingEpisodeCoordinator.cs:49-63, 112, 345`; `LandingEpisodeCoordinator.Lifecycle.cs:168-187`; legacy — `SimConnectLandingRecorder.cs:1004-1012` в HEAD; замысел — `docs/runtime-refactoring-plan.md:442`
- **Описание:**
  - Идентичность самолёта снимается в `StartEpisode` из текущих `_aircraftTitle/Type/Model`. Позже заполняются только неизвестные поля, а другой известный самолёт отвергается молча, без записи в журнал.
  - Текущие метаданные сбрасываются в Unknown только при `SourceInterrupted` (обрыв соединения). При смене полёта внутри соединения (FLT_LOAD, конец полёта, телепорт) этого не происходит, хотя по плану метаданные принадлежат контексту flight/connection.
  - Legacy брал имя при завершении эпизода, то есть не меньше 15 с спустя.
  - Неверный title ведёт к неверной или отсутствующей cfg-геометрии в анализе.
- **Сценарий:** посадка на самолёте A → новый полёт на самолёте B со стартом на коротком финале (< 500 ft) → первый кадр открывает эпизод с A, потому что кадр приходит раньше имени (ср. LS-015) → ответ с B отвергнут как несовместимый.
- **Направление:** сбрасывать идентичность в pending/unknown на границах flight segment (Loading/Ended/Teleport) и журналировать отвергнутый конфликт.
- **Подтверждено направлением нативных источников:**
  - И воркер, и родитель публикуют и сбрасывают Unknown только при `Connected` и `SourceInterrupted`.
  - На flow-границах (`BACK_TO_MAIN_MENU`, `FLT_LOAD(ED)`, `FLIGHT_START`) метаданные не сбрасываются и заново не запрашиваются. Запрос идёт с `SECOND|CHANGED`, новый title приходит асинхронно: по замерам LS-015 первый кадр через 0,27 с, имя через 1,14 с.
  - Направление дополняется: на flight-boundary публиковать `AircraftMessage(Unknown)` и запрашивать metadata `ONCE`.

### C-04. После restore `_restoredQuerySnapshot` заслоняет живой прогресс поиска facilities
- **Серьёзность:** Средняя · **Категория:** Баг / детерминизм · **Достоверность:** Подтверждено
- **Где:** `src/LandingStats.App/Telemetry/EpisodeFacilityCoordinator.cs:14-15, 131, 148, 159-162, 183-188, 214`; `EpisodeFacilityCoordinator.Checkpoint.cs:48`; `src/LandingStats.App/Runtime/RemoteSceneryQueryPort.cs:105-108`; `RecordingRuntimeState.cs:345`
- **Описание:**
  - После restore порт восстанавливает свой `_snapshot`, а координатор — отдельную копию `_restoredQuerySnapshot`, которую `QuerySnapshot` возвращает до завершения или сброса стадии.
  - Промежуточные трансферы обновляют только снимок порта. Поэтому при завершении стадии отменой или по 60-секундному deadline используется снимок на момент checkpoint.
  - В `FailCurrentLookupStage` runways берутся из устаревшего снимка, а airports и флаги полноты — из живого.
  - Результат зависит от того, был ли restore. Тест «restore после каждого сообщения» использует один завершённый chunk и этот случай не ловит.
- **Сценарий:** preliminary lookup → трансфер №1 → checkpoint → рестарт владельца → трансфер №2 с runways → контакт → `PreservePreliminaryFacilities` берёт №1, и preliminary runways теряются.
- **Направление:** не держать отдельную копию, когда порт восстанавливается из того же checkpoint, либо сбрасывать её при любом обновлении порта.

### C-05. Любое исключение в редьюсере делает вход «ядовитым»: нет quarantine, ожидания зависают
- **Серьёзность:** Средняя · **Категория:** Архитектура · **Достоверность:** Подтверждено (механизм)
- **Где:** `src/LandingStats.App/Runtime/RecordingRuntimeState.cs:126, 198, 211-218, 223, 258`; `RemoteSceneryQueryPort.cs:92, 96, 99`; `RecordingRuntimeOwner.cs:72-85, 177-188`; на стыке — `ApplicationRuntime.cs:158`, `ColdRecordingRecovery.cs:72`
- **Описание:**
  - Нарушения протокола и инвариантов источника внутри `Apply` (порядок трансфера, хеш, handshake, отсутствие join и т. п.) бросают исключение на durable-входе, и replay повторяет его бесконечно. План (раздел 5) требует иного: невосстановимая операция завершается с причиной или уходит в quarantine, а компонент продолжает работу.
  - `WaitForCheckpointAsync` и `RestoreAsync` не имеют отмены и не пробрасывают отказ владельца. Поэтому барьер Stop или retirement за «ядовитым» входом не завершится, и `StopOwnersAsync` и retire источников зависают.
  - Сейчас конкретный реальный триггер — C-02; остальные пути требуют ошибки в воркере.
- **Сценарий:** воркер присылает трансфер с неверным хешем → `InvalidDataException` → crash-loop; shutdown вечно ждёт барьер Stop.
- **Направление:** различать ошибки данных источника (диагностика, retire или gap этого источника) и внутренние инварианты; ввести quarantine входа; таймаут или отказ для ожиданий.

### C-06. Диагностика воркера попадает в журнал чужого эпизода (регрессия к legacy)
- **Серьёзность:** Средняя · **Категория:** Сопровождаемость / диагностика · **Достоверность:** Подтверждено
- **Где:** `src/LandingStats.App/Runtime/RecordingRuntimeState.cs:271`; `src/LandingStats.App/Telemetry/LandingEpisodeCoordinator.cs:46`; `src/LandingStats.App/Runtime/SimulatorWorkerRole.cs:93-98, 148`; `src/LandingStats.App/Telemetry/SimConnectSource.Runways.cs:83-87, 579-589`; `SimConnectSource.cs:344`; legacy — `…Runways.cs:145-150, 723-739`, `SimConnectLandingRecorder.cs:565` в HEAD
- **Описание:**
  - Все `SourceDiagnosticMessage` пишутся в `DecisionJournal` эпизода, активного в момент доставки, а воркер публикует их таймером с задержкой до 50 мс.
  - Диагностика финального lookup (final_requested, airport_candidates, request_*, base_geometry) приходит после `CompleteEpisode` и попадает в `_pendingLifecycleJournal`. Оттуда она уходит в журнал следующей посадки или теряется при сбросе сегмента, но не попадает ни в журнал своего эпизода, ни в `RuntimeFinalFacilities.Decisions`.
  - `simulator_exit` тоже доходит уже после завершения эпизода. В legacy обе записи попадали в журнал своего эпизода.
- **Направление:** маршрутизировать диагностику по `episodeId`/`lookupId` в журнал facilities, а прочую — по времени кадра.
- **Подтверждено направлением нативных источников:** финальный lookup запрашивается в `CompleteEpisode` уже после `_episodeDecisionJournal = null`, а `SourceDiagnosticMessage` не несёт episode/lookup в envelope. В архиве следующей посадки оказываются чужие записи `runway.requests` с прежним `episode_id`.

### C-07. Checkpoint каждые 5 с заново сериализует весь эпизод (квадратичный рост записи)
- **Серьёзность:** Средняя · **Категория:** Производительность · **Достоверность:** стоимость — Подтверждено; переполнение воркера — Вероятно. Подтверждено двумя направлениями (координаторы записи; форматы Core).
- **Где:** `src/LandingStats.App/Runtime/RecordingRuntimeOwner.cs:42, 152, 163-171`; `LandingEpisodeCoordinator.Checkpoint.cs:17, 26`; `RecordingRuntimeState.cs:292-313`; `ControllerJoiner.cs:191`; `ControllerProvenanceCodec.cs:34-43`; на стыке — `DurableRuntimeDispatcher.cs:127, 180`, `RollingRuntimeJournal.cs:208-222`, `WorkerChannel.cs:13`, `ApplicationRuntime.cs:47-48`
- **Описание:**
  - `dirty` выставляется после любого входа, включая тики и снимки контроллеров на 100 Гц. Поэтому checkpoint пишется каждые 5 с всегда, в том числе в простое.
  - Каждый раз заново сериализуются все `_episodeSamples` — около 2 КБ на кадр (1869 Б кадра + 129 Б evidence), до ~130 МБ при 65 536 кадрах. Объём записи за эпизод растёт квадратично; в худшем случае (300 с × 60 Гц) это порядка 1 ГБ записи и чтения за заход.
  - По пути делается несколько копий в LOH (MemoryStream, ToArray, внешний буфер, `Clone` в dispatcher), затем SHA-256, durable-запись и повторное чтение для проверки.
  - Каждый кадр несёт собственную копию закодированного каталога контроллеров (`RuntimeWire.Encode(Catalog)`), которая хешируется при каждом checkpoint. `prepare` в `ApplicationRuntime` десериализует всё состояние ради joiner.
  - Сохранение идёт синхронно в единственном цикле журнала: коммиты и ACK стоят, а у воркера лимит 1024 неподтверждённых сообщения (≈ 8 с кадров).
- **Сценарий:** долгий заход на высоком FPS (300 с × 144 к/с ≈ 43 тыс. кадров) → checkpoint ~86 МБ каждые 5 с, ~35 МБ/с записи и чтения на фоне MSFS. На медленном диске или под антивирусом это ведёт к переполнению simulator worker, его рестарту и обрыву посадки.
- **Направление:** инкрементальный или append-only checkpoint эпизода (сэмплы и так есть в WAL), checkpoint по событиям и объёму, общий буфер каталога, сохранение вне цикла коммитов.
- **Дополнительно (направление runtime):** тик каждые 500 мс держит состояние «dirty» и в простое. Поэтому каждые 5 с в checkpoint попадают 512 снимков контроллеров (~330 КБ), а при подключённом симуляторе ещё и pre-roll за 15 с. Объём записи от источников описан в A-04.

### C-08. Устаревший признак «в воздухе» навсегда блокирует `SafeToRestart`
- **Серьёзность:** Средняя · **Категория:** Баг · **Достоверность:** Подтверждено
- **Где:** `src/LandingStats.App/Runtime/RecordingRuntimeState.cs:106-107`; `LandingEpisodeCoordinator.cs:91-113, 196-209`; `RecordingRuntimeOwner.cs:160-163`; на стыке — `src/LandingStats.ProcessControl/GuardianPolicy.cs:33`
- **Описание:** `_aircraftOnGround` обновляется только по Running/Legacy-кадрам. Ни `SourceInterrupted`, ни конец полёта его не сбрасывают, потому что кадры Inactive выходят из `ProcessSample` раньше. После выхода в меню или закрытия MSFS в воздухе `IsAircraftAirborne` остаётся true, а `SafeForPresentationRestart` — false до следующего полёта на земле.
- **Сценарий:** выход из полёта в главное меню на эшелоне → UI зависает → guardian бесконечно ждёт безопасной точки (`WaitForSafePoint`) и не перезапускает приложение. Тестами этот путь не покрыт (T-03).
- **Направление:** сбрасывать ground state в unknown при disconnect и Ended/Loading и учитывать unknown в формуле безопасности.

### C-09. Логика стадий scenery продублирована трижды; воркер и родитель рассинхронизируются
- **Серьёзность:** Низкая · **Категория:** Архитектура · **Достоверность:** Подтверждено (влияние ограничено)
- **Где:** `src/LandingStats.App/Runtime/SimulatorWorkerRole.cs:68-72, 153-158`; `LandingEpisodeCoordinator.Lifecycle.cs:125-128`; `RemoteSceneryQueryPort.cs:73-74`; `ISimulatorSourceContext.cs:39-47`
- **Описание:** стадии lookup живут в трёх местах: `SceneryObserver` воркера, `RemoteSceneryQueryPort` и `EpisodeFacilityCoordinator`. Воркер отменяет lookup на любом Flow-событии и не сообщает об этом родителю. Родитель на FLIGHT_START (вне Ended) продолжает ждать до 60 с и затем фиксирует частичные данные.
- **Сценарий:** старт полёта сразу на заходе → preliminary-запрос → FLIGHT_START → воркер отменил lookup → родитель держит запрос 60 с, preliminary-данные потеряны.
- **Направление:** явный terminal outcome от воркера при отмене; убрать дублирование.

### C-10. «Осиротевшие» барьеры после replay
- **Серьёзность:** Низкая · **Категория:** Утечка ресурсов · **Достоверность:** Подтверждено (вероятность низкая)
- **Где:** `src/LandingStats.App/Runtime/RecordingRuntimeOwner.cs:79, 141-151`
- **Описание:** `Apply` создаёт `Barrier` для каждого retirement/Stop, а удаляет его только `WaitForCheckpointAsync`. Retirement, повторно применённые после restore, создают барьеры, которых никто не ждёт. При 128 записях бросается исключение — в цикле владельца или у нового ожидающего.
- **Направление:** удалять завершённые барьеры без ожидающих (или по TTL).

### C-11. Мёртвый и наполовину подключённый код
- **Серьёзность:** Низкая · **Категория:** Сопровождаемость · **Достоверность:** Подтверждено
- **Где:**
  - `RemoteSceneryQueryPort.cs:79-84` — `ResumeExisting` вызывается только тестами; комментарий в `EpisodeFacilityCoordinator.Checkpoint.cs:65-67` описывает несуществующий путь;
  - `RecordingRuntimeState.cs:43-50` — `LocalOnly` не используется;
  - `LandingEpisodeCoordinator.cs:38, 68, 86-89` — `DetectionReset`, `ResetPreRoll`, `Disconnect()` нужны только тестовому фасаду;
  - `EpisodeFacilityCoordinator.cs:25` — `StageCompleted`.
- **Описание:** тесты проверяют поведение, которого нет в production (P7 «Resume-existing»); см. также T-02.

### C-12. Изменился формат `ControlInputSources`
- **Серьёзность:** Низкая · **Категория:** Совместимость · **Достоверность:** Подтверждено
- **Где:** `src/LandingStats.App/Models/ControllerCatalogTimeline.cs:73-79`; `RecordingRuntimeState.cs:264`; legacy — `SimConnectLandingRecorder.cs:1291-1301` в HEAD; потребители — `RawChunkStore.cs:131-132`, `BugReportRepository.cs:249`
- **Описание:** теперь это всегда 8 слотов с пустыми строками и текст `"{Name} [WinMM id]"` вместо списка подключённых устройств `"winmm:id: …"`. `control_input_source_count` в архивах и bug reports всегда равен 8. Нужна сверка с серверными парсерами и dashboard.

### C-13. Залипший статус Recording в legacy-режиме (LS-017)
- **Серьёзность:** Низкая · **Категория:** Баг · **Достоверность:** Подтверждено
- **Где:** `src/LandingStats.App/Telemetry/LandingEpisodeCoordinator.cs:211-223`
- **Описание:** при `_sdkStateKnown == false` откат sim time без контакта вызывает `DiscardEpisode` без публикации статуса, и индикатор остаётся в Recording. Окно узкое: до первого lifecycle-сообщения после connect.

### C-14. Ловушки детерминизма
- **Серьёзность:** Низкая · **Категория:** Сопровождаемость · **Достоверность:** Подтверждено
- **Где:** `src/LandingStats.App/Telemetry/RecorderEvents.cs:55`; `EpisodeFacilityCoordinator.Checkpoint.cs:16`; `IEpisodeFacilityPort.cs:13-14`
- **Описание:**
  - Fallback `Guid.NewGuid()` для `PersistentId`: сейчас все пути редьюсера передают ID, но любой новый путь молча станет недетерминированным.
  - `CaptureCheckpoint` мутирует состояние через `PreservePreliminaryFacilities`.
  - Write-only `DecisionJournal`/`DecisionTime` создают скрытую временную связанность.

### Проверено в этом направлении, проблем не найдено
- Константы гейтов не изменились: 500 ft / −0,5 fps, pre-roll и post-contact по 15 с, 1000 ft, 300 с, 3000/3500 ft, 4096/65536.
- `ProcessSample`, `StartEpisode`, `AppendEpisodeSample`, `CompleteEpisode`, `DiscardEpisode` и batching контактов совпадают с legacy, кроме намеренных изменений. Lifecycle (pause, SimRunning, flow, replay, resume clock, сегменты) перенесён построчно.
- Checkpoint координатора, `PreRollBuffer` и `RejectedContactBatcher` полные; инварианты валидируются.
- В редьюсере нет wall clock и случайных ID; дубликаты эпизодов исключены.
- Семантика gap соответствует плану: контакт сохраняется, заход уходит в diagnostic, первая земля после восстановления посадку не создаёт.
- Корреляция facilities и 60-секундный deadline в порядке; все `TaskCompletionSource` завершаются на каждом пути.
- Chunked transfer: индексы, длины, SHA-256 и лимиты проверяются.
- `ControllerJoiner`: снимок не позже кадра, возраст по слоту, TTL 250 мс, поколения и ревизии каталогов.
- LS-015 (заполнение неизвестных полей) и LS-017 (публикация статуса при завершении) реализованы.

---

## 7. Форматы данных Core и геометрия самолёта

Совпадающие находки объединены: `ArgumentException` декодера source_state → P-02; неиспользуемый `TelemetryDownsampler` → S-03; стоимость checkpoint и копия каталога в каждом кадре → C-07.

### E-01. Новый joiner пишет `NaN` в столбцы возраста осей контроллера — сервер отклоняет архив целиком
- **Серьёзность:** Высокая · **Категория:** Потеря данных (доставка) · **Достоверность:** Подтверждено
- **Где:** `src/LandingStats.App/Runtime/ControllerJoiner.cs:189-212` (строка 198: `age = captured == 0 ? double.NaN : …`); `src/LandingStats.Core/TelemetryCsv.cs:138-156, 1008-1012`; `server/telemetry-ingest/app/validation.py:497-502`; `server/telemetry-ingest/app/main.py:400-402`; `src/LandingStats.App/TelemetryUpload/TelemetryUploadClient.cs:681-690`; `src/LandingStats.App/Telemetry/WindowsJoystickReader.cs:76, 82`; `src/LandingStats.App/Runtime/ControllersWorkerRole.cs:15, 38-44`
- **Описание:**
  - В 0.9.4 неудачный опрос оставлял age = 0.
  - Теперь `ControllerJoinDecision.Apply` для устройства из каталога с `CapturedAt(slot) == 0` пишет `double.NaN` во все 6 осей слота и в `controller_N_raw_y_age_s`, и `TelemetryCsv.Format` выводит `NaN`.
  - Сервер допускает нечисловое значение только в `analysis_time_s`: `ValueError("… is not finite")` → 422 → клиент отправляет архив в карантин без повтора.
  - Локальная история не страдает (NaN хранится как sentinel), но автоматический отчёт или RAW-чанк не доставляется.
- **Сценарий:** каталог обновляется раз в 5 с, а `ReadInto` ставит `capturedAt[slot] = 0` при любом отказе `joyGetPosEx`. Если на заходе геймпад уснул, USB-устройство отключили или драйвер кратко вернул ошибку, кадры до следующего перечисления получают NaN-age, и архив посадки отклоняется. Серверные тесты строят строки из нулей, а физический hotplug не проверялся (LS-013).
- **Направление:** писать конечное значение при valid = 0, как раньше; либо сначала расширить серверную схему и только потом выпускать writer.

### E-02. Snapshot геометрии v2 делает историю нечитаемой для опубликованной 0.9.4 (откат и второе устройство)
- **Серьёзность:** Высокая · **Категория:** Баг / Совместимость · **Достоверность:** Подтверждено (по коду HEAD и текущему writer)
- **Где:**
  - текущий writer: `src/LandingStats.App/Models/AnalysisGeometrySnapshot.cs:16, 31, 49, 81-98`; `src/LandingStats.Core/TelemetryBinaryCodec.cs:14`; `src/LandingStats.App/Runtime/EpisodeProcessor.cs:77, 90`;
  - HEAD/0.9.4: `AnalysisGeometrySnapshot.cs:73, 81-83` (`Version != 1` → throw; `Copy()` вызывает `Validate()`), `LandingRepository.cs:565`, `LandingRecordFile.cs:88`, `MainWindow.History.cs:115`;
  - фикстура отката: `artifacts/runtime-refactor-20261016/HistoryCompatibility.cs:27-41`;
  - тот же паттерн в текущем коде: `LandingRepository.cs:587`, `LandingRecordFile.cs:192`.
- **Описание:**
  - Каждая новая запись получает `AnalysisGeometry` Version = 2 (`telemetry_binary64_v1_csv7`), который сериализуется и в detail, и в индексе.
  - 0.9.4 загружает историю через `LoadAll().Select(CreateSummary)` → `Copy()` → `Validate()` → `InvalidDataException`. В результате не показывается вся история. Любая перезапись индекса в 0.9.4 тоже бросает исключение, так что 0.9.4 не может сохранить даже свою новую посадку.
  - Утверждение P11 «P0 reader прочитал историю нового writer» опирается на фикстуру без `AnalysisGeometry`.
  - Текущий код сохраняет ту же строгость при копировании summary, поэтому будущая версия snapshot сломает и эту версию.
- **Сценарий:** обновиться, выполнить посадку, вернуться на 0.9.4 → журнал не загружается. Вероятно, второе устройство на 0.9.4 при синхронизации через Google Drive тоже не сможет импортировать эти записи.
- **Направление:** явно ограничить downgrade или выпустить bridge-читатель v2; неизвестную версию snapshot в summary считать «недоступно», а не исключением; добавить в фикстуру отката реальный snapshot (LS-002).
- **Подтверждено направлением истории** (оно оценило находку как Критичную для отката и ПК со смешанными версиями):
  - На втором ПК с 0.9.4 `Import` из Drive бросает «invalid or unsupported». Изоляции по посадкам нет (G-02), поэтому синхронизация этого ПК падает целиком при каждом запуске.
  - В клиенте снимок нигде не используется (`Restore` в src не вызывается), так что строгая валидация добавляет только хрупкость.
  - Направление дополняется: писать v2 в новый DataMember (старый `AnalysisGeometry` оставлять null/v1) и не класть снимок в индекс.

### E-03. Полный холодный обход каталога самолётов (12–15 с) на каждую посадку
- **Серьёзность:** Средняя · **Категория:** Производительность / Архитектура · **Достоверность:** Подтверждено (по коду); длительность — из измерений плана
- **Где:** `src/LandingStats.App/Runtime/AnalysisWorkerRole.cs:17, 27, 70`; `RetainedAnalysisExecutor.cs:71`; `src/LandingStats.App/Telemetry/FlightModelGeometryResolver.cs:111-122, 273-301, 317-353, 1194-1260, 1386-1396`; `docs/runtime-refactoring-plan.md:158`
- **Описание:**
  - Каждый анализ запускает новый процесс WorkerHost и новый `FlightModelGeometryResolver()`. Конструктор стартует полный scan, а `ResolveAsync` его ждёт, поэтому кеш на 5 мин / 10 с в production не действует.
  - Обход — DFS глубиной до 32 по всем каталогам пакетов, включая texture/model.
  - Каждый flight_model.cfg читается дважды, а общий базовый cfg разбирается заново для каждой ливреи.
  - Геометрия не фиксируется во входах эпизода: повтор после сбоя job резолвит её по уже изменившейся файловой системе.
  - Класс на 1675 строк смешивает обнаружение путей, семантику VFS/merge MSFS, INI-парсинг, вывод топологии и сборку options.
- **Сценарий:** после касания и 15-секундного rollout отчёт появляется ещё на 12–15 с позже, с интенсивным чтением диска рядом с работающим MSFS. Гипотеза: на HDD с большой Community-папкой обход приближается к дедлайну анализа в 180 с.
- **Направление:** персистентный индекс с инвалидацией по mtime; разрешённая геометрия как неизменяемый вход job; обход только по SimObjects/presets; мемоизация разбора cfg.
- **Подтверждено направлением runtime:** в 0.9.4 каталог жил всё время работы приложения. Теперь каждый analysis-job — новый процесс с холодным сканом, который расходует тот же дедлайн 180 с (см. B-05).

### E-04. Признак модульного самолёта ищется вверх до корня диска, а не до корня SimObject
- **Серьёзность:** Средняя · **Категория:** Баг · **Достоверность:** Подтверждено (по коду); частота такой конфигурации — Вероятно
- **Где:** `src/LandingStats.App/Telemetry/FlightModelGeometryResolver.cs:483-501, 1068-1084` (`FindModularSimObjectRoot`)
- **Описание:** любой предок с именем `common`/`presets`/`attachments` выше пакета делает немодульный aircraft.cfg «модульным». Модульная ветка игнорирует `base_container`, поэтому ливреи без своего flight_model.cfg получают `flight_model_unavailable`. Немодульный самолёт с `attached_objects.cfg` может уйти в fail-closed.
- **Сценарий:** пакеты лежат по пути вида `D:\SteamLibrary\steamapps\common\…\Packages` → все ливреи на base_container молча уходят в телеметрийный fallback, и покрытие LS-001 падает.
- **Направление:** останавливать подъём на `FindSimObjectRoot`.

### E-05. Алгоритмы fingerprint не заморожены и не специфицированы
- **Серьёзность:** Средняя · **Категория:** Архитектура / Сопровождаемость · **Достоверность:** Подтверждено (тестовая сторона — T-06)
- **Где:** `src/LandingStats.Core/TelemetryBinaryCodec.cs:117-136`; `src/LandingStats.App/Models/AnalysisGeometrySnapshot.cs:102-109`; `tests/LandingStats.App.RegressionTests/BinaryTelemetryRegressionTests.cs:83`; `tests/LandingStats.Runtime.RegressionTests/Program.cs:14`; `docs/format-v7.md:68-76`
- **Описание:**
  - v2 использует тот же `Write()`, что и IPC/WAL-кодек, включая `Version` кодека в каждом сэмпле. Новое поле или bump payload молча меняет все хеши при прежнем `FingerprintFormat`.
  - v1 считается через живой `TelemetryCsv.Format`, поэтому CSV v8 сломает проверку всех v1 snapshot.
  - Байтовый формат v2 задан только кодом; format-v7.md описывает только v1.
- **Сценарий:** P9 добавляет канал → все сохранённые v2 snapshot при Restore дают «belongs to another physical segment», а тесты остаются зелёными (LS-002).
- **Направление:** отдельная замороженная каноническая сериализация (и замороженный форматтер для v1), зафиксированный хеш в тестах, раздел в format-v7.md.

### E-06. В replay-like сегментах причина подменяется на `KinematicEvidence`, счётчики искажены (LS-008)
- **Серьёзность:** Средняя · **Категория:** Вводящая в заблуждение диагностика · **Достоверность:** Подтверждено
- **Где:** `src/LandingStats.Core/ReplaySourceAssessment.cs:30-35, 47, 53-55`; `src/LandingStats.App/Runtime/EpisodeProcessor.cs:116-131`
- **Описание:**
  - `Unknown()` при `suspiciousKinematics` заменяет Stale/Unavailable/ClockDiscontinuity на `KinematicEvidence`. Поэтому `stale_observation` = 0 ровно там, где LS-008 должен отделять несвежие наблюдения.
  - При mask = 7 без движения возвращается `Unknown/AwaitingMovement`, при mask = 0 — `SuspiciousKinematics`.
  - Обычный кадр без freeze помечается `PartialFreeze`.
- **Сценарий:** Sky Dolly со старыми наблюдениями → журнал показывает stale = 0, suspicious = N.
- **Направление:** хранить исходную причину отдельно от вида оценки.

### E-07. `Decode` пропускает значения, которые CSV v7 и сервер отвергают
- **Серьёзность:** Низкая · **Категория:** Валидация · **Достоверность:** Гипотеза (источник маловероятен)
- **Где:** `src/LandingStats.Core/TelemetryBinaryCodec.cs:26-108` против `TelemetryCsv.cs:736-742`; `src/LandingStats.Runtime.Contracts/RuntimePayload.cs:58-64`; `LandingEpisodeCoordinator.Lifecycle.cs:209`
- **Описание:** `Decode` проверяет только bool и SimulatorState. `AnalysisTimeSeconds = ±Inf`, `PauseFlags < -1`, `FlightSegment < 0` проходят, хотя `TelemetryFrameMessage` использует `Decode` как фильтр некорректных значений. Из такого сэмпла получается CSV, который не читает собственный `ReadFile` и отвергает сервер.

### E-08. `Sequence` — счётчик экземпляра helper-процесса, а не общий счётчик родителя
- **Серьёзность:** Низкая · **Категория:** Интерфейс / Баг · **Достоверность:** Вероятно
- **Где:** `src/LandingStats.App/Telemetry/SimConnectSource.cs:62, 653`; `RecordingRuntimeState.cs:257-264`; `LandingEpisodeCoordinator.cs:91-113`; `src/LandingStats.App/Storage/RawCaptureRepository.cs:16`; `server/telemetry-ingest/app/validation.py:476-481`
- **Описание:** вопреки плану (§4.2) родитель не переназначает `Sequence`, и после замены simulator-helper нумерация начинается с 0. `SourceInterrupted` не останавливает ручной RAW, а RAW режется на чанки только по числу строк (30 000).
- **Сценарий:** RAW-чанк, пересекающий замену helper, содержит немонотонный `sequence` → 422. Автоэпизоды не затронуты: на разрыве они сбрасываются.

### E-09. Покрытие и устойчивость каталога самолётов (гипотезы)
- **Серьёзность:** Низкая · **Категория:** Баг / Устойчивость · **Достоверность:** Гипотеза
- **Где:** `src/LandingStats.App/Telemetry/FlightModelGeometryResolver.cs:282, 355-380, 1194-1260, 1262-1299, 1412-1445`
- **Описание:**
  - Корни MSFS 2020 и 2024 сканируются вместе без учёта подключённого симулятора. Одинаковый title или base_container с разной геометрией даёт `ambiguous_aircraft_geometry`.
  - Пакеты с content_type не AIRCRAFT/LIVERY (например, MISC у части ливрейных паков) отсекаются.
  - Нумерация `point.N` уплотняется, поэтому пропуск в cfg может сдвинуть индексы относительно SimConnect.
  - Завершившийся с ошибкой `_catalogTask` не восстанавливается (сейчас — в пределах одного job).
  - Посещённые каталоги различаются по строке, а не по reparse point. Цикл из двух junction ограничен только глубиной 32 — обход экспоненциальный.

### E-10. Обрезка строк журнала решений может оставить одиночный суррогат; пустые поля сервер отвергает
- **Серьёзность:** Низкая · **Категория:** Баг · **Достоверность:** Гипотеза (триггеры маловероятны)
- **Где:** `src/LandingStats.Core/TelemetryDecisionJournal.cs:119-120`; `src/LandingStats.App/Runtime/RuntimeCheckpointCodec.cs:13-19`; `server/telemetry-ingest/app/validation.py:205-206, 233-241`
- **Описание:** `Bound()` режет по UTF-16 и может оставить одиночный суррогат. Тогда строгий UTF-8 в `WriteText` бросит `EncoderFallbackException` при checkpoint координатора — возможна петля перезапуска (см. C-05). `Bound(null)` возвращает "", а сервер требует непустые component/code/reason/keys, иначе 422 на весь архив.

### Проверено в этом направлении, проблем не найдено
- `TelemetryBinaryCodec`: Encode/Decode согласованы (1869 Б, LE, строгие bool/state, отказ по длине и версии); fingerprint побайтно не зависит от CLR и культуры; −0 и NaN канонизируются.
- G17 на CLR 4.8: на 150 000 значений — 0 ошибок roundtrip и 0 расхождений с IEEE-разбором Python. Расхождения LS-002 в 17 архивах объясняются эпохой формата «R» (до 0.7.2).
- `TelemetryCsv`: везде Invariant; заголовки v5/v6/v7 побайтно совпадают с `server/telemetry-ingest/schema-v*.header`; недостающие поля v7 читаются как unknown.
- `source-state.bin`: 128 Б совпадают с Python-структурой; есть границы, проверка хвоста и ограничение base64 до аллокации; старые архивы читаются с evidence = null.
- LS-004 (8192 записи, оценка 3,6 МБ), LS-006 (datum применяется ровно один раз) и барьеры сегментов в Core-логике — в порядке.
- Resolver: `_catalogTask`, пути и время меняются под одной блокировкой; ошибка одного самолёта не ломает остальные.

---

## 8. WPF-оболочка и настройки

Находка «любой refresh истории перерисовывает выбранную посадку с нуля» совпала с R-02 и объединена с ней.

### U-01. Цикл полной перезагрузки истории после рестарта cloud-owner
- **Серьёзность:** Высокая · **Категория:** Баг / Производительность · **Достоверность:** Подтверждено
- **Где:** `src/LandingStats.App/MainWindow.Runtime.cs:79-84, 100-111, 127-133`; `src/LandingStats.App/Runtime/RuntimePresentation.cs:89-97`; `src/LandingStats.App/Runtime/CloudSyncRuntimeOwner.cs:143, 184-186`
- **Описание:**
  - Перезагрузку запускает условие `snapshot.Cloud.HistoryRevision != _runtimeCloudHistoryRevision`, а новое значение сохраняется только внутри `if (cloudChanged)`, то есть при смене `Revision`.
  - `CloudSyncRuntimeOwner.RunAsync` на старте каждого поколения публикует уведомление с `historyChanged: true` и `HistoryRevision == Revision`. `RuntimePresentation.Cloud` принимает его с тем же `Revision` и поднимает `HistoryRevision`.
  - Если последняя синхронизация не меняла историю (обычный случай), после рестарта owner счётчики расходятся навсегда.
  - После этого каждый snapshot выполняет `LoadAll` под `_landingRepositoryGate`, пересобирает `_landings`, инвалидирует детали и заново показывает выбранную запись со сбросом 3D и графиков (R-02). Так продолжается до следующей успешной синхронизации.
- **Сценарий:** Drive подключён, фоновая синхронизация прошла без изменений истории. Затем пропала сеть, Drive вернул 5xx или не удалось записать settings → supervisor перезапускает owner. Без сети owner снова падает и перезапускается с backoff до 30 с, и сброс выбранной посадки повторяется весь сеанс.
- **Направление:** применять `HistoryRevision` при любом обработанном snapshot; не считать повторную публикацию сохранённого состояния изменением истории.

### U-02. Завершение сеанса Windows обходит drain
- **Серьёзность:** Средняя · **Категория:** Потеря данных (риск) / Архитектура · **Достоверность:** Подтверждено по коду; поведение WPF при WM_QUERYENDSESSION — документированная семантика
- **Где:** `src/LandingStats.App/App.Guardian.cs:79-84`; `MainWindow.Shutdown.cs:19-37, 56-84`; `AsyncWindowShutdown.cs:34-41`
- **Описание:**
  - `OnSessionEnding` только подтверждает намерение guardian и вызывает base. Дальше WPF делает `Application.Shutdown()`, и отмена в Closing игнорируется.
  - `CompleteAsync` начинает drain после `await Task.Yield()`, поэтому `DrainWindowShutdownAsync` (`StopRuntimeAsync`, ожидание settings/history/deletion/policy) не запускается вовсе.
  - Каждый logoff с запущенным приложением равен аварии: восстановление идёт только через cold recovery.
  - Дополнительно `GetAwaiter().GetResult()` блокирует UI-поток до 5 с.
  - Пустой drain был и в 0.9.4, но теперь за ним стоит весь runtime, а план (§7.3, матрица P11 «OS shutdown») требует drain.
- **Сценарий:** ПК выключают во время 15-секундного rollout или пока идёт анализ/архив → эпизод завершится только через cold recovery при следующем запуске.
- **Направление:** в `OnSessionEnding` выполнять ограниченный синхронный drain (`StopRuntimeAsync` и pending-задачи) с `ShutdownBlockReasonCreate` до вызова base.
- **Сторона launcher** (подтверждено направлением launcher; `src/LandingStats.App.Launcher/Program.cs:87-88`, `src/LandingStats.ProcessControl/GuardianGeneration.cs:159, 171, 185, 204-213, 217`):
  - На SessionEnding launcher вызывает `supervisor.Stop()`, и тот закрывает pipe. Цикл выходит без `Decide`, а `RetireAsync` сразу делает `TerminateJobObject` без паузы.
  - App без `_intentionalExit` видит разрыв канала и делает `Exit(72)`.
  - Если App получил WM_QUERYENDSESSION первым, его `OnSessionEnding` до 5 с ждёт ack, который остановленный guardian уже не пришлёт.
  - `Task.Delay(Backoff)` не отменяется `Stop()`, поэтому во время паузы перед рестартом launcher висит до 30 с.
  - Направление: на SessionEnding только запрещать рестарт, а текущему поколению давать обычный graceful-путь; сделать паузу отменяемой.

### U-03. Таймаут подтверждения guardian роняет приложение при закрытии и отменяет drain
- **Серьёзность:** Средняя · **Категория:** Баг · **Достоверность:** Подтверждено
- **Где:** `src/LandingStats.App/MainWindow.Shutdown.cs:19-37, 58`; `App.Guardian.cs:64-77`; `src/LandingStats.ProcessControl/GuardianClient.cs:90-105`; `App.xaml.cs:60-63`
- **Описание:**
  - Первая строка drain — `await App.AcknowledgeExitAsync(...)`. `GuardianClient.StopAsync` бросает `TimeoutException`, если launcher жив, но не подтвердил выход за 5 с.
  - Исключение роняет `CompleteAsync` до `CanClose = true`. `async void OnWindowClosing` переподнимает его в диспетчер, а `OnDispatcherUnhandledException` не ставит `Handled` (U-11).
  - Процесс завершается без `_lifetimeCancellation.Cancel()`, без `StopRuntimeAsync` и без ожидания принятых записей.
  - `_stopIntent` закеширован в состоянии faulted, повторить нельзя.
- **Сценарий:** launcher занят (ожидание job, антивирус, отладчик) → пользователь закрывает окно → через 5 с в crash.log появляется «dispatcher TimeoutException», runtime не остановлен.
- **Направление:** считать подтверждение best-effort (лог и продолжение drain); ловить исключения в `OnWindowClosing`.
- **Подтверждено направлением launcher:** событие stop к этому моменту уже взведено, поэтому воскрешения не будет, но принятые записи не дренированы.

### U-04. Сбой старта runtime отключает запись, настройки и самообновление и запускает бесконечный цикл рестартов
- **Серьёзность:** Высокая · **Категория:** Архитектура / Баг · **Достоверность:** подтверждено четырьмя направлениями (WPF-оболочка, runtime, Google Drive, хранение); частота триггеров не измерялась
- **Где:** `src/LandingStats.App/MainWindow.xaml.cs:134-174, 599-602`; `MainWindow.Startup.cs:17-27`; `MainWindow.History.cs:332-344`; `MainWindow.Runtime.cs:43-67`; `src/LandingStats.ProcessControl/GuardianPolicy.cs:10, 29`
- **Описание:**
  - `_startupReady`, кнопка настроек, Drive, кнопка отчёта и инициализация автозапуска ждут `Task.WhenAll(history, runtime)`.
  - Исключение в конструкторе `LandingRuntime` (ownership, квота, engine) или в `StartAsync` оставляет окно без настроек. Нельзя сменить язык, выключить автозапуск с MSFS или телеметрию, хотя runtime для этого не нужен.
  - Ошибка показывается как «Landing analysis failed: …»; Retry есть только для ошибки истории.
  - Под guardian `Ready = false` даёт рестарт App через 2 мин, и при устойчивой причине окно перезапускается бесконечно.
  - LS-016 развязал историю, но не настройки.
- **Сценарий:** антивирус блокирует WorkerHost.exe → история видна, шестерёнка серая, окно перезапускается каждые ~2 мин, а MSFS продолжает автозапускать приложение.
- **Направление:** отделить готовность настроек от готовности runtime; отдельный статус и Retry для runtime.
- **Дополнительно (направления runtime, Google Drive, хранения и launcher):**
  - Связь с проверкой обновлений появилась в рефакторинге. В 0.9.4 `CheckAndInstallAsync` зависел только от загрузки истории; теперь не выполняются ни он, ни стартовая синхронизация Drive. Пользователь не может даже обновиться до исправленной версии.
  - Деградированного режима нет: конструктор `LandingRuntime` падает целиком из-за опциональных подсистем. Триггеры:
    - невалидные или созданные более новой версией `cloud/policy.json`, `Runtime\cloud\sync.json`, `credentials.json` (`IsolatedGoogleDriveApi.cs:53-54`, `CloudSyncRuntimeOwner.cs:106-110`);
    - ошибка `RuntimeSession.Create` на полном диске или при исчерпанной квоте;
    - повреждённая копия engine (B-11).
  - `StartAsync` вызывается без токена и ждёт durable-записи политики в журнал.
  - Сторона guardian того же цикла описана в H-03.
  - Направление дополняется: проверку обновлений и настройки запускать независимо от runtime; облачные и прочие опциональные подсистемы изолировать (карантин файла и старт с отключённым облаком); повторять старт с диагностикой.

### U-05. Ожидающий запрос синхронизации не завершается при отключении облака — Drive блокируется навсегда
- **Серьёзность:** Высокая · **Категория:** Гонка / Баг (регрессия к 0.9.4) · **Достоверность:** логика подтверждена двумя направлениями (WPF-оболочка, Google Drive); вероятность попасть в окно — Вероятно
- **Где:** `src/LandingStats.App/MainWindow.xaml.cs:85, 1118-1155, 1166-1222, 1333`; `src/LandingStats.App/Runtime/CloudSyncRuntimeOwner.cs:112-122, 147-165`; `src/LandingStats.App/Runtime/IsolatedGoogleDriveApi.cs:68-78`
- **Описание:**
  - Запрос UI во время идущей синхронизации становится `_pending`.
  - `SetCloudEnabledAsync(false)` отменяет только текущую эпоху, а цикл owner при `!_api.Enabled` ждёт и `_pending` не завершает.
  - `RunGoogleDriveSyncAsync` висит до `StopIntake`, `_googleDrivePendingOperations` остаётся > 0, и `GoogleDriveBusy` выключает кнопку Connect/Sync. UI зависает в «Syncing…» до перезапуска.
  - В `RunGoogleDriveSyncAsync` остались неиспользуемая переменная `cancellationToken` и устаревший комментарий.
  - Тот же исход при `GoogleDriveNotSignedInException`: invalid_grant на активном батче стирает токен, `Enabled` становится false, а ожидающий UI-запрос продолжает висеть.
  - Если настройки нечитаемы, старт ждёт этот же запрос (`MainWindow.xaml.cs:207-213`), и автозапуск не инициализируется.
  - Это новый путь к симптому п. 3 отчёта 2026-08-29.
- **Сценарий:** на старте идёт фоновая синхронизация; пользователь меняет язык или жмёт «Sync now», а затем «Disconnect» до её окончания.
- **Направление:** при disable отменять `_pending` или не принимать запрос без активной эпохи.

### U-06. Выключение автозапуска не удаляет запись без маркеров
- **Серьёзность:** Средняя · **Категория:** Баг · **Достоверность:** поведение — Подтверждено; частота триггера — Гипотеза
- **Где:** `src/LandingStats.App/Settings/SimulatorAutoStartManager.cs:278-281, 303-321, 427-436`
- **Описание:**
  - Своя запись в exe.xml определяется только по комментариям-маркерам.
  - Если другой инструмент пересохранил exe.xml без комментариев, запись `MSFS Landing Stats … --simulator-autostart` считается чужой. При `enabled = false` текст не меняется, мутация помечается как Unchanged, проверка не выполняется.
  - Настройка сохраняется как false и UI пишет «выключено», но MSFS продолжает запускать приложение.
  - Включить обратно тоже нельзя: «An unmanaged … entry already exists».
- **Сценарий:** включить автозапуск → установить аддон, инсталлятор которого переписывает exe.xml без комментариев → выключить автозапуск в настройках.
- **Направление:** распознавать свою запись по проверенным Path и CommandLine и удалять её; как минимум предупреждать при выключении.

### U-07. Загрузки деталей при навигации не отменяются и сериализуются под общим замком
- **Серьёзность:** Средняя · **Категория:** Производительность / Гонка · **Достоверность:** отсутствие отмены и сериализация — Подтверждено; голодание пула — Гипотеза
- **Где:** `src/LandingStats.App/MainWindow.History.cs:176-252` (203–249)
- **Описание:**
  - Каждая смена выбора без кеша запускает `Task.Run`. Под `_landingRepositoryGate` он выполняет `LoadAll`, `LoadDetail` всех контактов эпизода и иногда миграционный `Save`.
  - Отмена есть только по `_lifetimeCancellation`; устаревший результат отбрасывается уже после полного чтения.
  - Тот же замок нужен runtime: `EpisodeWorkCoordinator` для истории и отчёта, presentation `LoadAll`, cloud.
  - Работы, заблокированные на Monitor, занимают потоки пула, на котором исполняется весь parent-runtime.
- **Сценарий:** пользователь держит ↓ в большом списке → десятки последовательных чтений, итоговая запись появляется с задержкой, сохранение только что завершённого эпизода ждёт в той же очереди.
- **Направление:** debounce выбора, токен отмены на каждую выборку, проверка версии до чтения внутри замка.

### U-08. `MainWindow` остаётся корнем сборки и владельцем durable-состояния
- **Серьёзность:** Средняя · **Категория:** Архитектура · **Достоверность:** Подтверждено
- **Где:** `src/LandingStats.App/MainWindow.xaml.cs:26-125, 521-536, 1224-1314`; `MainWindow.Runtime.cs:27-67`; `MainWindow.History.cs:107-119, 203-249`; `src/LandingStats.App/Runtime/LandingRuntime.cs:129`
- **Описание:** окно (~2870 строк в 8 partial) создаёт репозитории и gate, токены/OAuth, `GoogleDriveBackupService` с callback'ами в само окно, настройки и их очередь, автозапуск и updater. Оно же собирает `LandingRuntime` и отвечает guardian о здоровье. Следствия:
  - cloud-синхронизация читает и применяет настройки через `Dispatcher.InvokeAsync`, поэтому зависший UI останавливает облако;
  - UI сам пишет в общее хранилище истории (`UpdateSummaries`, миграционный `Save`, `DeleteLandingLocally`). Это нарушает принцип «единственный владелец записи файла» (§3 плана) и держится только на общем lock;
  - готовность настроек привязана к runtime (U-04);
  - состояние дублируется: `_landings`/`_loadedDetails` и репозиторий; `_runtimeStatus`, `_lastRecorderState`, `_connectionStatusKey`; `_lastGoogleDriveSyncUtc` и предупреждения пишутся из двух мест.
- **Направление:** вынести корень сборки и команды settings/cloud/history в runtime-сервис; окно оставить только проекцией и источником команд.

### U-09. Остатки удалённого legacy-пути и мёртвые ресурсы
- **Серьёзность:** Низкая · **Категория:** Сопровождаемость · **Достоверность:** Подтверждено
- **Где:** `src/LandingStats.App/MainWindow.RunwayPersistence.cs:13-42, 52-59`; `src/LandingStats.App/Storage/RunwayMetadataPersistence.cs`; `MainWindow.xaml:560-562`; `MainWindow.Shutdown.cs:81`; `MainWindow.xaml.cs:46, 322-326, 537-538, 1633-1651, 1740-1741`; `App.xaml:7, 49-55, 61-63`
- **Описание:**
  - `PersistRunwaysAsync` больше не вызывается. `_runwayPendingIds` всегда пуст, `RunwayLoadingText` никогда не показывается, а всегда завершённый `_runwayPersistenceTask` впустую дренируется при закрытии. `RunwayMetadataPersistence` используется только этим методом и тестами.
  - `OnResetZoomClick` ни к чему не привязан.
  - `_googleDriveDeleteTask` дублирует `_localDeletionTask`.
  - `TryResolveRunway` — тестовый фасад в окне.
  - `OnSimulatorExited`/`OnRecorderStatusChanged` сохранили сигнатуры событий старого recorder.
  - В App.xaml не используются `MonoTextStyle` и `SidebarBrush`; неявный `Style TargetType="Window"` к производному `MainWindow` не применяется.

### U-10. Локализация: неиспользуемые ключи, строки не обновляются при смене языка, неверные ключи сообщений
- **Серьёзность:** Низкая · **Категория:** Локализация · **Достоверность:** Подтверждено
- **Где:** `src/LandingStats.App/Localization/Strings.en.xaml`, `Strings.ru.xaml`; `MainWindow.xaml.cs:141, 183-196, 975, 1196, 1208, 1381-1411, 1824`; `MainWindow.Runtime.cs:131, 187`
- **Описание:**
  - Наборы ключей en и ru совпадают (по 310), все ключи из XAML и кода существуют.
  - Не используются: `BugReport.SavedForRetryFormat`, `Chart.ContactPointFormat`, `Help.LandingFeelName`, `Help.PeakG150`, `Metric.PeakG150`, `Metric.PeakG2`, `Replay.NoRunway`. `History.LoadingRunway` фактически мёртв (U-09).
  - `RefreshLocalizedPresentation` не пересчитывает:
    - tooltip и суффикс версии (`Update.*`);
    - `_googleDriveWarningDetail` и локализованные `_googleDriveErrorDetail`;
    - `SettingsTelemetryErrorText`;
    - суффикс `HoverTimeText`.
  - Неверные ключи: сбой старта runtime показывается как `Recorder.AnalysisFailedFormat`, сбой политики телеметрии — как `BugReport.FailedFormat`. Английские тексты исключений runtime попадают в русский UI.

### U-11. Исключение синхронного UI-обработчика завершает процесс
- **Серьёзность:** Низкая · **Категория:** Баг · **Достоверность:** обработчик — Подтверждено; триггеры — Вероятно
- **Где:** `src/LandingStats.App/App.xaml.cs:60-63`; `MainWindow.xaml.cs:1879-1900, 1917-1921`
- **Описание:** `OnDispatcherUnhandledException` не ставит `Handled`, поэтому тривиальные сбои валят приложение; под guardian это рестарт с cold recovery. Примеры: `Process.Start(URL)` бросает `Win32Exception`, если нет ассоциации браузера или запуск запрещён политикой; `DragMove()` бросает `InvalidOperationException` при вводе пером или касанием.

### U-12. Неограниченный кеш деталей
- **Серьёзность:** Низкая · **Категория:** Утечка ресурсов / Производительность · **Достоверность:** Подтверждено; объём — оценка
- **Где:** `src/LandingStats.App/MainWindow.xaml.cs:49`; `MainWindow.History.cs:281`
- **Описание:** `_loadedDetails` копит полные серии всех просмотренных посадок и очищается только при инвалидации или удалении. При просмотре большой истории память растёт до сотен МБ. Так было и в 0.9.4.

### U-13. Автообновление на старте закрывает окно без проверки безопасной точки
- **Серьёзность:** Низкая · **Категория:** Баг · **Достоверность:** влияние на захват — Гипотеза; порядок операций — Подтверждено
- **Где:** `src/LandingStats.App/MainWindow.xaml.cs:180-221`
- **Описание:**
  - При `UpdateStarted` окно сразу закрывается без проверки `SafeToRestart` или активного эпизода. После рестарта guardian посреди полёта приложение может само закрыться ради обновления и дать второй разрыв захвата.
  - Инициализация автозапуска, онбординг и стартовая синхронизация Drive ждут сетевой проверки обновлений (до 45 с на запрос) — порядок тот же, что в 0.9.4.

### U-14. Числовые ссылки на символы в пути exe.xml
- **Серьёзность:** Низкая · **Категория:** Баг · **Достоверность:** Гипотеза
- **Где:** `src/LandingStats.App/Settings/SimulatorAutoStartManager.cs:356-406, 610-668`
- **Описание:** символы пути вне кодировки файла (например, кириллица в профиле при exe.xml с `encoding="Windows-1252"`) записываются как `&#x…;`. Для XML это корректно, но если загрузчик MSFS их не раскрывает, автозапуск молча не работает, а внутренняя проверка проходит.

### U-15. Вход в Google держит очередь настроек до 3 минут
- **Серьёзность:** Низкая · **Категория:** Архитектура · **Достоверность:** Подтверждено
- **Где:** `src/LandingStats.App/MainWindow.xaml.cs:953-966`; `MainWindow.SettingsOperations.cs:61-78`
- **Описание:** на время OAuth-входа все переключатели выключены, поля показывают «Applying settings…», а настройки, инициированные runtime, ждут. Дедлока нет; так было и в 0.9.4.

### Проверено в этом направлении, проблем не найдено
- XAML: обработчики, Binding-пути и ресурсы существуют; список истории виртуализирован; `IsIndeterminate` привязан к `IsVisible` (LS-018).
- Утечек через привязки к не-INPC `LandingRecord` нет; подписки ReplayView и графиков снимаются при закрытии; таймеры `DelayedBusyIndicator` останавливаются.
- Дисковый I/O, JSON, `LoadAll`, exe.xml и создание runtime выполняются вне диспетчера; `.Result` используется только после `await`; синхронного `Dispatcher.Invoke` нет.
- `SettingsOperationQueue`: операции сериализуются, при остановке отклоняются, ошибка не «отравляет» очередь; дедлоков нет.
- `ApplicationSettingsRepository`: атомарная запись (tmp + `Flush(true)` + Replace), нечитаемый файл сохраняет opt-out, неизвестные поля сохраняются.
- `SimulatorAutoStartManager` (кроме U-06 и U-14): mutex, запрет DTD, лимит 4 MiB, отказ от reparse point, сохранение кодировки и BOM, бэкап, атомарный Replace с проверкой, read-back, откат, XML-экранирование пути.
- Повторные клики и команды при закрытии защищены; поздние результаты загрузок отбрасываются по версиям.
- Обычное закрытие (кроме U-03): drain локальных записей без таймаута, идемпотентный `StopAsync`.
- `StartupTimeline` ограничен (32 записи / 256 KiB), I/O вне UI.

---

## 9. Композиция runtime, supervisor, процессы-воркеры и IPC

Объединены с другими разделами:
- «сбой старта runtime» → U-04;
- бессрочные каталоги сессий → B-08;
- «ядовитые» операции и дедлайн owner → B-05;
- fixtures и мёртвый код протокола → S-04;
- стоимость checkpoint → C-07.

В BACKLOG классы этого раздела не упоминаются; связанная запись — только LS-020.

### A-01. Переполнение окна неподтверждённых сообщений роняет процесс источника вместо записи gap-маркера
- **Серьёзность:** Высокая · **Категория:** Архитектура / Потеря данных · **Достоверность:** механизм подтверждён двумя направлениями (runtime/IPC, нативные источники); частота пауз ACK ≥ 10 с — гипотеза
- **Где:** `src/LandingStats.WorkerHost/WorkerChannel.cs:13-14, 36-38, 58`; `src/LandingStats.App/Runtime/WorkerOwner.cs:15-18`; `src/LandingStats.App/Runtime/SimulatorWorkerRole.cs:41-46, 195-200`; `src/LandingStats.App/Telemetry/SimConnectSource.cs:157-164`; `src/LandingStats.App/Runtime/ControllersWorkerRole.cs:44`; `src/LandingStats.App/Runtime/RuntimeWorkerSource.cs:104-117`; `src/LandingStats.Runtime.Contracts/RuntimePayload.cs:141-156`; `src/LandingStats.WorkerHost/Program.cs:103-107`
- **Описание:**
  - Канал воркера держит не больше 1024 неподтверждённых сообщений или 4 МиБ. Родитель подтверждает их только после durable-коммита журнала.
  - При переполнении `WorkerChannel` навсегда выставляет `_failed`, и `WorkerOwner.Publish` бросает `IOException`.
  - В simulator-роли `Publish` вызывается из SimConnect callbacks. Catch в `HandleWindowMessage` сам вызывает `SetStatus` → `Publish`, который снова бросает → `Dispatcher.UnhandledException` → выход с кодом 70 → рестарт и переподключение SDK.
  - `SourceGapMessage(SourceGapReason.BufferOverflow)` объявлен в контрактах, но нигде не создаётся. Кадры из очереди теряются без учтённого диапазона. Это прямо противоречит §4.1 плана: «StorageBackpressure не должно запускать ложную петлю перезапусков SDK; диапазон потерь — в marker».
  - Ниже 3000 ft simulator публикует около 100 сообщений в секунду (Guard + Full), то есть запас ~10 с. У controllers (100 Гц) запас тот же, поэтому при заторе падают оба источника.
- **Сценарий:** fsync журнала или recording consumer стоит ≥ 10 с на заходе (медленный диск, антивирус, полный диск — A-02, B-10) → воркер падает → `SourceRetirement(ProcessExit)` → `SourceInterrupted` → заход становится `approach_interrupted`, посадка не записывается. Если затор не проходит, начинается цикл рестартов с backoff.
- **Направление:** при переполнении отбрасывать или объединять кадры в воркере, а после разгрузки публиковать ограниченный `SourceGapMessage(BufferOverflow, first, last)`; не бросать исключения из SimConnect callbacks.

### A-02. Отказ хранилища эскалируется в жёсткий перезапуск приложения и цикл рестартов
- **Серьёзность:** Средняя · **Категория:** Архитектура · **Достоверность:** Подтверждено
- **Где:** `src/LandingStats.App/Runtime/ApplicationRuntime.cs:35-36, 58-69`; `RuntimeCommandQueue.cs:158-165`; `ComponentGenerations.cs:117-130`; `ComponentSupervisor.cs:190-209`; `RuntimeWorkerSource.cs:130-138`; `src/LandingStats.ProcessControl/GuardianPolicy.cs:31`
- **Описание:**
  - Tick-owner ждёт `receipt.Durable` без отмены, хотя для операции объявлен дедлайн 30 с. Commands-owner коммитит результат с `CancellationToken.None`.
  - Если журнал недоступен (полный диск, ошибка I/O, owner журнала в backoff), через 30 с supervisor останавливает tick-owner. `StopAsync` (3 с) не может его прервать, а `TerminateAsync` для managed-owner всегда бросает исключение. Дальше `Escalate` → `GuardianRecovery.Runtime` → немедленный `TerminateJobObject` приложения без безопасной точки, в том числе посреди посадки.
  - `RetireAsync` источника при этом блокируется и не обновляет `ObservationQpc`, поэтому через 60 с срабатывает `ControlStall`.
  - Новый экземпляр на полном диске падает уже в конструкторе (U-04). План для полного диска требует backoff и диагностику, а не пересоздание.
- **Сценарий:** rolling cache MSFS заполняет диск C: → запись в журнал завершается ошибкой → через ~30 с приложение убито → новый экземпляр не стартует → цикл.
- **Направление:** отменяемое ожидание durability (квитанция остаётся у dispatcher); различать «ждёт хранилище» и «завис»; не эскалировать отказ хранилища до рестарта App.

### A-03. Дедлайн handshake не ограничивает чтение Hello
- **Серьёзность:** Средняя · **Категория:** Баг · **Достоверность:** Подтверждено (по коду и reference source net48)
- **Где:** `src/LandingStats.App/Runtime/ProcessWorkerHost.cs:83-97`; `src/LandingStats.Runtime.Contracts/RuntimeWire.cs:60-73`; `src/LandingStats.App/Runtime/ProcessJobExecutor.cs:36`
- **Описание:**
  - На net48 `PipeStream` не переопределяет `ReadAsync`, и токен проверяется только при старте операции. Поэтому дедлайн фактически ограничивает лишь `WaitForConnectionAsync`.
  - Если воркер подключил оба пайпа и завис до отправки Hello, `StartAsync` не завершится.
  - Для source-ролей ситуацию спасает стартовый дедлайн supervisor с `TerminateAsync`. У `ProcessJobExecutor` такого терминатора нет: он висит до OperationDeadline владельца (120–180 с), затем отмена не срабатывает за 3 с → эскалация → жёсткий рестарт App.
- **Направление:** по истечении дедлайна убивать job и закрывать пайпы (через `Register`), не полагаясь на токен в `ReadAsync`.

### A-04. Постоянное усиление записи в WAL от источников
- **Серьёзность:** Средняя · **Категория:** Производительность · **Достоверность:** подтверждено двумя направлениями; объёмы — оценка
- **Где:** `src/LandingStats.App/Runtime/ControllersWorkerRole.cs:14, 43-46`; `SimulatorWorkerRole.cs:66, 186-191`; `src/LandingStats.App/Telemetry/SimConnectSource.Runways.cs:256, 544`; `src/LandingStats.App/Runtime/RecordingRuntimeState.cs:266`; `RuntimeWorkerSource.cs:97-114`; `ApplicationRuntime.cs:58-76`
- **Описание:**
  - Controllers публикуют снимок каждые 10 мс — даже без изменений и при нуле устройств. Каждый снимок пишется в WAL (fsync раз в ≤ 250 мс) и подтверждается отдельно. Пока приложение просто открыто, это около 64–140 КБ/с, то есть порядка 0,25–0,5 ГБ/ч.
  - Guard-кадры публикуются с частотой SIM_FRAME и журналируются, хотя родитель их выбрасывает (`case GuardFrameMessage _: break;`).
  - Каждый `SceneryRevision++` заново отправляет весь snapshot вместе со списком аэропортов reality bubble. Ревизия растёт на любом `FacilityDataEnd`, включая optional threshold/PAPI/ILS и поздние ответы. На сохранённом образце это 4342 аэропорта и ~608 КБ JSON, то есть мегабайты WAL на каждый lookup.
  - Всё это вдвое сокращает запас до переполнения из A-01.
- **Направление:**
  - снимки контроллеров слать только при изменении, плюс heartbeat в пределах TTL;
  - Guard не публиковать (или объединять);
  - список аэропортов отправлять один раз на lookup и дебаунсить ревизии;
  - не журналировать снимки без активного потока или эпизода.

### A-05. `ManagedComponentContext.Progress` вызывается не из owner-цикла
- **Серьёзность:** Низкая · **Категория:** Гонка · **Достоверность:** нарушение контракта подтверждено двумя направлениями (runtime, Google Drive); частота — гипотеза
- **Где:** `src/LandingStats.App/Runtime/ComponentGenerations.cs:31-55`; `CloudSyncRuntimeOwner.cs:137-139`; `IsolatedGoogleDriveApi.cs:115, 132, 167, 197, 206`; `ComponentSupervisor.cs:163-164`
- **Описание:**
  - По контракту метод вызывается только из owner-цикла: инкременты не синхронизированы, а Progress отвечает на probe.
  - `IsolatedGoogleDriveApi` поднимает событие из потоков `Task.Run`, в том числе для операций из UI (вход, удаление через `DeferredGoogleDriveApi`). В результате на probe зависшего cloud-owner может ответить чужой поток.
  - Неатомарный `++_ownerProgress` может опубликовать меньший `OwnerProgress` после большего → «Component counters reversed» → ложный рестарт. Вход в аккаунт к тому же засчитывается как операция cloud.sync.

### A-06. Номер последовательности расходуется до записи, которая может быть отменена
- **Серьёзность:** Низкая · **Категория:** Баг · **Достоверность:** Подтверждено (окно узкое)
- **Где:** `src/LandingStats.App/Runtime/ProcessWorkerHost.cs:196-197, 231-235`; `src/LandingStats.Runtime.Contracts/RuntimeWire.cs:75-80`; `src/LandingStats.WorkerHost/WorkerChannel.cs:73-74`
- **Описание:** `++_controlSentSequence` выполняется до `WriteAsync`. Если токен уже отменён, net48 возвращает отменённую задачу, ничего не записав. Следующее сообщение приходит с seq+2, воркер бросает `InvalidDataException` и завершается.
- **Сценарий:** commands-owner перезапускается в момент, когда уже взял `_controlWriter` → simulator worker падает → SimConnect переподключается.

### A-07. Крайние случаи жизненного цикла команд и остановки
- **Серьёзность:** Низкая · **Категория:** Баг · **Достоверность:** Подтверждено (нужна ошибка протокола от собственного воркера)
- **Где:** `src/LandingStats.App/Runtime/ProcessWorkerHost.cs:148-154, 202-224, 252-255`; `ProcessJobExecutor.cs:38-46, 55-74`
- **Описание:**
  - Фильтр `StopAsync` не ловит `InvalidDataException`, которым control pump обрывает ожидающие команды. Последствия:
    - процесс не завершается с подтверждением выхода дерева — остаётся только kill-on-close;
    - исключение из finally подменяет уже полученный результат;
    - `retired` не вызывается.
  - Команда, зарегистрированная после завершения control pump, ждёт только токен.
  - `ProcessJobExecutor` ждёт `CommandAsync` раньше, чем проверяет `host.Completion`.

### A-08. Проверки целостности движка и воркера не привязаны к исполнению
- **Серьёзность:** Низкая · **Категория:** Безопасность / Архитектура · **Достоверность:** Подтверждено; граница привилегий не пересекается — всё лежит в %LOCALAPPDATA%
- **Где:** `src/LandingStats.App/Runtime/ProcessWorkerHost.cs:81-82`; `src/LandingStats.Runtime.Contracts/WorkerExecution.cs:36-49`; `src/LandingStats.WorkerHost/Program.cs:54-56, 62`; `src/LandingStats.App/Runtime/RetainedAnalysisExecutor.cs:66-71`; `ProcessingEngineLease.cs:90-107`
- **Описание:**
  - Проверка build identity тавтологична. Обе стороны хешируют одни и те же 4 файла одного каталога engine прямо перед handshake, поэтому подмену после `ProcessingEngineLease.Open` она не заметит. Остальные DLL (Archives, SimConnect, Newtonsoft) не покрыты вовсе. Ожидаемое значение стоит брать из manifest или identity engine.
  - Хеши retained engine проверяются до запуска, но файлы не блокируются на запись. Лишние файлы в каталоге (подложенная DLL) не обнаруживаются.
  - Воркер не проверяет сервер пайпа (`GetNamedPipeServerProcessId`), в отличие от `GuardianClient.VerifyServer`. Риск низкий: имена пайпов непредсказуемы.

### A-09. Backoff расходится с планом
- **Серьёзность:** Низкая · **Категория:** Архитектура · **Достоверность:** Подтверждено
- **Где:** `src/LandingStats.App/Runtime/ComponentSupervisor.cs:39-40, 227-233`; `src/LandingStats.ProcessControl/GuardianPolicy.cs:15-16`
- **Описание:** в коде backoff 0,25 → 30 с, а в плане (§5) — 1, 2, 5, 10, 30 и максимум 60 с. Первые повторы crash-loop идут каждые 250–500 мс, и каждый раз переподключается SimConnect.

### A-10. Отмена при старте превращается в `IOException`
- **Серьёзность:** Низкая · **Категория:** Сопровождаемость · **Достоверность:** Подтверждено
- **Где:** `src/LandingStats.App/Runtime/ProcessWorkerHost.cs:106-113`; `ComponentSupervisor.cs:117-122`
- **Описание:** `StartAsync` оборачивает в `IOException` любое исключение, включая отмену. При штатном shutdown это даёт ложные «Recovering» и `_failures++`, а `ProcessJobExecutor` теряет семантику отмены.

### A-11. Остановка runtime не защищена от исключений
- **Серьёзность:** Низкая · **Категория:** Утечка ресурсов · **Достоверность:** Подтверждено
- **Где:** `src/LandingStats.App/Runtime/LandingRuntime.cs:227-237`; `ApplicationRuntime.cs:150-162`
- **Описание:** в `StopOwnedAsync` и `StopOwnersAsync` нет try/finally. Исключение на любом шаге (например, faulted completion supervisor) оставляет остальных владельцев работать и не освобождает `runtime.owner`, сессию и квоту.

### Проверено в этом направлении, проблем не найдено
- P/Invoke и структуры для x64 (`JOBOBJECT_EXTENDED_LIMIT_INFORMATION`, `STARTUPINFOEXW`, `PROC_THREAD_ATTRIBUTE_JOB_LIST`); корректные SafeHandle и `SetLastError`.
- Создание процессов: suspended-создание сразу в kill-on-close job через атрибут, так что окна сиротства нет; хэндлы не наследуются; выход всего дерева подтверждается через job accounting до запуска преемника.
- Пайпы: случайные GUID-имена, DACL только для текущего пользователя с запретом Network, `maxInstances = 1`, проверка PID клиента.
- Handshake: session, instance, generation, role, token (сравнение за постоянное время), clock и capabilities проверяются до любой мутации состояния.
- Wire-кодек: длина проверяется до аллокации, лимит 1 МиБ, границы строк и массивов, точная длина без хвоста.
- Ограждение поколений и retirement-барьер; supervisor не перезапускает компоненты после `StopAsync`, преемник стартует только после подтверждённого выхода.
- Порядок shutdown в `ApplicationRuntime` без deadlock.

---

## 10. Durable-хранение: журнал, транзакции эпизодов, outbox, квота, retained analysis

Объединены находки разных направлений:
- полная очередь автоархивов (также направление истории) → B-07;
- бессрочные каталоги сессий (направление runtime) → B-08;
- `File.Replace` без backup (также направление истории) → B-09;
- повреждённая копия engine (также направление launcher) → B-11;
- «ядовитые» операции и дедлайн owner (направления runtime и истории) → B-05;
- проверка retained engine → A-08.

В BACKLOG из этих находок частично описано только удержание данных (LS-020).

### B-01. Оборванный хвост бывшего активного сегмента навсегда переводит журнал в `IntegrityGap`
- **Серьёзность:** Критично · **Категория:** Потеря данных / Баг · **Достоверность:** Подтверждено
- **Где:** `src/LandingStats.App/Runtime/RollingRuntimeJournal.cs:102, 124-128`; `RecoveryJournal.cs:83-89, 161-170`; `RuntimeStorageQuota.cs:109, 128-131, 184-195`
- **Описание:**
  - При восстановлении `RecoveryJournal` сохраняет оборванный хвост в `NNNN.wal.tail-<guid>.bin` и никогда не удаляет этот файл.
  - При открытии `RollingRuntimeJournal` ищет такой файл у каждого сегмента с номером меньше активного. Если находит, ставит `IntegrityGap = true` и пишет флаг в `session.json` — после этого каждое открытие падает на строке 102.
  - Хвост у активного сегмента — законный случай: этот сегмент ещё не подтверждался. Но после продолжения записи и ротации он становится «запечатанным», и проверка срабатывает ложно.
  - Оборванная запись возникает в штатном сценарии нехватки места. Заголовок и payload пишутся двумя `Write`, `BudgetStream` режет запись по квантам квоты, а `Allocate` может бросить `IOException` посреди записи: квота полна, мьютекс занят дольше 10 с или настоящий ENOSPC.
- **Сценарий:**
  1. Квота почти заполнена. Append записал заголовок, на payload `Allocate` бросил исключение.
  2. Супервизор перезапускает dispatcher; хвост сохранён и усечён.
  3. Место освободилось, запись продолжилась, не позже чем через 5 с — ротация на N+1.
  4. Дальше:
     - любой следующий старт dispatcher в этой сессии получает `IntegrityGap`, и запись мертва до конца сессии;
     - выход зависает: `StopOwnersAsync` ждёт `stop.Durable`, окно ждёт без дедлайна;
     - при следующем запуске `ColdRecordingRecovery` падает навсегда и повторяется каждые ≤ 30 с, каждый раз с запуском WorkerHost и хешированием engine;
     - неприменённый хвост («посадка в полёте») теряется, RAW сессии не экспортируется.
- **Тесты:** сценарий «хвост → append → ротация → reopen» не покрыт.
- **Направление:** фиксировать в `session.json`, что хвост сохранён, пока сегмент был активным, и ставить `IntegrityGap` только для хвостов уже запечатанных сегментов. Добавить тест «сбой квоты посреди записи → reopen → rotate → reopen».

### B-02. Cold recovery и живая запись дают разный `InputHash` одного эпизода — восстановление сессии срывается навсегда
- **Серьёзность:** Высокая · **Категория:** Баг / Потеря данных · **Достоверность:** Подтверждено; окно срабатывания — временное
- **Где:** `src/LandingStats.App/Runtime/ColdRecordingRecovery.cs:54, 128-138`; `RecordingEffectPersistence.cs:23`; `LandingRuntime.cs:92-93`; `EpisodePersistenceData.cs:37`; `EpisodeTransactionStore.Import.cs:20-22`; `EpisodeWorkCoordinator.cs:70`
- **Описание:** живой путь вызывает `CommitInputs(..., _known(), ...)` и сериализует в `inputs.bin.gz` весь кеш известных аэропортов (`KnownAirports`). Cold recovery для того же эпизода передаёт пустой массив. Байты, а значит и `InputHash`, получаются разными. `ImportInputs` считает любое расхождение hash у эпизода в состоянии `InputsCommitted` конфликтом и бросает `InvalidDataException` ещё до импорта facilities и записи `recovered.json`.
- **Сценарий:**
  1. Эпизод E завершился, его inputs записаны.
  2. До следующего checkpoint (≤ 5 с) App упал или был убит — крэш, kill или выключение ПК сразу после выхода из сима.
  3. При старте recovery снова получает E с пустым кешем, и `Import` падает. Повтор каждые 30 с, бесконечно.
  4. Если финальные facilities E были только в хвосте журнала, E навсегда остаётся в `HistorySaved` без отчёта, ВПП и архива и опрашивается каждую секунду. Сессия не собирается.
- **Тесты:** `ColdRecordingRecoveryRegressionTests` и в живом пути передаёт пустой список, поэтому расхождение не ловится.
- **Направление:** для эпизода в `InputsCommitted` считать основной store авторитетным и импортировать только facilities, либо сделать входы детерминированными: снимок кеша — в журнал или вне хешируемых inputs.

### B-03. Хранилище эпизодов никогда не собирается и лежит внутри квоты 8 GiB
- **Серьёзность:** Высокая · **Категория:** Архитектура / Утечка ресурсов · **Достоверность:** Вероятно (скорость роста оценена в 2–6 МБ на эпизод, не замерена)
- **Где:** `src/LandingStats.App/Runtime/EpisodeTransactionStore.cs:143-154, 191-198` (API удаления нет); `EpisodeTransactionStore.Facilities.cs:91-96`; `RuntimeRetentionOwner.cs:39-71`; `RuntimeStorageQuota.cs:128-131, 142-177`; `LandingRuntime.cs:52`; `src/LandingStats.App.Launcher/Program.cs:214`
- **Описание:**
  - На каждый эпизод навсегда остаются:
    - `inputs.bin.gz` — все кадры плюс полный кеш аэропортов (K-02);
    - несжатые `analysis.json` и `report.json` — полные серии примерно по 40 колонок на 20 Гц;
    - `facilities.bin.gz`.
  - Удаление посадки пользователем пишет только tombstone.
  - `Scan` считает весь корень `Runtime`, включая деплой `Runtime\App\<ver>` и engines.
  - Когда квота заполнится, `Allocate` начнёт бросать исключения на append журнала и на `CommitInputs`. Запись остановится, а освободить место нечем. Вместе с B-01 и B-06 приложение ещё и не закрывается.
  - LS-020 оставляет manual-report inputs, но это последствие там не разобрано.
- **Направление:** политика для завершённых эпизодов — удалять `analysis.json` после `ReportReady`, сжимать отчёт, не копировать кеш аэропортов, учитывать tombstones. Либо вынести эти данные из квоты записи.

### B-04. Одна битая запись outbox навсегда останавливает все загрузки
- **Серьёзность:** Высокая · **Категория:** Баг (сломанный путь восстановления) · **Достоверность:** Подтверждено
- **Где:** `src/LandingStats.App/Runtime/DurableOutbox.cs:111-113, 152, 171, 185, 252, 258, 260-261`; `UploadRuntimeOwner.cs:54, 101-104, 155`
- **Описание:**
  - `IsRecoverableStorageFailure` не включает `InvalidDataException`. Поэтому в `RetireConfirmedWorker`, `RecoverTerminalEntries` и `RecoverAfterPreviousRuntimeExited` невалидная запись выходит из цикла исключением, а не попадает в `RecoveryErrors`.
  - `UploadRuntimeOwner.RunAsync` вызывает `RecoverTerminalEntries` при каждом старте без защиты; `FinishRetirement` в начале цикла тоже вне try.
  - Итог — бесконечный crash-loop владельца загрузок.
- **Сценарий:** любой из трёх случаев — после этого ни одна загрузка больше не выполняется:
  - сменился путь `%LocalAppData%` (миграция профиля), и `ValidateArchivePath` отвергает все записи;
  - повреждён один JSON;
  - у `Delivered`-записи остался файл с другими байтами.
- **Направление:** при `InvalidDataException` восстанавливать записи по отдельности; хранить пути относительно корня архивов.

### B-05. «Ядовитый» эпизод или операция роняет владельца по кругу; терминального состояния ошибки нет
- **Серьёзность:** Высокая · **Категория:** Баг / Архитектура · **Достоверность:** подтверждено тремя направлениями (хранение, runtime, история)
- **Где:** `src/LandingStats.App/Runtime/EpisodeRuntimeOwner.cs:57-70`; `EpisodeWorkCoordinator.cs:58-63`; `EpisodeTransactionStore.cs:238-239`; `RetainedAnalysisExecutor.cs:75`; `AnalysisWorkerRole.cs:17, 70, 84-85`; `RuntimeComponentCatalog.cs:28-41`; `ComponentSupervisor.cs:160-162`; `RuntimeSessionOwners.cs:78-87`; `src/LandingStats.Archives/CaptureArchive.cs:218, 247-254`
- **Описание:**
  - Фильтр `EpisodeRuntimeOwner` ловит только `IOException`, `UnauthorizedAccessException` и `InvalidDataException`. Наружу уходят:
    - `SerializationException` от повреждённого manifest;
    - `ArgumentException` из `CommitArchive`, если ревизия политики меньше записанной (например, при потерянном `policy.json`);
    - `InvalidOperationException` от `RequireStage` и `ReadAnalysis`;
    - исключения `LandingRepository`;
    - `InvalidOperationException` из `CaptureArchiveWriter.Dispose`, которым тот подменяет исходную ошибку.
  - Supervisor перезапускает владельца без ограничения, а обход начинается с той же позиции. Все эпизоды после «ядовитого» ждут вечно.
  - По истечении OperationDeadline отменяется весь owner. Отмена не попадает в `_retry` и `RecordFailure`, поэтому новое поколение сразу берёт тот же первый элемент (head-of-line).
  - Детерминированный сбой анализа в воркере превращается в `IOException("Retained analysis failed: <TypeName>")`. Он повторяется каждые ≤ 30 с бесконечно, каждый раз с новым процессом и полным хешем engine. `FailureCount` повторы не останавливает, сообщение и стек теряются.
  - Каждый analysis-job к тому же заново сканирует каталог самолётов (E-03), и этот скан сам может занять заметную часть дедлайна 180 с.
- **Направление:** изолировать сбой каждого эпизода; ввести терминальное состояние `Failed` с порогом и диагностикой; дедлайн и карантин на уровне элемента; передавать из воркера сообщение исключения; сохранять первичное исключение в `CaptureArchiveWriter`.

### B-06. Durable-квитанции и drain никогда не завершаются ошибкой — при недоступном журнале приложение не закрывается
- **Серьёзность:** Средняя · **Категория:** Архитектура · **Достоверность:** Подтверждено
- **Где:** `src/LandingStats.App/Runtime/DurableRuntimeDispatcher.cs:98-109, 130-138, 237-242`; `ApplicationRuntime.cs:131-140, 157-160`; `LandingRuntime.cs:153`; `src/LandingStats.App/MainWindow.Shutdown.cs:69-83`
- **Описание:** `Pending.Committed` и `_drained` получают результат только из работающего `RunAsync`. Если журнал не поднимается (квота полна, B-01), все `await receipt.Durable` висят вечно: stop, policy, `StartConfiguredAsync`. Закрытие окна ждёт без дедлайна. См. также A-02.
- **Направление:** ограничить ожидание квитанций при остановке и/или переводить их в ошибку при закрытии intake; показывать пользователю честный статус.

### B-07. Полная очередь автоархивов навсегда держит эпизоды в `ReportReady` с дорогими повторами
- **Серьёзность:** Высокая · **Категория:** Баг / Производительность · **Достоверность:** подтверждено двумя направлениями (хранение, история)
- **Где:** `src/LandingStats.App/Storage/AutomaticTelemetryArchive.cs:12-14, 39-51`; `src/LandingStats.App/Storage/BugReportRepository.cs:187-188, 197-203`; `src/LandingStats.App/Runtime/EpisodeRuntimeOwner.cs:65-69, 82, 87, 99-103`; `DurableOutbox.cs:219-221, 229-238`; `EpisodeTransactionStore.cs:240-242`; `RawChunkStore.cs:119-120`; `src/LandingStats.App/TelemetryUpload/TelemetryUploadClient.cs:718-727`
- **Описание:**
  - Контракт `TryCreate` — «null означает пропустить только новую автокопию» — остался от 0.9.4. Но `EpisodeRuntimeOwner` превращает null в `IOException` и повторяет эпизод бесконечно (каждые ≤ 30 с). `CommitArchive` не умеет принять причину пропуска «очередь полна».
  - Каждая попытка заново хеширует и распаковывает весь `inputs.bin.gz`, хеширует и парсит `report.json`. Если байтовый бюджет занят частично, архив ещё и полностью собирается (Zstd-9/Deflate) и тут же удаляется.
  - В бюджет 128 файлов / 256 MiB (шаблон `*_auto_raw*.zip`) попадают карантинные `.rejected-NNN.zip`, которые никто не удаляет, а также Deferred-архивы.
  - `controller-catalogs.json` и `source-state.bin` пишутся в архив без проверки capability: capability сервера проверяется только при отправке.
- **Сценарий:**
  - Клиент выпущен раньше серверного reader (нет `controller_catalog_formats`) или долго работает офлайн → накапливается 128 Deferred → каждая следующая посадка застревает и каждые 30 с перечитывает мегабайты.
  - ≥ 128 отказов 4xx → автодиагностика молча останавливается навсегда.
- **Направление:** записывать durable-пропуск с причиной «очередь полна» (или ждать события освобождения); не учитывать карантин в бюджете либо ограничить срок его хранения; не писать новые члены архива без подтверждённой capability; повторять, не читая полные входы.

### B-08. Неограниченный рост каталогов runtime и ежесекундный полный опрос (O(N))
- **Серьёзность:** Высокая · **Категория:** Производительность / Утечка ресурсов · **Достоверность:** подтверждено тремя направлениями; нагрузка — оценка
- **Где:** `src/LandingStats.App/Runtime/EpisodeRuntimeOwner.cs:52-60, 73, 82, 87-88`; `RuntimeRetentionOwner.cs:36, 62-83, 127-135`; `DurableOutbox.cs:117-123, 140-154`; `ArchiveIntakeOwner.cs:80-91`; `LandingRuntime.cs:55-58`; `RuntimeSessionOwners.cs:15-32, 74, 89, 124, 156`; `ComponentSupervisor.cs:176-186`; `ComponentGenerations.cs:56-66`
- **Описание:**
  - **Сессии.** Каждый запуск App, включая каждый рестарт guardian, создаёт новую сессию `sessions/<guid>`. Retention навсегда оставляет в ней `runtime.json`, `runtime.owner` и `collected.json`. `RuntimeSessionDirectories.Read` для каждой собранной сессии делает `File.Exists` и два разбора JSON; её вызывают raw-owner каждые 0,5 с, recovery-owner каждую секунду и retention. При > 4096 несобранных сессий `Read` бросает исключение, и raw/recovery-владельцы уходят в цикл падений.
  - **Retention.** Задуман раз в 30 с, а фактически работает раз в ~5 с: probe при `OwnerStall` = 5 с будит owner через `SignalWork`, и тот выполняет полный `Collect()` с перечитыванием всех сессий и manifests эпизодов.
  - **Эпизоды.** `EpisodeRuntimeOwner` каждую секунду читает и парсит manifest каждого эпизода, включая завершённые. Эпизод, ждущий facilities, каждую секунду без backoff заново хеширует и распаковывает inputs.
  - **Outbox и intake.** Записи outbox в состояниях `Delivered`/`Quarantined` и intake-заявки не удаляются никогда. `RetireConfirmedWorker` проходит весь outbox на каждую попытку загрузки.
- **Сценарий:** после сотен запусков и посадок фоновая нагрузка вырастает до тысяч файловых операций в секунду — на той же машине, где работает симулятор.
- **Направление:** удалять или сворачивать собранные сессии и терминальные записи в общий индекс; держать незавершённые элементы в памяти или индексе; будить владельцев по событиям; probe не должен запускать полный цикл работы. Связано с LS-020.

### B-09. При сбое `File.Replace` теряется единственная копия файла; temp пишется без flush
- **Серьёзность:** Средняя · **Категория:** Потеря данных · **Достоверность:** путь в коде подтверждён двумя направлениями; частота — гипотеза, зависит от окружения
- **Где:** `src/LandingStats.App/Runtime/DurableFile.cs:15-23`; `RuntimeStorageQuota.cs:198-203`; `src/LandingStats.App/Storage/LandingRepository.cs:172, 262, 288, 294, 298-304`; `LandingSiteMetadataPersistence.cs:43`; `AirportFacilityRepository.cs:92-116`; `src/LandingStats.App/MainWindow.History.cs:243`
- **Описание:**
  - По документации ReplaceFile при `ERROR_UNABLE_TO_MOVE_REPLACEMENT` (1176) без backup-имени заменяемый файл уже удалён, а данные остаются только во временном файле. При этом `finally` в `DurableFile.Write` и `Import` безусловно удаляет temp.
  - Это единственный примитив для manifests, outbox, `session.json`, политики, checkpoint и ledger. Detail посадки теперь перезаписывается чаще: метаданные отчёта, миграция, Drive replace/merge.
  - `Import` и `DeserializeBackup` пишут temp через `File.WriteAllBytes` без `Flush(true)`; `AirportFacilityRepository.MergeAndSave` — тоже без flush.
- **Сценарий:** антивирус держит свежий `.w*.tmp` в момент Replace → ошибка 1176 → удалены и destination, и temp → посадка исчезает при следующей сверке.
- **Направление:** не удалять temp, если destination отсутствует (восстанавливать через Move), либо `MoveFileEx(REPLACE_EXISTING | WRITE_THROUGH)` или backup-имя; flush перед Replace во всех путях.

### B-10. `RuntimeStorageQuota` держит общий lock во время I/O, ожидания мьютекса и полного скана
- **Серьёзность:** Средняя · **Категория:** Производительность / Гонка · **Достоверность:** Подтверждено; влияние — вероятно
- **Где:** `src/LandingStats.App/Runtime/RuntimeStorageQuota.cs:107-111, 112-140, 142-177, 179-197`
- **Описание:**
  - `Write` выполняет `stream.Write`, `Allocate` (ожидание named mutex до 10 с) и `Scan` всего дерева под одним `_gate` на процесс.
  - Счётчик только растёт, поэтому у предела каждый 1 MiB вызывает полный `Scan`. Всё это время заблокированы все писатели, включая append журнала.
  - Таймаут мьютекса превращается в `IOException` посреди записи WAL — прямой путь к B-01.
- **Направление:** не держать `_gate` во время I/O и скана; резервировать квоту на всю WAL-запись до начала записи.

### B-11. Повреждённая копия текущего engine навсегда блокирует запуск runtime
- **Серьёзность:** Средняя · **Категория:** Баг · **Достоверность:** подтверждено двумя направлениями (хранение, launcher); триггер внешний
- **Где:** `src/LandingStats.App/Runtime/ProcessingEngineLease.cs:76-77, 92-105`; `LandingRuntime.cs:54, 113`; `src/LandingStats.App.Launcher/Program.cs:76-84`
- **Описание:**
  - Если `engine.json` существует, `CaptureFiles` сразу вызывает `Open`. Удалённый или изменённый файл (например, помещённый антивирусом в карантин) даёт исключение в конструкторе `LandingRuntime` при каждом запуске; последствия описаны в U-04.
  - Самовосстановления нет, хотя исходный деплой с теми же hash доступен.
  - Launcher проверяет и восстанавливает только свой кеш `Runtime\App\<ver>`, а WorkerHost запускается из `Runtime\engines\<id>`.
- **Направление:** для не-pinned engine пересоздавать несовпавшие файлы из проверенного деплоя; для pinned — карантин и явная диагностика.

### B-12. Lock хранилища эпизодов смешан с путём записи; избыточная верификация
- **Серьёзность:** Низкая · **Категория:** Архитектура / Производительность · **Достоверность:** Вероятно
- **Где:** `src/LandingStats.App/Runtime/EpisodeTransactionStore.cs:222-232`; `EpisodeTransactionStore.Facilities.cs:40-63, 78, 85-101`; `RecordingEffectPersistence.cs:21-27`
- **Описание:**
  - `SaveHistory` и `CommitReport` держат `_gate` во время hash/parse, захвата gate репозитория и O(history)-операций `LandingRepository`. Recording thread ждёт тот же `_gate` в `CommitInputs` и `CommitFacilities`.
  - `CommitFacilities` дважды верифицирует и распаковывает inputs прямо на recording thread.
  - При открытии журнала checkpoint размером до 192 MiB читается 4 раза.

### B-13. GC «закрыт при сомнении» и не умеет исправляться
- **Серьёзность:** Низкая · **Категория:** Утечка ресурсов · **Достоверность:** Подтверждено
- **Где:** `src/LandingStats.App/Runtime/RuntimeRetentionOwner.cs:76-85, 109`; `LandingRuntime.cs:57-58`; `RuntimeSessionOwners.cs:15-21`
- **Описание:**
  - Сессии, в которых есть хоть один `*.tail-*.bin`, хранятся вечно.
  - Одна нечитаемая сессия или manifest — например, каталог без `runtime.json` после сбоя `RuntimeSession.Create` — отключает GC engines целиком.
  - Такие сессии к тому же бесконечно повторяются владельцами recovery и RAW (LS-020).

### B-14. RAW-конвейер подключён, но в production не включён; при включении проявятся скрытые дефекты
- **Серьёзность:** Низкая · **Категория:** Архитектура · **Достоверность:** подключение подтверждено; последствия скрыты, пока RAW выключен
- **Где:** `src/LandingStats.App/MainWindow.Runtime.cs:60`; `src/LandingStats.App/Runtime/ApplicationRuntime.cs:96`; `RawEffectStore.cs:69, 125-138, 161`; `RecordingEffectPersistence.cs:28`; `RawChunkStore.cs:74-78, 94`; `src/LandingStats.App/TelemetryUpload/TelemetryUploadClient.cs:223`
- **Описание:** при включении RAW проявится следующее:
  - на каждый кадр создаётся отдельный файл с `Flush(true)`, причём на recording thread;
  - `CollectBefore` удаляет до ~30 тыс. файлов под тем же lock, что и `Commit`;
  - переполнение RAW-store (512 MiB / 65 536) роняет recording owner по кругу — сбой отладочной функции блокирует запись посадок;
  - чанки регистрируются с `automatic: false` и уходят без проверки автозагрузки и согласия; в 0.9.4 требовалось `AutomaticUploadEnabled && ConsentAccepted`;
  - экспорт старых сессий кодом новой версии может дать вечный «RAW replay differs».

### B-15. Пределы журнала
- **Серьёзность:** Низкая · **Категория:** Баг (живучесть) · **Достоверность:** Вероятно (редкие условия)
- **Где:** `src/LandingStats.App/Runtime/RollingRuntimeJournal.cs:155, 163, 216, 277-278`; `RecoveryJournal.cs:101-107, 242-249`
- **Описание:**
  - На лимите `DiskBytes` `RequireDisk(snapshot.Length)` не даёт записать непустой checkpoint. Без checkpoint невозможен `Collect`, и журнал не освобождается. Тест проверяет только пустой checkpoint.
  - Проверка квоты, не имеющая побочных эффектов, тем не менее переводит экземпляр в `_faulted`.
  - Частично записанный заголовок нового активного сегмента делает журнал неоткрываемым, хотя пустой сегмент можно безопасно инициализировать заново.

### B-16. Диагностика и мёртвый код
- **Серьёзность:** Низкая · **Категория:** Сопровождаемость · **Достоверность:** Подтверждено
- **Где:** `src/LandingStats.App/Runtime/AnalysisWorkerRole.cs:84-85`; `DurableOutbox.cs:59-60, 157-173`; `EpisodeWorkCoordinator.cs:58-63`; `EpisodeRuntimeOwner.cs:69`; `RollingRuntimeJournal.cs:198-207`; `DurableFile.cs:15`
- **Описание:**
  - Воркер возвращает только имя типа исключения.
  - `RecoveryErrors` нигде не читается.
  - `RecordFailure` считается дважды и пишет `OperationCanceledException` при штатной остановке; пустой catch в `EpisodeWorkCoordinator.cs:61` глушит сбой записи самой ошибки.
  - `RecoverAfterPreviousRuntimeExited`, `Pin` и `Release` в production не используются.
  - `.w*.tmp`, оставшиеся после крэша, не удаляются.

### Проверено в этом направлении, проблем не найдено
- CRC32 и формат записи: CRC заголовка и payload, непрерывность sequence, монотонность receipt, проверка идентичности.
- `RecoveryJournal`: ACK только после `Flush(true)`; при восстановлении сохраняется валидный префикс, а хвост копируется и сбрасывается на диск до усечения.
- Порядок операций `RollingRuntimeJournal`: маркеры ротации и сборки на месте; checkpoint записывается и проверяется до state.
- `DurableRuntimeDispatcher`: ограниченный intake, group commit до ACK, дедупликация после замены писателя, TCS с `RunContinuationsAsynchronously`.
- Стадии `EpisodeTransactionStore`: маркер пишется после данных, уже записанные файлы принимаются после крэша, `ArchiveId` фиксируется при создании, `CopyImmutable` проверяет hash до и после копирования.
- Tombstones соблюдаются. `DurableOutbox`: fencing claims, квитанция `Delivered` до удаления архива, двухфазный карантин, путь внутри корня.
- `RetainedAnalysisExecutor`: длина, SHA-256 и Id/Engine/InputHash/FacilityHash результата проверяются до commit; `RuntimeScratchLease` корректен.
- Порядок блокировок store → repository и intake → store → outbox: циклов, ведущих к deadlock, нет.

---

## 11. Нативные источники (SimConnect, WinMM)

Критичных дефектов не найдено. Объединены с другими разделами:
- переполнение выходного буфера → A-01;
- диагностика финального lookup в чужом журнале → C-06;
- метаданные самолёта вне flight context → C-03;
- избыточные сообщения в WAL → A-04;
- тестовая композиция ≠ production → T-02;
- `ResetControlContext` не вызывается → P-01;
- binding redirects WorkerHost → S-01.

### N-01. Невалидная optional-строка вызывает бесконечный reconnect или crash-loop роли
- **Серьёзность:** Средняя · **Категория:** Баг · **Достоверность:** путь подтверждён; оба триггера редкие — гипотеза
- **Где:** `src/LandingStats.App/Runtime/SimulatorWorkerRole.cs:64`; `src/LandingStats.Runtime.Contracts/RuntimePayload.cs:97-100`; `RuntimeWire.cs:142-145`; `src/LandingStats.App/Telemetry/SimFrameData.cs:88-102`; `SimConnectSource.cs:157-164, 485-491`; `WindowsJoystickReader.cs:112-122, 147-158`; `src/LandingStats.Runtime.Contracts/ControllerMessages.cs:14-19`; `src/LandingStats.App/Runtime/ControllersWorkerRole.cs:29, 40`
- **Описание:**
  - Simulator:
    - `AircraftMessage` требует не больше 512 байт UTF-8 для title и 256 для ATC type/model.
    - Строки SimConnect (до 255 байт) маршалятся как ANSI. После перекодировки в UTF-8 они могут вырасти в 2–3 раза.
    - `ArgumentException` из конструктора в обработчике `MetadataReceived` проходит через dispatch → `Disconnect` → reconnect через 2 с → снова metadata → снова исключение. Optional-метаданные бесконечно рвут обязательные потоки.
  - Controllers:
    - `ResolveProductName` берёт OEMName из реестра; `.Trim()` не убирает `'\0'`, а `ControllerDescriptor` такой символ отвергает.
    - `CreateCatalog` бросает исключение, роль уходит в crash-loop. Legacy такие строки принимал.
- **Сценарий:**
  - Самолёт с длинным локализованным TITLE (примерно ≥ 105 кириллических или ≥ 70 CJK-символов) → reconnect каждые ~3 с, ни одна посадка не записывается.
  - Контроллер с лишним NUL в OEMName → оси недоступны всю сессию.
- **Направление:** санитизировать и обрезать строки по UTF-8-байтам в воркере до создания payload; ошибки публикации optional-данных изолировать без `Disconnect`; рассмотреть декодирование строк SimConnect как UTF-8.

### N-02. Непоследовательная обработка исключений SDK вне callbacks
- **Серьёзность:** Низкая · **Категория:** Баг · **Достоверность:** путь подтверждён; реальные HRESULT — гипотеза
- **Где:** `src/LandingStats.App/Telemetry/SimConnectSource.cs:124-125, 212-216` (сравнить с legacy-веткой 258–270), `904-943`; `src/LandingStats.App/Runtime/SimulatorWorkerRole.cs:83-84`
- **Описание:**
  - В `TryConnect` с `constructOnOwner` любое исключение `new SimConnect`, кроме `COMException`, роняет воркер вместо повтора. `Marshal.ThrowExceptionForHR` превращает `E_ACCESSDENIED`, `E_INVALIDARG` и `E_OUTOFMEMORY` именно в не-COM типы.
  - `RefreshMetadata` и `RefreshAirports` вызываются из command handler без try/catch: `COMException` на разорванном pipe роняет процесс вместо `Disconnect`.

### N-03. Пробелы в обработке отказов SDK (LS-005/LS-009)
- **Серьёзность:** Низкая · **Категория:** Баг · **Достоверность:** логика вероятна; триггеры — гипотеза
- **Где:** `src/LandingStats.App/Telemetry/SimConnectSource.cs:350-375, 786-902`; `SimConnectSource.Health.cs:9-16`; `RequiredTelemetrySubscription.cs:50-73`
- **Описание:**
  - Отказы optional-операций (health probes, metadata, control events, lifecycle) всё равно ставят `RecorderState.Error`. Probe идёт каждые 2 с, поэтому систематический отказ будет мигать статусом.
  - Отказ обязательных полей definition никак не связан с подписками Guard/Full. Обёртка делает `PtrToStructure` без проверки размера, и поля могут молча сдвинуться.
  - `_everRequested` ставится при отправке, а не при принятии. После асинхронного отказа `SIM_FRAME` gate отправит `NEVER` на ни разу не активный ID → `UNRECOGNIZED_ID` → бесконечный повтор с backoff до 30 с и статусом Error.

### N-04. Классификация здоровья и lifecycle
- **Серьёзность:** Низкая · **Категория:** Диагностика · **Достоверность:** (a), (b), (d) подтверждены; (c) — гипотеза
- **Где:** `src/LandingStats.App/Telemetry/SimConnectSource.Health.cs:21, 59-64`; `SimConnectSource.cs:61, 190-199, 204, 593-605`; `src/LandingStats.App/Runtime/SimulatorWorkerRole.cs:41-42, 62, 99`; `ComponentSupervisor.cs:166-173`; `RecordingRuntimeState.cs:241`
- **Описание:**
  - (a) Reconnect из-за молчащего потока, сбои receive/setup/mode change и обычный разрыв дают одно и то же `LifecycleMessage(Disconnected)` с причиной «connection_closed».
  - (b) `useful++` учитывает любой `Publish`, включая статусы Waiting каждые 2 с. Пока приложение ждёт MSFS, supervisor показывает `Running/useful_progress` и сбрасывает `_failures`.
  - (c) Нет дедлайна на `SIMCONNECT_RECV_OPEN`. Пока соединение создано, но не открыто, watchdog выключен, а повторная попытка не переподключается.
  - (d) Темп reconnect считается по `DateTime.UtcNow`, поэтому откат часов задерживает переподключение.

### N-05. Перечисление устройств каждые 5 с внутри 10-мс цикла опроса
- **Серьёзность:** Низкая · **Категория:** Производительность / данные · **Достоверность:** Гипотеза
- **Где:** `src/LandingStats.App/Runtime/ControllersWorkerRole.cs:38-42`; `src/LandingStats.App/Telemetry/WindowsJoystickReader.cs:31-43`
- **Описание:**
  - Каждые 5 с выполняется 16 ID × (`joyGetPosEx` + `joyGetDevCaps`) плюс чтение реестра, в том числе во время посадки. Legacy на время записи перечисление откладывал.
  - Длительность не измерялась. Медленный путь WinMM для отключённых ID может превысить TTL 250 мс, и оси на посадке периодически станут невалидными (ср. E-01).

### N-06. God class, мёртвый legacy-код и отступления от плана
- **Серьёзность:** Низкая · **Категория:** Архитектура · **Достоверность:** Подтверждено
- **Где:** `src/LandingStats.App/Telemetry/SimConnectSource*.cs`; `SimConnectSource.Lifecycle.cs:49-53`; `SimConnectSource.Observations.cs:16`; `SimConnectSource.Runways.cs:17`; `SimulatorStreamWatchdog.cs:17-19`; `WindowsJoystickReader.cs:194-195`; `src/LandingStats.Probe/SimFrameData.cs`
- **Описание:**
  - ~2000 строк в partial-классах с общими изменяемыми флагами; например, `_airportFacilityRequestPending` меняется в 8 местах.
  - Мёртвое в production:
    - путь `ConnectAsync` / `_pendingConnectionMessages`;
    - `Maintenance`, `AirportFacilitiesUpdated`, `RunwayFacilitiesUpdated`;
    - `GetRunwayFacilities`, `RunwayFacilities` / `_publishedRunways`;
    - `SeedAirportFacilities`;
    - `RefreshAirports` / `WorkerCommand.Airports` — отправителя нет;
    - `TimeOf`, `ElapsedSeconds`;
    - запрос списка аэропортов при connect, результат которого не используется.
  - Диапазоны request ID у observation и runway оба начинаются с 0x40000000, хотя комментарий watchdog утверждает, что они различны.
  - Смена режима Full и зависимые facility-запросы по-прежнему выполняются внутри dispatch callbacks; отложен только Close (план §6.1).
  - `Enum.TryParse(flow.ToString())` принимает числовые строки для неизвестных значений SDK.
  - Первый параметр `joyGetDevCaps` объявлен как `uint` вместо `UINT_PTR` и работает только за счёт zero-extension.
  - `SimFrameData` и definitions продублированы в Probe.

### Проверено в этом направлении, проблем не найдено
- LS-011: закрытие отложено до выхода из dispatch, старый handle не закрывает новый, Close идемпотентен и выполняется на owner.
- Единственный STA owner с message-only HWND; блокирующие ожидания не прокачивают сообщения, поэтому реентерабельного `ReceiveMessage` нет.
- Раскладки `SimGuardData`, `SimFrameData`, `SourceObservationData`, `AircraftMetadata` и facility-структур совпадают с `AddToDataDefinition` поле в поле.
- High-bit request ID обрабатываются корректно; пространства event и group ID не пересекаются.
- Подписки: desired/requested/observed разделены, Guard не подтверждает Full, backoff 1–30 с, initial NEVER подавлен.
- Watchdog приостанавливается на паузе, SimStop, flow-блоках и при `motion = false`. SimulatorQuit, Disconnected и смерть воркера различаются.
- Scenery RPC и runway/facility transport: логика парсинга, merge и таймаутов не изменилась относительно 0.9.4.
- WinMM: `JOYINFOEX` (52 байта), `JOYCAPSW` (728 байт), флаги и нормализация осей верны. `OwnerCadence` использует high-resolution waitable timer без изменения глобального timer period.
- Загрузка роли в WorkerHost: managed и native SimConnect лежат рядом, оба x64; `NoInlining` в `WorkerRoleFactory` не даёт загрузить SimConnect в роли controllers.

---

## 12. Численный анализ касания

Падений анализа на допустимых входах и ошибок в первичных метриках (`InertialVerticalFpm`, `LatchedNormalFpm`, `PeakG`, `Impact`) не найдено. Незакоммиченный рефакторинг путь вычислений не меняет. M-01 подтверждена пересчётом 16 записанных фикстур; скрипты лежат в scratchpad сессии ревью. `ContactPointAnalysis` вынесен в S-03.

### M-01. Неверный знак члена ωy·sinφ в `WorldVerticalRigidBodyProjection` смещает datum offset модели v1
- **Серьёзность:** Средняя · **Категория:** Баг · **Достоверность:** Подтверждено (по коду и данным 16 фикстур)
- **Где:** `src/LandingStats.Core/WorldVerticalRigidBodyProjection.cs:23-27`; `src/LandingStats.Core/TelemetryGeometryCalibration.cs:282-295, 340-342, 1285-1300`; потребитель — `src/LandingStats.App/Telemetry/FlightModelGeometryResolver.cs:212-218`; `docs/methodology.md:245-252, 300-306`; для сравнения — `TelemetryContactPointReconstruction.cs:545-550` (`NativeRates`)
- **Описание:**
  - Хелпер возвращает `−q·L·cosθ·cosφ − r·L·sinφ`. Точная вертикальная скорость вынесенной вперёд точки — `−L·cosθ·θ̇` при `θ̇ = q·cosφ − r·sinφ`; так считают `NativeRates` в v4 и синтетический генератор тестов. Правильный член — `+r·L·cosθ·sinφ`, то есть у хелпера противоположный знак и нет множителя cosθ.
  - Само замыкание v1 передаёт ωy = 0 и не затронуто. Но `TryRecoverDatumOffsetCore` передаёт реальный ωy, поэтому D смещается во всех воздушных окнах с креном и рысканием.
  - D входит в cfg-arm (`L = A_cfg + D`; этот arm ещё и отключает point model) и в телеметрический arm. Метрика качества смещения не видит: SD фаз ≈ 0.
  - Проверка:
    - регрессия по 26 452 парам кадров даёт b = −1,028, а хелпер подразумевает +1;
    - с исправленным знаком datum совпадает с точным во всех 16 фикстурах (≤ 0,003 ft); текущий код отклоняется до 0,098 ft, в одной фикстуре на −55 %;
    - на синтетике с заложенным D = 4 ft текущий путь даёт ошибку +15,5 %.
- **Сценарий:** в записанную часть захода попадает доворот, circling или de-crab. У лайнеров D ≈ 8–11 ft, и ошибка arm в 10–50 % от D даёт ошибку члена тангажа в `ReconstructedClosureFpm` и Residual. В текущих фикстурах эффект меньше 1 fpm.
- **Направление:** в версионированном изменении v1 с ревалидацией использовать `−cosθ·(q·cosφ − r·sinφ)` или attitude-datum. Исправить формулу в методологии и добавить тест `TryRecoverDatumOffset` с креном. Связано с LS-001.

### M-02. Проверка «settled gear» сравнивает с последней строкой целиком, а не с последней motion-строкой
- **Серьёзность:** Низкая · **Категория:** Баг · **Достоверность:** (a) подтверждено, (b) вероятно
- **Где:** `src/LandingStats.Core/TelemetryGeometryCalibration.cs:601-612, 1116-1123`; `TelemetryKinematicDiscontinuity.cs:205-213`; `TouchdownAnalysis.cs:30-57`
- **Описание:** `HasContactEvidence` требует `MotionSimulation`, поэтому non-motion строка в конце даёт `settledRuns = 0` и ложную `IncompleteSettledGear`. Барьеры двух видов разрывов ведут себя несогласованно.
- **Сценарий:**
  - (a) Анализ архива «посадка, пробег, телепорт» в Analyzer или тестах теряет timing-топологию первого сегмента. В приложении этого нет: `EpisodeProcessor` режет скачки заранее.
  - (b) Захват заканчивается Running-кадрами с MOTION SIMULATION = 0.
- **Направление:** сравнивать с последней motion-строкой или обрезать non-motion хвост в `Normalize`.

### M-03. `TryCalibrate` игнорирует принятую топологию; provenance противоречив
- **Серьёзность:** Низкая · **Категория:** Архитектура (контракт API) · **Достоверность:** Подтверждено; из приложения недостижимо
- **Где:** `src/LandingStats.Core/TouchdownAnalysis.cs:85-119, 145-194, 222-226, 337-341, 473-478, 911-958`; `TelemetryGeometryCalibration.cs:226-235`
- **Описание:**
  - Если роли из конфигурации приняты, а arm не задан, `TryCalibrate` заново выводит роли из телеметрии. В итоге arm берётся из конфигурации, но `GeometrySource = Telemetry` и `ArmRecoveredFromTelemetry = true`.
  - Point model может заменить результат по геометрии из конфигурации — вопреки комментарию в коде.
  - Резолвер приложения всегда задаёт arm вместе с ролями, поэтому на практике это не проявляется.
- **Направление:** один объект «решение по геометрии» (роли + arm + provenance), который вычисляется один раз.

### M-04. Две временные базы в коде анализа
- **Серьёзность:** Низкая · **Категория:** Архитектура · **Достоверность:** Подтверждено (латентно)
- **Где:** `src/LandingStats.Core/TouchdownImpactAnalysis.cs:86, 95`; `TelemetrySpatialGearTopology.cs:65-100`; `TelemetryGeometryCalibration.cs:242, 372, 606, 656, 687-700, 990, 1057-1095, 1120-1121`; `TouchdownAnalysis.cs:230, 433`
- **Описание:** корректность держится на неявном инварианте «`Prepare` перезаписывает `SimulationTimeSeconds` временем анализа». Публичный `TouchdownImpactAnalysis.Analyze` на сырых кадрах, где часы шли во время паузы, даёт `Incomplete`. Строки без конечного `SimulationTimeSeconds` делают доступность метрик несогласованной.
- **Направление:** везде `TimeOf` или типизированное представление «подготовленных кадров».

### M-05. Коды решений вводят в заблуждение; каскад моделей управляется строками
- **Серьёзность:** Низкая · **Категория:** Сопровождаемость · **Достоверность:** Подтверждено
- **Где:** `src/LandingStats.Core/TelemetryContactPointReconstruction.cs:130-131, 160-170, 219-307`; `TouchdownClosureReconstruction.cs:90-94, 374-377`; `TouchdownAnalysis.cs:183-194`
- **Описание:**
  - Неконечная высота останавливает калибровку, а журнал пишет `window_complete`.
  - `point_geometry_unavailable` выставляется один раз до цикла, и все последующие отказы получают ту же причину.
  - Каскад v2 → v3 → v4 ветвится по строковым литералам кодов журнала, так что переименование кода меняет поток управления.
  - При немонотонном времени причина — `insufficient_distinct_precontact_samples`.
  - Для захватов без контактов журнал пишет `arm_unavailable: No supported gear topology`.
  - Полевые разборы в BACKLOG опираются на эти причины.
- **Направление:** enum вместо строк и конкретная причина в каждой точке выхода.

### M-06. Повторная подготовка данных и O(n)-аллокации на каждый контакт
- **Серьёзность:** Низкая · **Категория:** Производительность · **Достоверность:** Подтверждено
- **Где:** `src/LandingStats.Core/TouchdownAnalysis.cs:24, 123, 160, 386, 683-695`; `TelemetryGeometryCalibration.cs:226, 442, 561-562, 603, 1116-1123`; `TouchdownImpactAnalysis.cs:88-104`; `TouchdownClosureReconstruction.cs:109, 260-358`; `TelemetryContactPointReconstruction.cs:92-93, 118`
- **Описание:**
  - `Prepare` (MemberwiseClone ~610 Б) выполняется для сегмента 6–7 раз. На 65 536 кадрах это ≈ 40 МБ за проход и ~250–300 МБ мусора на эпизод.
  - Топология выводится дважды.
  - На каждый контакт весь сегмент копируется; `SanitizeGroundAltitude` выделяет 5 массивов; `FilteredGroundAltitude` ищет кадр линейно, O(n·w).
  - Окна калибровки point model — O(W²) на LINQ.
  - Квадратичного обхода всего списка нет.
- **Направление:** подготовить данные один раз; ограничить санитизацию окном фита.

### M-07. Дублированные фильтры и константы, мёртвый параметр
- **Серьёзность:** Низкая · **Категория:** Сопровождаемость · **Достоверность:** Подтверждено
- **Где:** `src/LandingStats.Core/TouchdownAnalysis.cs:9, 683-731`; `TouchdownClosureReconstruction.cs:260-358`; `TelemetryGeometryCalibration.cs:170, 656-660, 1317, 1574-1610`; `TouchdownImpactAnalysis.cs:64-66`; `src/LandingStats.App/Models/LandingRecord.cs:383-385`; `TelemetrySpatialGearTopology.cs:62`
- **Описание:**
  - Три разных фильтра выбросов высоты земли с одним порогом 5 ft.
  - Проверка стабильности берёт немаскированную высоту, другие места — маскированную.
  - `MinimumIntegrationDurationSeconds` недостижим.
  - Пороги 1,2/1,5 G и разрыв эпизода 10 с объявлены дважды.
  - Порог «есть сжатие» в разных местах равен 0, 0,001 и 1e-6.
  - Четыре решателя с разными порогами пивота.
  - Около 9 копий `IsFinite`.
- **Направление:** общий модуль констант и математики.

### M-08. Размер классов и смешение ответственностей
- **Серьёзность:** Низкая · **Категория:** Архитектура · **Достоверность:** Подтверждено
- **Где:** `src/LandingStats.Core/TelemetryGeometryCalibration.cs` (1675 строк); `TouchdownAnalysis.cs:70-286, 318-508`; `TelemetryContactPointReconstruction.cs:172-325`
- **Описание:**
  - Один статический класс объединяет нормализацию, 4 стратегии топологии с арбитражем, регрессию arm, 2 оценщика datum и фильтры выбросов.
  - Provenance в `CreateResult` собирается из ~20 тернарных выражений — отсюда M-03.
  - v2/v3/v4 реализованы в одном методе с замыканиями.
  - Тестировать можно только end-to-end через `Analyze`.

### Проверено в этом направлении, проблем не найдено
- Рефакторинг: аналитические файлы Core не изменены; `EpisodeProcessor` повторяет последовательность HEAD `MainWindow.ProcessEpisodeAsync`.
- Единицы (ft/s, knots, deg, rad/s, проценты) и знаки (descent-positive, derotation) согласованы.
- Окна контакта, latch, G и impact корректно ограничены; индексы на краях окон безопасны.
- Пустые и короткие входы защищены. Исключений нет для вертолётов, тейлдраггеров, поплавков, отскоков и при отсутствии каналов шасси. Недоступность выражается NaN или `Unavailable`, а не нулём.
- Линейная алгебра корректна: pivoting, правило Крамера, вращения Якоби, `BoundedLinearPrediction`.
- Детерминизм на net48 и net10 не зависит от порядка коллекций и культуры.

---

## 13. Google Drive

Объединены с другими разделами:
- Disconnect при ожидающем запросе синхронизации → U-05 (повышено до Высокой);
- повреждённые облачные метаданные блокируют старт runtime → U-04;
- гонка `Progress` → A-05;
- тестовые двойники в production → S-04;
- откат pending delete при необратимом tombstone → K-01.

Ни одна находка в BACKLOG не заведена; близкий контекст — LS-012 и LS-020.

### G-01. Долгая локальная фаза синхронизации по кругу эскалируется в перезапуск всего приложения
- **Серьёзность:** Высокая (при срабатывании последствие критичное) · **Категория:** Архитектура / Баг · **Достоверность:** Вероятно — механизм подтверждён, порог зависит от объёма данных и железа
- **Где:** `src/LandingStats.App/GoogleDrive/GoogleDriveBackupService.cs:300, 557-572, 674-706, 709-777, 1235-1246`; `src/LandingStats.App/Runtime/CloudSyncRuntimeOwner.cs:137-139, 171-172`; `ComponentSupervisor.cs:175-210`; `RuntimeComponentCatalog.cs:37`; `src/LandingStats.App/MainWindow.Runtime.cs:31-33`; `src/LandingStats.ProcessControl/GuardianPolicy.cs:31`
- **Описание:**
  - `Progress` cloud-owner публикуется только на границах API-вызовов.
  - Хеширование несвязанных посадок идёт синхронно, без `Progress` и без проверки токена отмены. Это `LoadLocalContent` → `ExportForBackup`: распаковка, десериализация, сериализация, gzip Optimal и SHA-256 под `_landingGate`. Так же идёт metadata-merge.
  - Цепочка эскалации:
    1. Через OwnerStall 5 с supervisor шлёт probe и ждёт 2 с.
    2. Затем `StopAsync` ждёт 3 с, но цикл отмену не видит.
    3. Owner эскалируется как сбой runtime, и guardian перезапускает App без безопасной точки — запись и SimConnect обрываются.
  - State сохраняется только в конце (`:300`), поэтому следующий старт повторяет ту же фазу — получается цикл.
  - Тест `CloudRuntimeRegressionTests.cs:69-77` подтверждает, что заблокированный не-UI owner эскалируется.
- **Сценарий:** примерно 150–300+ посадок уже лежат в облаке, но в state для них нет валидных ссылок. Так бывает:
  - при переносе на новый ПК;
  - при возврате на прежний аккаунт после смены (`SwitchAccount` очищает ссылки);
  - после сброса state (G-05).
- **Направление:** `Progress` и `ThrowIfCancellationRequested` на каждую посадку; bounded operation для локальной фазы; промежуточные checkpoint ссылок.

### G-02. Одна «плохая» посадка навсегда обрывает всю синхронизацию
- **Серьёзность:** Высокая · **Категория:** Баг / Потеря резервирования · **Достоверность:** Подтверждено (ранее: п. 2 отчёта 2026-08-29 — частично сохраняется в другой форме)
- **Где:** `src/LandingStats.App/GoogleDrive/GoogleDriveBackupService.cs:513-706` (нет изоляции по посадке), `524, 603, 610, 651, 658, 722, 732-733, 747, 1243`; `src/LandingStats.App/Runtime/IsolatedGoogleDriveApi.cs:122`; `src/LandingStats.App/GoogleDrive/BoundedCloudDownload.cs:12`; `CloudSyncRuntimeOwner.cs:186`
- **Описание:**
  - Любая ошибка обработки одной посадки обрывает `SyncAsync` ещё до синхронизации настроек и сохранения state. Owner перебрасывает исключение, supervisor повторяет попытку с backoff до 30 с — бесконечно.
  - Причины, которые сами не исчезнут:
    - нечитаемая локальная запись;
    - запись в индексе без файла;
    - облачная посадка новее `CurrentFormatVersion` — в том числе запись v2 на ПК с 0.9.4 (E-02);
    - несовпадение sha256 или id;
    - размер больше 64 MiB;
    - постоянный 403.
  - Посадки после проблемной и настройки больше не резервируются. Пользователь видит только текст ошибки, без указания посадки.
  - `LandingRepository.Import` пишет temp без flush (B-09). После сбоя питания копия может стать нечитаемой, и восстановить её из облака синхронизация уже не сможет.
- **Направление:** карантин id с предупреждением; продолжать синхронизацию остальных посадок и настроек; нечитаемую локальную копию восстанавливать из облака.

### G-03. Форк ревизий в Drive никогда не разрешается
- **Серьёзность:** Средняя · **Категория:** Архитектура / Баг · **Достоверность:** Подтверждено (ранее: п. 2 отчёта 2026-08-29 — частично)
- **Где:** `src/LandingStats.App/GoogleDrive/GoogleDriveBackupService.cs:285-289, 295-298, 455-467, 487-496, 686-689, 1198-1214`; тест `GoogleDriveBackupRegressionTests.cs:874-920`
- **Описание:**
  - Если у одной базы два наследника, `ResolveRevisionGroup` бросает исключение. Посадка навсегда уходит в `ConflictedLandingIds`: её больше не выгружают и не скачивают. Настройки получают `SettingsConflict` и перестают синхронизироваться.
  - Metadata-merge к форку не применяется, даже если ветки различаются только ВПП. Тест `SiblingRevisionConflictsAreIsolated` закрепляет такое поведение как постоянное.
  - Разрешить конфликт посадки можно только её удалением; для настроек способа нет вовсе.
- **Сценарий:** два ПК одновременно меняют одну посадку или настройки → предупреждение о конфликте висит на всех устройствах навсегда.
- **Направление:** детерминированно сливать ветки форка или выбирать одну, а проигравшую отправлять в корзину.

### G-04. Любая не-5xx ошибка token endpoint стирает refresh token; refresh выполняется на каждый вызов
- **Серьёзность:** Средняя · **Категория:** Безопасность / Баг (учётные данные) · **Достоверность:** Подтверждено (по коду и IL Google.Apis.Auth 1.75); частота 4xx — гипотеза
- **Где:** `src/LandingStats.App/GoogleDrive/GoogleOAuthTokenStore.cs:57, 145-149`; `GoogleOAuthClient.cs:69-75, 89-94`; `src/LandingStats.App/Runtime/CloudWorkerRole.cs:70, 74`; `IsolatedGoogleDriveApi.cs:190-193`
- **Описание:**
  - Цепочка стирания токена:
    1. `FetchTokenAsync` при `TokenResponseException` со статусом вне 500–599 вызывает `DeleteTokenAsync`.
    2. `Clear()` удаляет копию воркера.
    3. Воркер возвращает `Failed` с пустым `CredentialHash`.
    4. `ImportSnapshot` при пустом hash удаляет основной токен, независимо от outcome. Этот путь задумывался только для invalid_grant.
  - Воркер каждый раз создаёт `UserCredential` без access token, поэтому каждый API-вызов делает refresh. В 0.9.4 токен жил в памяти около часа.
- **Сценарий:** первичная выгрузка 500 посадок — это около 500 refresh. Один ответ 429 или 400 — и пользователь отключён, нужна повторная авторизация. В UI при этом только «Cloud API operation failed: TokenResponseException».
- **Направление:** `DeleteAsync` не должен удалять файл; стирать токен только при NotSignedIn; кешировать access token на время sync scope.

### G-05. Нечитаемый state синхронизации молча считается пустым и записывается поверх настоящего
- **Серьёзность:** Средняя · **Категория:** Потеря данных · **Достоверность:** механизм подтверждён; триггер — гипотеза
- **Где:** `src/LandingStats.App/GoogleDrive/GoogleDriveSyncStateRepository.cs:43-99, 114-169`; `GoogleDriveBackupService.cs:147, 186-220, 1018-1033`
- **Описание:**
  - При ошибках I/O, доступа или сериализации `LoadCore` возвращает `new GoogleDriveSyncState()`.
  - `AddPendingDelete` сразу сохраняет этот пустой state. `SyncAsync` уходит в legacy `SwitchAccountState`, и `SaveAfterAccountSwitch` фиксирует сброс; ветка `:204` использует `Save`, а не `SavePreservingPendingDeletes`.
  - Последствия:
    - теряются PendingDeletes — удалённые посадки остаются в облаке и на других ПК;
    - пустой `LastSettingsHash` включает режим «first union»: облачные настройки перезаписывают несинхронизированные локальные, вплоть до exe.xml автозапуска;
    - теряются все ссылки, что питает G-01.
- **Сценарий:** антивирус или программа резервного копирования держит файл state в момент чтения.
- **Направление:** различать «файла нет» и «файл не читается»; при ошибке чтения не перезаписывать state.

### G-06. Горячий цикл: кеш говорит «вошёл», а токен не читается
- **Серьёзность:** Средняя · **Категория:** Баг / Утечка ресурсов (CPU, I/O) · **Достоверность:** Подтверждено
- **Где:** `src/LandingStats.App/GoogleDrive/GoogleOAuthTokenStore.cs:32-41, 70-82`; `src/LandingStats.App/Runtime/IsolatedGoogleDriveApi.cs:59, 157`; `CloudSyncRuntimeOwner.cs:144-165, 184`
- **Описание:**
  - `ExportSnapshot` бросает NotSignedIn, но не обновляет `_hasRefreshToken`, поэтому `Enabled` остаётся true.
  - Owner сразу запускает новый `SyncAsync` без backoff. Каждая итерация читает state и создаёт scratch-каталог с тремя fsync.
  - Supervisor видит Progress и не тормозит цикл. Цикл идёт до перезапуска или Disconnect.
- **Сценарий:** токен удалён или повреждён во время работы либо DPAPI перестал расшифровывать.
- **Направление:** обновлять кеш через `Load()`; делать backoff после NotSignedIn.

### G-07. Каждый Drive API-вызов — новый процесс и полный OAuth refresh
- **Серьёзность:** Средняя · **Категория:** Производительность / Архитектура · **Достоверность:** Подтверждено
- **Где:** `src/LandingStats.App/Runtime/IsolatedGoogleDriveApi.cs:149-205`; `src/LandingStats.App/GoogleDrive/GoogleOAuthClient.cs:69-75`; `GoogleOAuthTokenStore.cs:52-62, 135-143`; `GoogleDriveBackupService.cs:148, 150, 272, 291, 987`
- **Описание:**
  - Каждый вызов:
    - запускает WorkerHost с загрузкой WPF-exe;
    - экспортирует DPAPI-копию токена и делает refresh;
    - создаёт новый DPAPI-блоб;
    - импортирует его в parent с fsync;
    - делает ещё около пяти fsync job-файлов.
  - Синхронизация без изменений — это 5 процессов и 5 refresh: settings.json скачивается всегда, даже при равных hash.
  - Первичная выгрузка или восстановление N посадок — порядка N процессов и N refresh.
  - Это в разы медленнее, чем в 0.9.4, и усиливает риск G-04.
- **Направление:** один воркер на sync scope с кешем access token; не скачивать настройки при совпадении hash.

### G-08. Восстановление истории из облака остаётся O(n²)
- **Серьёзность:** Средняя · **Категория:** Производительность · **Достоверность:** Подтверждено (ранее: п. 7 отчёта 2026-08-29 — частично)
- **Где:** `src/LandingStats.App/GoogleDrive/GoogleDriveBackupService.cs:372-378, 610-613, 658-665, 747-751`; `src/LandingStats.App/Storage/LandingRepository.cs:185-198, 307-350, 545-553, 561-570`
- **Описание:**
  - На каждую скачанную или слитую посадку `Import` полностью перезаписывает индекс (gzip Optimal, теперь с fsync), вызывает `LoadAll()` с сортировкой и обходит каталог в `GetBackupFingerprint`. Всё это — под `_landingGate`, общим с UI и коммитом эпизодов.
  - Каждая синхронизация к тому же вызывает `_landings.Delete` с обходом каталога для каждого маркера в корзине.
- **Сценарий:** на новом ПК с 1000 посадок в облаке — 1000 перезаписей индекса и 1000 обходов каталога.
- **Направление:** пакетный импорт с одной записью индекса; словарь id → путь на всю синхронизацию.

### G-09. UI показывает «Connected», а фоновая синхронизация молча не идёт
- **Серьёзность:** Средняя · **Категория:** Баг (вводящий в заблуждение статус) · **Достоверность:** Подтверждено; триггеры узкие
- **Где:** `src/LandingStats.App/Runtime/IsolatedGoogleDriveApi.cs:53-55, 100-119`; `src/LandingStats.App/MainWindow.xaml.cs:994, 1135-1151, 1186-1188, 1316-1377`
- **Описание:**
  - UI считает подключение по наличию токена, а runtime требует ещё `credentials.json Enabled`.
  - Вход записывает `Enabled = false`, а включение происходит только в `RunGoogleDriveSyncAsync(true)`.
  - Расхождение возникает, если:
    - этот шаг не выполнился;
    - после Disconnect не удалось удалить токен;
    - пользователь вышел из цикла G-01 через Disconnect.
  - После рестарта статус — «Connected», а фоновые и стартовые синхронизации тихо выходят по `!CloudEnabled`.
- **Направление:** единый источник статуса; явно показывать «приостановлено».

### G-10. PendingDeletes растёт у пользователей без Drive
- **Серьёзность:** Низкая · **Категория:** Утечка / рост · **Достоверность:** Подтверждено (ранее: п. 4 отчёта 2026-08-29 — часть про рост сохраняется)
- **Где:** `src/LandingStats.App/MainWindow.xaml.cs:510, 521-529`; `src/LandingStats.App/GoogleDrive/GoogleDriveBackupService.cs:61-107`; `GoogleDriveSyncStateRepository.cs:84-99, 346-390`
- **Описание:**
  - Каждое удаление вызывает `AddPendingDelete` (полные Load + Save с fsync), даже если пользователь не входил в Drive.
  - При первом подключении `SwitchAccount` этот список всё равно отбрасывает.
  - Сессионные множества удалений растут в памяти.

### G-11. Disconnect не отзывает токен; DPAPI-копии могут остаться на диске
- **Серьёзность:** Низкая · **Категория:** Безопасность (гигиена учётных данных) · **Достоверность:** Подтверждено
- **Где:** `src/LandingStats.App/GoogleDrive/GoogleOAuthClient.cs:166-171`; `src/LandingStats.App/Runtime/RuntimeScratchLease.cs:59`; `IsolatedGoogleDriveApi.cs:151-157, 199-204`; `DurableFile.cs:15-23`; `GoogleOAuthTokenStore.cs:227`
- **Описание:**
  - Refresh token у Google остаётся действующим.
  - `cloud\jobs\<id>\token.json` не собирается, если parent упал между экспортом токена и `EnterWorker`.
  - Остатки `.w<guid>.tmp` и `google-drive-token.json.tmp` никто не чистит, а экспорт и импорт теперь выполняются на каждый вызов.
  - Риск ограничен DPAPI CurrentUser.

### G-12. Нет retry/backoff на 429/5xx; ошибки непрозрачны
- **Серьёзность:** Низкая · **Категория:** Надёжность / Сопровождаемость · **Достоверность:** Подтверждено
- **Где:** `src/LandingStats.App/GoogleDrive/GoogleDriveApiClient.cs:73-77`; `src/LandingStats.App/Runtime/CloudWorkerRole.cs:69-70, 79`; `IsolatedGoogleDriveApi.cs:173, 193`; `CloudSyncRuntimeOwner.cs:186`; `ComponentSupervisor.cs:166-170`
- **Описание:**
  - Работает только стандартная политика Google.Apis для 503, поэтому любая временная ошибка перезапускает весь sync с нуля.
  - Воркер отдаёт только имя типа исключения — квоту, лимит и 404 не различить.
  - Любой OCE, включая таймаут HttpClient, роняет воркер; в parent это выглядит как «Worker ended before committing its result».

### G-13. OAuth client secret в `AssemblyMetadata`
- **Серьёзность:** Низкая · **Категория:** Безопасность · **Достоверность:** Подтверждено (не регрессия; практика допустимая)
- **Где:** `src/LandingStats.App/GoogleDrive/GoogleDriveOAuthConfiguration.cs:11-38`; `src/LandingStats.App/LandingStats.App.csproj:13-19`
- **Описание:**
  - Для Desktop-клиента Google секрет не конфиденциален: используется PKCE, секрет не уходит в URL браузера и не передаётся в PR-сборки.
  - Остаточный риск — имитация клиента или расход квоты. Ротировать секрет можно только релизом.

### G-14. Маркеры удаления живут только в корзине Drive
- **Серьёзность:** Низкая · **Категория:** Потеря консистентности · **Достоверность:** Вероятно
- **Где:** `src/LandingStats.App/GoogleDrive/GoogleDriveBackupService.cs:340-379, 691-706`
- **Описание:**
  - Корзина очищается через 30 дней или вручную. Устройство, которое не синхронизировалось дольше, заново выгрузит удалённую посадку, а остальные её не вернут из-за ledger — данные расходятся.
  - Ошибочно удалённую посадку нельзя восстановить из корзины: её снова отправят в корзину.

### G-15. Дубликат id в листинге отклоняет весь ответ List
- **Серьёзность:** Низкая · **Категория:** Баг · **Достоверность:** Гипотеза
- **Где:** `src/LandingStats.App/Runtime/CloudOperationData.cs:84`; `CloudWorkerRole.cs:75`; `src/LandingStats.App/GoogleDrive/GoogleDriveApiClient.cs:100-114`
- **Описание:** пагинация files.list не снапшотная: файл, изменённый между страницами, может прийти дважды, и тогда вся синхронизация (временно) падает. Безопаснее дедуплицировать.

### G-16. Мёртвый и вводящий в заблуждение код
- **Серьёзность:** Низкая · **Категория:** Сопровождаемость · **Достоверность:** Подтверждено
- **Где:** `src/LandingStats.App/GoogleDrive/GoogleDriveBackupService.cs:55-59, 126-138, 1319-1325`; `GoogleDriveSyncStateRepository.cs:321-344`; `GoogleOAuthClient.cs:32, 47`; `src/LandingStats.App/MainWindow.xaml.cs:90`
- **Описание:**
  - `DeleteLandingAsync` и `CancelLandingDeletionAsync` используются только в тестах; `Cancel…` к тому же не отменяет ledger.
  - `ClearSessionPendingDeletes` и `ResetForRoot` не используются.
  - `AccountEmail` в production всегда null, поэтому `SignInAsync` возвращает null.
  - Parent держит интерактивный `GoogleOAuthClient` только ради `IsSignedIn` и `SignOut`.

### Статус пунктов отчёта 2026-08-29

| № | Статус | Основание |
|---|---|---|
| 1 | Исправлен | Посадка, которой нет в облаке, загружается заново. Локальное удаление — только по маркеру in-app pending delete плюс ledger. |
| 2 | Частично (G-02, G-03) | Конфликт local/remote больше не роняет всю синхронизацию (`ConflictedLandingIds`, metadata-merge). Форки и «плохие» посадки остаются. |
| 3 | Исправлен; новый путь к симптому — U-05 | Таймаут 3 мин в UI и в `Call`, Cancel; воркер с loopback-listener завершается. |
| 4 | Частично (G-10) | Удаление не зависит от ошибок state и не ждёт `_syncGate`. Неограниченный рост остался. |
| 5 | Исправлен | `RuntimeIsComplete` допускает посторонние файлы. |
| 6 | Исправлен | Закрытие ждёт `StopRuntimeAsync`, прежде чем освобождать ресурсы. |
| 7 | Частично (G-08) | Стартовые fingerprints считаются за один обход. Путь восстановления — O(n²). |
| 8 | Исправлен | `HasRefreshToken` кешируется, DPAPI вызывается вне UI. |
| 9 | Исправлен | Автозапуск инициализируется до ожидания синхронизации. Исключение — сценарий восстановления настроек, где возможна вечная пауза по U-05. |
| 10 | Исправлен | Скрытие без force блокируется, пока идёт удаление. |
| 11 | Исправлен | `GoogleDriveBusy` вычисляется. |

### Проверено в этом направлении, проблем не найдено
- Потеря ответа после commit на сервере не дублирует посадку (тест `cloud-sync-lost`).
- Поздние результаты после logout или смены аккаунта отбрасываются: сверяются job, CredentialRevision, epoch и `_enabled`, `ImportSnapshot` — CAS по hash.
- Токен: DPAPI CurrentUser с entropy, атомарная запись, открытого текста на диске нет; ошибки воркера передаются без сообщений.
- Границы: List — не больше 65 536 с корректным циклом `nextPageToken`; download и result — не больше 64 MiB; request — не больше 512 KiB.
- Некооперативные вызовы: дедлайн 120 с (3 мин для OAuth); дерево процесса воркера завершается до следующей операции.
- OAuth loopback: 127.0.0.1, PKCE S256, URL браузера собирается из констант.
- Ручное удаление в Drive — файлов, папки Landings или root — не удаляет локальную историю; гонки удаления и синхронизации закрыты.

---

## 14. Launcher, guardian, updater, сборка и CI

Объединены с другими разделами:
- SessionEnding в launcher → U-02;
- сбой ack stop-intent → U-03;
- повреждённый engine → B-11;
- CI не запускает Runtime-тесты → T-01;
- binding redirects WorkerHost → S-01;
- гигиена репозитория → S-06.

### H-01. Сбой updater после commit «перезапускает» приложение, пока жив старый guardian, — перезапуск теряется
- **Серьёзность:** Высокая · **Категория:** Баг · **Достоверность:** Подтверждено; вариант (b) частично существовал в 0.9.x
- **Где:** `src/LandingStats.App.Updater/Program.cs:30-39, 56-95, 362-380`; `src/LandingStats.ProcessControl/GuardianInstance.cs:20-30`; `GuardianGeneration.cs:107-116, 191-195`; `src/LandingStats.App/MainWindow.xaml.cs:181-190`
- **Описание:**
  - После `Commit()` guardian ставит `_stop = Update`, и App закрывается. Сам guardian выходит только после выхода App — до 20 с.
  - Updater в это время качает manifest, подпись и пакет. При любом исключении он сразу вызывает `TryRestartExistingApplication(target)`, не дожидаясь выхода target.
  - Если старый guardian ещё владеет `Local\MSFSLandingStats.Guardian`, новый launcher лишь сигналит `.Activate` и выходит. Старый guardian завершается с Update-intent и ничего не перезапускает.
  - Итог: приложение закрыто, а MessageBox сообщает «The existing application was preserved».
  - Если сбой детерминированный и происходит уже после выхода guardian, перезапуск срабатывает. Но `CheckAndInstallAsync` при каждом старте без ограничения снова запускает то же обновление — получается цикл, и на каждом витке появляются процесс updater и MessageBox.
- **Сценарий:**
  - (a) Во время drain GitHub отвечает 404/5xx или рвётся сеть → приложение не запущено.
  - (b) Bundle лежит в `C:\Program Files\…` → `File.Move(target, backup)` бросает `UnauthorizedAccessException` → бесконечный цикл обновления.
- **Направление:** в catch дождаться выхода target и освобождения mutex, затем перезапускать; запоминать детерминированный отказ установки (backoff или «отложено»).

### H-02. Updater открывает commit-event только после ready — commit может потеряться
- **Серьёзность:** Высокая · **Категория:** Гонка · **Достоверность:** Подтверждено; частота низкая. В LS-014 эти гонки отмечены проверенными, но fixture их маскирует.
- **Где:** `src/LandingStats.App.Updater/Program.cs:49-50, 279-282, 291-295`; `src/LandingStats.ProcessControl/GuardianUpdateLease.cs:29-37, 46-55`; `GuardianGeneration.cs:106-120`; `tests/LandingStats.App.RegressionTests/GuardianUpdateRegressionTests.cs:68`
- **Описание:**
  - Guardian опрашивает ready раз в 20 мс. Увидев его, сразу делает `Commit()`, а в `finally` через `RetireAsync()` закрывает свой единственный handle события.
  - Updater вызывает `SignalReady()` и только потом `OpenExisting(commit)`. Если его поток задержался дольше окна опроса, объект события уже уничтожен → `WaitHandleCannotBeOpenedException` → обновление провалено, хотя App уже закрывается (дальше — H-01).
  - Тестовый helper открывает оба события до ready и этой гонки не видит.
- **Сценарий:** обновление при запущенном MSFS (CPU нагружен) или первый JIT `WaitForCommitAsync`.
- **Направление:** открывать и удерживать commit до `SignalReady`; в тесте воспроизводить реальный порядок вызовов.

### H-03. Guardian молча и бесконечно перезапускает приложение при детерминированных отказах старта
- **Серьёзность:** Высокая · **Категория:** Архитектура · **Достоверность:** Подтверждено
- **Где:** `src/LandingStats.ProcessControl/GuardianGeneration.cs:68, 166-172, 189, 216-217`; `GuardianPolicy.cs:10, 26, 29`; `src/LandingStats.App.Launcher/Program.cs:85, 100-113`; `src/LandingStats.App/App.xaml.cs:47, 70-91`
- **Описание:**
  - Ошибки `PrepareRuntime` пишутся только в guardian.log и повторяются вечно с паузой до 30 с. В 0.9.4 пользователь видел MessageBox.
  - `StartAsync` ждёт только pipe-connection (до 2 мин) и не следит за `_process.Exit`. Падение App до handshake обнаруживается лишь через 2 минуты.
  - Код выхода App не анализируется. `Shutdown(70)` из catch в `OnStartup` даёт рестарт примерно каждые 30 с навсегда, и каждый раз пишется запись в `crash.log`, у которого нет ротации.
  - Повторный запуск пользователем в это время ничего не делает.
  - Сторона runtime того же цикла описана в U-04.
- **Сценарий:**
  - Диск переполнен или антивирус блокирует распаковку → остаётся только невидимый резидентный launcher.
  - Повреждённое состояние, при котором падает `new MainWindow()` → App циклически перезапускается невидимым, а crash.log растёт на мегабайты в сутки.
- **Направление:** распознавать повторяющийся отказ (одинаковые стадия и exit code N раз подряд) и показывать ошибку; наблюдать `Exit` во время handshake; ротировать crash.log.

### H-04. После сна или гибернации guardian видит «устаревшее» здоровье и сразу убивает приложение
- **Серьёзность:** Высокая · **Категория:** Баг · **Достоверность:** Вероятно — вывод опирается на то, что QPC идёт во сне; проверяется сном дольше 60 с
- **Где:** `src/LandingStats.ProcessControl/GuardianPolicy.cs:29-33, 38`; `GuardianGeneration.cs:36, 81, 187-189`
- **Описание:**
  - `Decide` считает возраст `ControlQpc`/`UiQpc` из последнего пакета относительно текущего QPC. После пробуждения первые тики guardian и первые пакеты App ещё несут значения, снятые до сна.
  - Сон дольше 60 с даёт `Restart` — немедленный `TerminateJobObject`. Сон дольше 30 с даёт `PrepareRecovery`.
  - Детекции разрыва между собственными тиками у guardian нет.
- **Сценарий:** ПК с открытым приложением спит 5 минут → в guardian.log появляется `Restart`, окно исчезает и появляется снова.
- **Направление:** если пауза между тиками намного больше интервала проверки, давать grace до свежего пакета; либо мерить прогресс счётчиками, а не абсолютным QPC.

### H-05. Поздний Ack после 15-секундного дедлайна: App завершает себя, а guardian может закоммитить update
- **Серьёзность:** Средняя · **Категория:** Гонка · **Достоверность:** Подтверждено; тайминг редкий
- **Где:** `src/LandingStats.ProcessControl/GuardianClient.cs:59-63, 111-127`; `src/LandingStats.App/App.Guardian.cs:27-36`; `src/LandingStats.ProcessControl/GuardianGeneration.cs:98-123`; `GuardianUpdateLease.cs:27-35`; `src/LandingStats.App/Updates/ReleaseUpdater.cs:117-118, 181-184`
- **Описание:**
  - `RequestAsync` ждёт Ack 15 с и обнуляет `_pending`. Guardian об этом сроке не знает: у него verify, `OwnedProcess.Start`, до 10 с ожидания ready и commit.
  - Поздний Ack → «Obsolete guardian acknowledgement» → канал закрыт → `Exit(72)` без drain.
  - При позднем accept App уже показал «Update.Timeout», а guardian всё равно убьёт его через 20 с.
- **Сценарий:** медленный CreateProcess (антивирус сканирует новый EXE) около 5 с плюс ready на ~10-й секунде.
- **Направление:** передавать guardian дедлайн запроса либо после таймаута опрашивать `UpdateAccepted` и не считать поздний Ack нарушением протокола.

### H-06. На штатное завершение даётся фиксированные 20 с без учёта прогресса
- **Серьёзность:** Средняя · **Категория:** Потеря данных · **Достоверность:** Вероятно
- **Где:** `src/LandingStats.ProcessControl/GuardianGeneration.cs:191-195`; `GuardianPolicy.cs:13`; `src/LandingStats.App/MainWindow.Shutdown.cs:56-84`
- **Описание:**
  - После любого stop-intent guardian ждёт выхода не дольше 20 с и убивает дерево. Health-пакеты, которые App шлёт во время drain, игнорируются.
  - Для Update отсчёт начинается ещё с момента commit.
  - Это тот же параметр, что и дедлайн recovery, и App не может его продлить.
  - Расходится с планом §7.3: «медленно, но идёт» — не то же самое, что «завис».
- **Сценарий:** медленный диск или антивирус при закрытии с большим хвостом журнала → kill посреди `StopRuntimeAsync`.
- **Направление:** отдельный stop-deadline, который продлевается прогрессом shutdown, с жёстким верхним пределом.

### H-07. Updater в guardian-пути стартует скрытым — его единственный UI, MessageBox ошибки, может быть невидим
- **Серьёзность:** Средняя · **Категория:** Баг · **Достоверность:** Гипотеза (проверить искусственным отказом)
- **Где:** `src/LandingStats.ProcessControl/GuardianUpdateLease.cs:27`; `src/LandingStats.App/Runtime/OwnedProcess.cs:29-30, 63`; `src/LandingStats.App.Updater/Program.cs:30-38`
- **Описание:**
  - `OwnedProcess.Start` по умолчанию передаёт `hideWindow: true` (`SW_HIDE`). Windows применяет это к первому показу top-level окна процесса, а у updater это MessageBox в catch.
  - Вероятный итог — невидимый модальный диалог и процесс, который висит вечно.
  - В legacy-пути `WindowStyle` не применялся, и диалог был виден.
- **Направление:** для updater передавать `hideWindow: false`.

### H-08. Authenticode не покрывает собственный `LandingStats.Archives.dll`; пустой отпечаток даёт тихо неподписанную сборку
- **Серьёзность:** Средняя · **Категория:** Сборка · **Достоверность:** Подтверждено
- **Где:** `build-app.ps1:15-27, 137-143`; `verify-authenticode-build.ps1:34`
- **Описание:**
  - Подписываются updater, App, Core, Runtime.Contracts и WorkerHost, а Archives.dll — нет. Проверка идёт по тому же захардкоженному списку и пропуска не замечает.
  - В наборе `Signed` отпечаток из пробелов проходит `ValidateNotNullOrEmpty`, после чего `Sign-ApplicationFile` тихо выходит. Локально получается неподписанная «signed»-сборка.
- **Направление:** подписывать и проверять по перечислению payload: все `LandingStats.*` обязательны, сторонние — по явному списку. Для `Signed` — fail-closed.

### H-09. Gate совместимости обновлений не проверяет опубликованный 0.9.4
- **Серьёзность:** Средняя · **Категория:** Сборка · **Достоверность:** Подтверждено
- **Где:** `verify-published-client-updates.ps1:28-29`; `.github/workflows/build.yml:305`
- **Описание:**
  - Список baselines заканчивается на 0.9.3, и release job жёстко задаёт `$previousClientVersion = '0.9.3'`. Последний и самый массовый клиент — 0.9.4; AGENTS.md и план §7.2 требуют каждый раз заново определять базовую версию.
  - Новый межверсионный контракт guardian → updater (13 аргументов, `--commit-event`) не покрыт никаким gate.
- **Направление:** добавить 0.9.4, вычислять предыдущую версию из последнего релиза, добавить fixture handoff'а guardian.

### H-10. Цепочка поставки CI и секреты (не регрессия)
- **Серьёзность:** Средняя · **Категория:** Безопасность · **Достоверность:** подтверждено по коду; для эксплуатации нужна компрометация внешней стороны
- **Где:** `.github/workflows/build.yml:29, 32, 58-66, 74, 83, 87, 101, 190-197, 200, 249, 254-316`
- **Описание:**
  - SimConnect DLL берутся из плавающего `game_versions[0]`, без фиксированной версии и хеша, и попадают внутрь подписанного bundle.
  - URL из того же внешнего JSON подставляется через `${{ steps.sdk.outputs.download_url }}` прямо в текст PowerShell — в подписывающей сборке, где сертификат уже подключён.
  - Actions закреплены тегами, а не SHA.
  - `RELEASE_SIGNING_KEY_PKCS8_B64` лежит в env шага, который скачивает и исполняет 21 старый клиент и кандидат. То же в release job, вместе с `GH_TOKEN`.
- **Направление:** закрепить SDK по версии и SHA-256; передавать выводы через env; закрепить actions по SHA; вынести подпись манифеста в отдельный шаг.

### H-11. Payload по-прежнему пропускает любые `*.dll`
- **Серьёзность:** Низкая · **Категория:** Сборка · **Достоверность:** Подтверждено
- **Где:** `src/LandingStats.App.Launcher/Program.cs:265-270`; `build-app.ps1:117-125`; `verify-previous-client-update.ps1:92-97`; `src/LandingStats.App/Runtime/ProcessingEngineLease.cs:55-58`
- **Описание:** builder упаковывает любую `.dll` из нечищеного `bin\Release\net48`; там уже лежит устаревший `LandingStats.QA.exe` — `.exe` не берётся, но DLL попала бы. Launcher и engine capture примут любую DLL, хотя план §7.2 требует точного списка.

### H-12. Target `CopyWorkerHost` берёт файлы по фиксированному пути
- **Серьёзность:** Низкая · **Категория:** Сборка · **Достоверность:** Подтверждено (риск)
- **Где:** `src/LandingStats.App/LandingStats.App.csproj:52-58`
- **Описание:** копируются только exe и config из `..\LandingStats.WorkerHost\bin\$(Configuration)\net48\`, без FileWrites и зависимостей. При `-o`, другом OutputPath или Platform либо частичной сборке в пакет попадёт устаревший бинарь или не попадёт ничего.

### H-13. Активация при повторном запуске срабатывает не всегда
- **Серьёзность:** Низкая · **Категория:** Баг (UX) · **Достоверность:** Вероятно
- **Где:** `src/LandingStats.ProcessControl/GuardianInstance.cs:20, 24-28, 46`; `src/LandingStats.App/App.Guardian.cs:52-62`
- **Описание:**
  - Без `AllowSetForegroundWindow` фоновый App обычно не может вывести окно вперёд — кнопка просто мигает в taskbar.
  - Во время паузы перед рестартом или handshake запрос теряется.
  - Если guardian запущен с повышенными правами (MSFS «от администратора» через exe.xml), обычный повторный запуск получает `UnauthorizedAccessException`. Исключение не ловится, и пользователь видит «could not start».

### H-14. Очистка кеша runtime
- **Серьёзность:** Низкая · **Категория:** Сопровождаемость · **Достоверность:** Подтверждено
- **Где:** `src/LandingStats.App.Launcher/Program.cs:157-158, 169, 336-386`
- **Описание:**
  - Каталоги `.staging-<guid>`, оставшиеся после краха во время распаковки, не удаляются никогда.
  - «Каталог занят» определяется по `IOException` от частичного `Directory.Delete(recursive)`. К этому моменту он уже удалил незаблокированные конфиги и ещё не загруженные DLL. Старые launcher'ы (0.9.4 и раньше) тем же способом портят каталог новой версии при параллельном запуске.
  - Комментарий в строках 157–158 описывает поведение, обратное фактическому.

### H-15. Неудача удаления backup откатывает успешную установку (не регрессия)
- **Серьёзность:** Низкая · **Категория:** Баг · **Достоверность:** Подтверждено
- **Где:** `src/LandingStats.App.Updater/Program.cs:310-329`
- **Описание:** `File.Delete(backupPath)` стоит внутри try. Если старый EXE ещё отображён в память (висящий вторичный launcher, антивирус), catch удаляет новый EXE и возвращает старый.

### H-16. `Application.ExecutablePath` строится из CodeBase (не регрессия)
- **Серьёзность:** Низкая · **Категория:** Баг · **Достоверность:** Подтверждено
- **Где:** `src/LandingStats.App.Launcher/Program.cs:74, 220`; `src/LandingStats.App.Updater/Program.cs:252-256`
- **Описание:** если в имени каталога есть `%XX`, путь декодируется неверно: `ReadPayload` не находит файл, а `--target` не совпадает с `MainModule.FileName`.

### H-17. В `Directory.Build.props` всё ещё 0.9.4
- **Серьёзность:** Низкая · **Категория:** Сборка · **Достоверность:** Подтверждено
- **Где:** `Directory.Build.props:7-9`
- **Описание:** dev-сборки используют тот же `Runtime\App\0.9.4`, что и установленная 0.9.4, и перераспаковывают его друг у друга. От выпуска релиза с этой версией защищает только сверка тега.

### H-18. Двойная компиляция ProcessControl/OwnedProcess и мёртвый код
- **Серьёзность:** Низкая · **Категория:** Сопровождаемость · **Достоверность:** Подтверждено
- **Где:** `src/LandingStats.App/LandingStats.App.csproj:21-22`; `src/LandingStats.App.Launcher/LandingStats.App.Launcher.csproj:15-18`; `src/LandingStats.App.Launcher/Program.cs:478-511`; `src/LandingStats.ProcessControl/GuardianGeneration.cs:88-97`
- **Описание:**
  - Одни и те же internal-типы собраны в две сборки: в App попадают лишние `GuardianSupervisor`/`GuardianUpdater`, в launcher — `GuardianClient`.
  - `QuoteArgument` в launcher не используется.
  - Клиент не отправляет `Stop`-пакет.
  - Если сборки когда-нибудь станут ссылаться друг на друга, появится ошибка CS0436.

### Проверено в этом направлении, проблем не найдено
- **Job Object:** suspended create с `PROC_THREAD_ATTRIBUTE_JOB_LIST` и `KILL_ON_JOB_CLOSE`. Потеря guardian убивает App и вложенные job; следующий App стартует только после `ActiveProcesses == 0`; двух runtime одновременно не бывает.
- **Stop-intent:** отдельные события с DACL пользователя имеют приоритет над exit и разрывом канала; Update-intent принимается только при `UpdateAccepted`.
- **Пайп и handshake guardian:** случайное имя, DACL пользователя с запретом Network, проверка PID обеих сторон, токен, границы generation, sequence и размера.
- **Backoff:** 250 мс → 30 с, сброс после 60 с здоровой работы; шторма рестартов и цепочки guardian'ов нет.
- **`GuardianUpdater.Verify`:** точный target, разрешённый каталог, reparse-проверки, размер и SHA-256, удержание файла до ready; отмена до commit гарантированно убивает helper.
- **Протокол обновления:** RSA-подпись встроенным ключом, HTTPS и allowlist хостов GitHub, потоковая проверка размера и хеша; downgrade отсекается.
- **Пакет и распаковка:** `BundlePayload` покрыт Authenticode. Распаковка — плоские уникальные имена, только два разрешённых `.exe`, staging и атомарный rename, полный SHA-256 перед каждым поколением.
- **Сборка:** `MsfsLandingStats.App.sln` включает все новые проекты; наборы параметров `Unsigned`/`Signed` взаимоисключающие.

---

## 15. Локальная история и архивы

Объединены с другими разделами:
- история нечитаема для 0.9.4 → E-02;
- полная очередь автоархивов → B-07;
- ошибки разбора вне `IsStorageException` → P-02;
- `File.Replace` без backup → B-09;
- `CaptureArchiveWriter.Dispose` подменяет ошибку → B-05;
- расхождение выбора EOCD в pre-check → F-01/F-07;
- бюджет 10 с по wall-clock → F-09.

### K-01. Удаление: tombstone фиксируется первым, а вызывающий код считает исключение признаком «не удалено»
- **Серьёзность:** Средняя · **Категория:** Баг / Потеря консистентности облака и локальной истории · **Достоверность:** подтверждено двумя направлениями (история, Google Drive)
- **Где:** `src/LandingStats.App/Storage/LandingRepository.cs:360-381`; `src/LandingStats.App/GoogleDrive/GoogleDriveBackupService.cs:81-104, 1306-1309`; `src/LandingStats.App/MainWindow.xaml.cs:521-560`
- **Описание:**
  - `File.Delete` (атрибут read-only, блокировка антивирусом) или `SaveIndex` могут бросить исключение уже после записи tombstone.
  - `DeleteLandingLocally` в catch откатывает pending delete в Drive, а UI возвращает запись в список с ошибкой. Но detail уже не открывается: `FindRecordPath` для tombstone возвращает null.
  - После рестарта запись исчезает. `IsDeletePending` через `IsDeleted` заставляет sync пропускать удалённую копию, поэтому в Drive она остаётся навсегда и видна на других ПК. Удалить её из приложения уже нельзя.
- **Направление:** после записи tombstone считать удаление совершённым; ошибки уборки повторять в фоне; pending cloud delete не откатывать.

### K-02. Кеш аэропортов не ограничен и целиком копируется в каждый эпизод
- **Серьёзность:** Средняя · **Категория:** Утечка ресурсов / Производительность · **Достоверность:** Подтверждено; порог — оценка
- **Где:** `src/LandingStats.App/Storage/AirportFacilityRepository.cs:51-118`; `src/LandingStats.App/Runtime/LandingRuntime.cs:66, 85, 93`; `RecordingEffectPersistence.cs:23`; `EpisodePersistenceData.cs:37, 44`; `EpisodeTransactionStore.cs:78, 286`
- **Описание:**
  - `MergeAndSave` только добавляет записи, без лимита.
  - `EpisodeInputMetadata.Capture` кладёт весь кеш (~125 байт JSON на аэропорт) в каждый `inputs.bin.gz`. Папки эпизодов не собираются и считаются в квоту (B-03). Кеш уходит и в analysis job.
  - Жёсткие пределы: 8 MiB на весь metadata JSON при записи и 65 536 аэропортов при чтении. За этими порогами `CommitInputs` и `ReadInputs` детерминированно падают на каждой новой посадке — вероятно, с остановкой recording consumer.
  - Этот же кеш — причина расхождения hash в B-02.
- **Направление:** хранить только аэропорты в радиусе эпизода или ссылку на версионированный снимок кеша; ограничить сам кеш.

### K-03. История и архивы хранят сырые IPC-конверты `RuntimeWire`
- **Серьёзность:** Средняя · **Категория:** Архитектура · **Достоверность:** сцепление подтверждено; последствие условное
- **Где:** `src/LandingStats.App/Models/ControllerCatalogTimeline.cs:22-60`; `src/LandingStats.App/Runtime/ControllerProvenanceCodec.cs:16-24`; `src/LandingStats.Runtime.Contracts/RuntimeWire.cs:12, 94`; `src/LandingStats.App/Storage/LandingRecordFile.cs:34-35, 201`; `server/telemetry-ingest/app/validation.py:246-261`
- **Описание:**
  - `controller_catalogs` в detail и `controller-catalogs.json` содержат Base64 полного wire-сообщения: заголовок, `ProtocolVersion`, GUID сессии, QPC.
  - При чтении истории сообщение проверяется с условием `ProtocolVersion == 1`. Сервер так же жёстко проверяет magic, version, kind и раскладку заголовка.
  - В итоге долговременный формат истории и серверный контракт привязаны к внутреннему протоколу parent ↔ worker.
- **Сценарий:** следующая версия поднимает `ProtocolVersion` → все прежние посадки с каталогами молча теряют provenance, а сервер отвечает 4xx (карантин).
- **Направление:** хранить каталог в собственном версионированном формате; при неизвестной версии деградировать до «нет provenance».

### K-04. Совместимость с будущими версиями держится на соглашении: перезапись теряет неизвестные поля
- **Серьёзность:** Средняя · **Категория:** Архитектура / Потеря данных · **Достоверность:** Подтверждено; последствие проявится в будущих версиях
- **Где:** `src/LandingStats.App/Storage/LandingRecordFile.cs:10-38`; `src/LandingStats.App/Models/LandingRecord.cs:10-23`; `LandingRepository.cs:122-157, 561-570`; `LandingSiteMetadataPersistence.cs:43`; `src/LandingStats.App/MainWindow.History.cs:243`; `GoogleDriveBackupService.cs:743-771, 1243`
- **Описание:**
  - Новые optional-блоки встроены в JSON detail, а не вынесены в sidecar. `LandingRecordFile` и `LandingRecord` не реализуют `IExtensibleDataObject`.
  - Запись пересериализуют:
    - метаданные отчёта;
    - миграция pitch-input;
    - индекс;
    - `ExportForBackup` — в Drive уходит пересериализованная запись, а не байты файла;
    - merge конфликтов Drive.
  - Члены, добавленные более новой версией, теряются и локально, и в облаке. Это противоречит требованию плана «unknown sidecars preserved».
- **Сценарий:** ПК A на версии N+1 пишет новое поле; ПК B на текущей версии разрешает метаданный конфликт той же посадки → в Drive загружается урезанная запись.
- **Направление:** `IExtensibleDataObject` или сохранение исходного JSON; бэкап байтов файла; новые блоки — отдельными sidecar-файлами.

### K-05. Неубираемые временные и служебные файлы
- **Серьёзность:** Низкая · **Категория:** Утечка ресурсов · **Достоверность:** Подтверждено
- **Где:** `src/LandingStats.App/Runtime/DurableFile.cs:15, 23`; `src/LandingStats.App/Storage/LandingRepository.cs:167-182, 257-304, 363-373, 432-436`; `BugReportRepository.cs:176-178, 206-207`; `src/LandingStats.App/Runtime/DurableOutbox.cs:224-228`; `LandingDeletionLedger.cs:15-21`; `RawCaptureRepository.cs:90`
- **Описание:**
  - После kill процесса остаются уникальные `.w<guid>.tmp` (раньше temp был детерминированным) и `.google-drive-read/import-*.tmp`.
  - В `Runtime/archives` никто не чистит `*_raw.zip.tmp` и `.interrupted-*.part`; файлы `.<id>.sha256` переживают доставку архива.
  - Прерванный `Delete` оставляет detail, который перечитывается при каждом старте.
  - Опубликованный, но не зарегистрированный автоархив при opt-out остаётся навсегда.
  - `.deleted` растёт.
  - `CleanupAbandonedTemporaryFiles` чистит только legacy-очередь.
- **Направление:** стартовая уборка по шаблонам с учётом возраста; удалять receipt вместе с архивом; удалять orphan-detail для tombstone.

### K-06. Мёртвый старый pipeline держится только на тестах
- **Серьёзность:** Низкая · **Категория:** Сопровождаемость · **Достоверность:** Подтверждено (grep вызовов)
- **Где:** `src/LandingStats.App/Storage/RawCaptureRepository.cs:34-58, 120-420` (`RawCaptureSession`/`StreamingChunk`); `BoundedTelemetryQueue.cs`; `BugReportRepository.cs:52-117, 135-136`; `AirportMetadataPersistence.cs`; `RunwayMetadataPersistence.cs` (и `MainWindow.RunwayPersistence.cs:16`, см. U-09); `ControllerCatalogArchive.cs:17-18, 27-39`; `ReplaySourceArchive.cs:16-27`; `TelemetryDecisionArchive.cs:15-16`; `LandingRecordFactory.cs:571-580`
- **Описание:** из production эти пути не вызываются, но покрыты регрессионными тестами. Тесты подтверждают поведение, которого в приложении больше нет: legacy RAW writer, «пропуск» автоархива. `ControllerCatalogArchive.Read` работает только с `ZipArchive` и не читает формат 2.
- **Направление:** удалить или пометить test-only; перевести тесты на production-пути.

### K-07. Квадратичная сверка и полная перезапись индекса на каждую запись
- **Серьёзность:** Низкая · **Категория:** Производительность · **Достоверность:** Подтверждено; оценки приблизительные
- **Где:** `src/LandingStats.App/Storage/LandingRepository.cs:421-449, 545-553, 561-570`; `src/LandingStats.App/Runtime/EpisodeTransactionStore.cs:227-229`; `LandingSiteMetadataPersistence.cs:15-44`
- **Описание:**
  - Сверка — O(N·M) вызовов `PathMatchesRecord`, по две аллокации строк на пару.
  - Каждый `Save` переписывает весь индекс: `CreateSummary` и `Validate()` снимка для каждой summary, gzip Optimal, fsync. На посадку с k контактами это около 2k полных перезаписей.
  - Всё выполняется под общим gate, которого ждёт загрузка detail в UI.
  - `FindRecordPath` сканирует каталог при каждом вызове.
- **Направление:** строить словарь id → путь один раз за загрузку; обновлять индекс пакетно на эпизод.

### K-08. `LandingRecord` одновременно служит DTO, summary индекса и view-model
- **Серьёзность:** Низкая · **Категория:** Архитектура · **Достоверность:** Подтверждено (сейчас списки полей согласованы)
- **Где:** `src/LandingStats.App/Models/LandingRecord.cs`; `LandingRepository.cs:572-654`; `LandingRecordFile.cs:97-183`; `GoogleDriveBackupService.cs:779-795`; `LandingRecordFactory.cs`
- **Описание:** около 70 DataMember и около 30 свойств локализованного отображения. Новое поле нужно синхронно добавить в `CreateSummary`, `SanitizedSummaryCopy`, `RestoreSummary` и merge-хеш. Если пропустить хотя бы одно место, в JSON появится NaN или поле потеряется в индексе.
- **Направление:** разделить persistence-DTO и presentation; проверять списки полей тестом через рефлексию.

### K-09. Нет лимита распакованного размера при чтении истории и бэкапа
- **Серьёзность:** Низкая · **Категория:** Безопасность · **Достоверность:** Гипотеза (нужен доступ к файлам приложения при scope `drive.file`)
- **Где:** `src/LandingStats.App/Storage/LandingRepository.cs:159-183, 246-264, 486-501, 525-543`
- **Описание:** gzip распаковывается без предела, а из Drive ограничен только сжатый размер. Небольшой файл, который распаковывается в гигабайты, вызовет при синхронизации `OutOfMemoryException`, который нигде не ловится.
- **Направление:** ограничить распакованный размер, как в `CaptureArchiveReader`.

### Проверено в этом направлении, проблем не найдено
- **Единственный писатель истории:** UI, `SaveHistory`/`CommitReport` и Drive используют один `_landingRepositoryGate`; порядок блокировок согласован.
- **Tombstone** соблюдается во всех путях записи истории; пути воскрешения удалённой посадки не найдено.
- **Атомарность:** detail и индекс пишутся атомарно (temp + `Flush(true)` + Replace/Move); обрыв между ними исправляют сверка и `SaveRecovered`.
- **Идентификаторы:** replay использует стабильные Id; `ArchiveId` фиксируется до публикации, а опубликованный архив переиспользуется только при совпадении hash.
- **C# и Python согласованы:** manifest LSAR0002, имена и лимиты членов, разбор Zstd-кадров, окно 128 MiB, отказ от ZIP64, шифрования и многотомности.
- **`CaptureArchiveReader`:** длина decoded сверяется на каждом чтении, SHA-256 проверяется в EOF.
- **Обратная совместимость:** новая версия читает индекс и detail 0.9.4 (v5/v6/v7, снимки v1).

