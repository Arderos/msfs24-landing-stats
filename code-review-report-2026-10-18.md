# Независимое ревью всего текущего кода — 18 октября 2026

Последующие исправления и повторная проверка ведутся в
[отдельном отчёте](code-review-fixes-2026-10-18.md). Ниже сохранены исходные
замечания и доказательства состояния до исправлений.

Проверена текущая рабочая копия, включая незакоммиченный рефакторинг и API,
а не только последние изменения. Базовый HEAD:
`3297bf38db7810375f7b97ba2d735119df450b30`. Это отчёт о текущем состоянии,
не сравнение с опубликованным релизом.

**Итог: 20 подтверждённых замечаний к приложению, серверу и документации:
3 P1, 16 P2, 1 P3.** Из них P3 — воспроизведённая условная проблема чтения
будущего формата, а не дефект файла, который выпускает нынешний writer.
Отдельно найдены **4 дефекта локальных исследовательских инструментов**,
исключённых из поставляемого приложения. Неподтверждённые риски в эти числа
не включены. Исправления в рамках этого ревью не выполнялись.

## Метод и охват

Семь агентов независимо проверяли runtime/архитектуру, данные/хранилище,
API/настройки/обновление, вычислительное ядро, нативные источники, UI,
сервер/облако. Каждый запущен с `fork_turns=none`: без переписки, памяти Codex,
исторических ревью и истории бэклога. Восьмой агент с таким же чистым контекстом
получил только исходники и результаты этого прогона для попытки опровергнуть
находки. Координатор перепроверял воспроизведения и сопоставлял их с бэклогом;
его дополнительная находка R15 не выдаётся за независимое открытие агента.

Охват: все проекты `src`, WPF и ресурсы, Core/Models, acquisition и worker-роли,
runtime/supervisor/guardian, IPC/API и пример клиента, persistence/archives,
Drive/upload, launcher/updater, сборка/упаковка/CI, Python receiver/dashboard,
его JavaScript и конфигурация контейнеров, а также присутствующие локальные
Analyzer/Probe. Инвентарь кода/сборки содержит 500 файлов. Это граница прохода,
не обещание исчерпывающего перебора всех состояний программы.

Артефакты: `artifacts/independent-api-review-20261018/`. Там находятся отчёты
каждого агента, исходники воспроизведений, искусственные данные, журналы и
контрольные SHA-256 исходников. Продукт, пользовательская история, настройки,
симулятор и внешние сервисы не изменялись. Новые сборки и тестовые данные
изолированы в этой папке. Реальные подпись, релиз и деплой не выполнялись.

Заключительная проверка SHA-256: **0 изменений** в обоих инвентарях
(492 source/test/docs файла и 500 code/build файлов; множества пересекаются).
Результат: `final-source-verification.json`. Записаны только материалы ревью
и дополнения в BACKLOG.md.

## Реестр подтверждённых замечаний

| ID | Приоритет | Следствие | Бэклог |
|---|---|---|---|
| R01 | P1 | Выключенная при запуске автоматическая телеметрия остаётся включённой в runtime | LS-023 |
| R02 | P1 | Чистый dashboard-образ не запускается | LS-027 |
| R03 | P1 | Допустимый архив превышает бюджет памяти ingress при разборе | LS-029 |
| R04 | P2 | Необязательный API может инициировать перезапуск приложения при ожидании хранилища | LS-022 |
| R05 | P2 | Drive перезаписывает локальное изменение, сделанное во время скачивания | LS-024 |
| R06 | P2 | Прерванный импорт оставляет новый файл со старым индексом, повтор не исправляет | LS-025 |
| R07 | P2 | Удалённая посадка возвращается в события и эпизоды API | LS-022 |
| R08 | P2 | Retention молча теряет ещё не прочитанное событие API | LS-022 |
| R09 | P2 | Последнее состояние паузы/загрузки отключает восстановление умершего соединения | LS-014 |
| R10 | P2 | Кэш геометрии переживает изменение связанных cfg и возвращает старые плечи | LS-026 |
| R11 | P2 | Закрытие регистрации блокирует отправку уже зарегистрированных клиентов | LS-028 |
| R12 | P2 | Ошибка удаления доставленного outbox-файла не повторяется до перезапуска | LS-020 |
| R13 | P2 | Сервер подтверждает получение архива, удалённого при неудачной очистке | LS-020 |
| R14 | P2 | Provenance нормальной длинной записи невозможно получить ни одной страницей | LS-022 |
| R15 | P2 | API остаётся disconnected после восстановления переполненного источника | LS-022 |
| R16 | P2 | График старой записи выдаёт команду симулятора за отсутствующий физический ввод | LS-032 |
| R17 | P2 | Неактивная промежуточная контактная точка скрывает последующие активные | LS-033 |
| R18 | P2 | Общий график нагрузок обрезает положительные горизонтальные пики | LS-034 |
| R19 | P3 | Принятые непрозрачные метаданные будущего формата ломают отображение записи | LS-035 |
| R20 | P2 | README неверно обещает создание отчётов только вручную | LS-031 |

### R01 — Потерянное отключение автоматической телеметрии

`src/LandingStats.App/MainWindow.Runtime.cs:48-58` захватывает настройку до
асинхронного создания runtime. Настройки уже доступны; пока `_runtime == null`,
оба вызова отключения — немедленный и после сохранения — меняют только UI-флаг.
Затем `StartAsync` получает старое `true`. Повторной сверки telemetry-policy нет.
Настройка сохранена как выключенная, но автоматические capture/upload включены.

Воспроизведение неизменённых orchestration-методов с управляемой задержкой
конструктора: `preference=False runtimeAutomatic=True policyCalls=0`.
Реальные сеть и запись настроек не использовались. Причина подтверждена
кодом и независимой перепроверкой. Исправление должно упорядочить startup и
ревизии настройки до включения доставки; одного дополнительного чтения флага
недостаточно для всех гонок. Проверить оба переключения при задержке создания
и `StartAsync`. Подробности: [UI-001](artifacts/independent-api-review-20261018/ui-review.md).

### R02 — Незапускающийся dashboard-образ

`server/telemetry-ingest/Dockerfile.dashboard:9` не копирует `capture_archive.py`
и `validation.py`, которые импортирует `dashboard_index.py:23-24`.
Точное воспроизведение COPY-раскладки завершается при `import app.dashboard`:
`ModuleNotFoundError: No module named 'app.capture_archive'`.
До HTTP health дело не доходит. Требуется включить полное множество зависимостей
и проверить запуск чистого образа. Docker и действующий сервер не запускались;
вывод относится к образу из текущего Dockerfile.
[SC-01 и журнал](artifacts/independent-api-review-20261018/server-cloud-review.md).

### R03 — Неограниченное число объектов в landing-results.json

`server/telemetry-ingest/app/validation.py:439-454` полностью материализует JSON
до ограничений числа объектов. Валидатор принимает 4 000 000 пустых словарей
при согласованном `landing_count`. STORED ZIP размером 12 007 713 байт укладывается
в upload/uncompressed/ratio limits и обычные квоты, обход подписи не требуется.

Замер actual validator в отдельном Windows Python 3.13: peak working set
344 506 368 байт, около 328,5 MiB; у ingress в Compose лимит 256 MiB.
Рост памяти воспроизведён; завершение Linux-контейнера по OOM — обоснованное
следствие, **не выполненный эксперимент**. Нужны ограничения размера и структуры
до materialization, плюс бюджет одновременно проверяемых архивов. Проверка
`len(records)` после `json.loads` уже запаздывает.
[SC-02 и memory probe](artifacts/independent-api-review-20261018/server-cloud-review.md).

### R04 — Ожидание repository gate эскалирует необязательный API

`src/LandingStats.Runtime/IntegrationApiRuntimeOwner.cs:126` сообщает pending
операцию без `operationId`. Поэтому supervisor не применяет её 30-секундный
бюджет. При удержании общего repository gate реальный owner/worker проходит
stall/probe/stop и эскалацию примерно за 10,17 с. Guardian связывает эскалацию
runtime с перезапуском приложения. API-клиенты для воспроизведения не нужны.
Этот gate используют сохранение истории и пакетный cloud import.

Контроль с единственным изменением в частной копии — заданным operationId —
пережил тот же 12-секундный hold. Живое приложение не перезапускали, длительность
реального импорта не измеряли. Нужны корректный учёт операции и ограниченное,
отменяемое ожидание для необязательного компонента. Проверка: задержка storage
меньше объявленного бюджета не перезапускает recorder.
[ARCH-1](artifacts/independent-api-review-20261018/architecture-review.md).

### R05 — Drive теряет локальные изменения во время await

`src/LandingStats.App/GoogleDrive/GoogleDriveBackupService.cs:696-708` использует
`localUnchanged`, вычисленный до скачивания. Перед replace под общим gate
проверяется удаление, но не текущий fingerprint. Искусственные два устройства:
remote меняет ВПП на `09L`; во время задержанного download локально сохраняется
`LBSF`; после sync аэропорт становится `Unknown airport`. Реальный service и
repository, fake Drive, никаких запросов в облако.

Нужна проверка актуальной ревизии непосредственно перед записью, затем merge,
конфликт или отложенный повтор. Проверить также repair/merge ветки.
[SC-03](artifacts/independent-api-review-20261018/server-cloud-review.md).

### R06 — Незавершённая транзакция detail/index не восстанавливается

`src/LandingStats.App/Storage/LandingRepository.cs:285-302`: новый detail
публикуется раньше индекса; identical-byte retry выходит до `UpdateSummary`.
Отказ замены занятого index даёт новый файл со старым аэропортом в индексе.
После открытия `LoadDetail` накладывает старые поля индекса; повтор импорта
возвращает NEW, но `summaryAirport=OLD detailAirport=OLD`.

Воспроизведение повторено координатором со свежесобранными из текущих проектов
Storage/зависимостями: `root-data-fresh-build-recheck.log`. Нужна восстанавливаемая
связь detail/index и ремонт при retry с сохранением законных поздних index-only
уточнений. Проверить сбой на каждой границе, reopen и идентичный повтор.
[D4](artifacts/independent-api-review-20261018/data-review.md).

### R07 — Удаление и публикация API не согласованы одним поколением

`src/LandingStats.Runtime/IntegrationApiData.cs:268-284` делает snapshot
неудалённых записей, отпускает repository gate, затем публикует. Между ними
Delete + hello успевают сбросить stream; старый snapshot попадает в новый stream.
В детерминированном допустимом расписании неизменённого Publish удалённый контакт
сохраняется в events/episodes и после reconciliation; `landings.get` возвращает
NotFound. **Каноническая локальная запись не восстанавливается**: повреждается
API-проекция и возможное зеркало внешнего клиента.

Нужны согласованные deletion epoch/snapshot/append без lock inversion и без
долгого удержания repository gate. Проверить delete на границе snapshot/append
и после клиентского resync. Несколько случайных запусков без гонки не опровергают
детерминированное воспроизведение. [D1](artifacts/independent-api-review-20261018/data-review.md).

### R08 — Retention создаёт незаметную дыру после действительного курсора

`src/LandingStats.Runtime/IntegrationEventStore.cs:104-110,135-139` удаляет по
timestamp все события ниже порога, а допустимость cursor проверяет только по
первой оставшейся sequence. При seq1=T, seq2=T+1, seq3=T-1 лимит 4096 удаляет
seq1 и seq3. Cursor2 принимается и получает seq4 без `ResyncRequired`.
Независимый контроль подтвердил и обычную позднюю revision2 с тем же временем
эпизода, что seq1: произвольная перестановка исторических дат не обязательна.

Нужны удаление непрерывного префикса либо сохранённая граница всех потерянных
sequence, требующая resync. Проверить поздние ревизии и restore старой истории
при уже подключённом клиенте. [D2](artifacts/independent-api-review-20261018/data-review.md),
[повторная проверка](artifacts/independent-api-review-20261018/validation-review.md).

### R09 — Потерянные ответы не восстанавливают соединение после suspension

`src/LandingStats.App/Telemetry/SimulatorStreamWatchdog.cs:96` выключает deadline
через `!Suspended`. Если последнее известное состояние pause/loading/stopped/
non-motion, а соединение больше не даёт вообще никаких ответов, этот флаг нечем
сбросить. Owner-loop при этом работает и отвечает supervisor.

Actual source с fake SDK: 121 безответный probe за 600 host-секунд, ноль
отключений; running-control отключается через 30 с; здоровая пауза с ответами
остаётся подключённой. Это доказанная дыра recovery-policy при введённом отказе,
**не доказательство зависания реального SDK или вреда обычной паузы**.
Разделить свежесть физики и ответы transport/system probe; живые pause/menu/
loading проверить отдельно. [N2](artifacts/independent-api-review-20261018/native-review.md).

### R10 — Неполный набор зависимостей кэша геометрии

`src/LandingStats.App/Telemetry/FlightModelGeometryResolver.cs:781,840` читает
attached_objects.cfg и attachment.cfg без `TrackCatalogPath`. При изменении
содержимого существующего файла directory timestamp не меняется. Новый resolver
из persistent cache возвращает плечо -10 ft, uncached по тем же файлам — -30 ft.
Проверены отдельно смена attachment root и inheritance. Правка tracked
flight_model.cfg корректно инвалидирует кэш — отрицательный контроль прошёл.

Требуется учитывать все зависимости/отсутствующие входы и инвалидировать старые
версии кэша. Это отдельный дефект invalidation; LS-001 не переоткрывается и
расчётная модель в этом ревью не пересматривается.
[N1](artifacts/independent-api-review-20261018/native-review.md).

### R11 — Closed registration ошибочно означает запрет всем клиентам

`src/LandingStats.App/TelemetryUpload/TelemetryUploadClient.cs:292-295`
возвращает RegistrationClosed до чтения уже существующей identity. Production
SendClaimAsync всегда вызывает этот Prepare. Сервер разрешает existing active
same-key refresh при closed. Actual client + fake HTTP дают enrolled=True,
Unavailable и 0 enrollment requests. Файлы сохраняются pending, но не доставляются.
Согласовать client/server semantics: закрыты новые регистрации, действующие
клиенты продолжают работу; проверить также удалённую сервером identity.
[SC-04](artifacts/independent-api-review-20261018/server-cloud-review.md).

### R12 — Терминальная очистка outbox выполняется только при старте

`src/LandingStats.App/Runtime/UploadRuntimeOwner.cs:70-71` пропускает Delivered/
Quarantined. Acknowledge сначала сохраняет Delivered, затем удаляет файл.
Sharing violation оставляет терминальную запись и архив; unlock/wake/новые
проходы не повторяют уборку. Явный RecoverTerminalEntries сразу очищает их.
Начальная startup-recovery есть, периодической terminal-recovery нет.

Проба подтверждает состояние, source объясняет отсутствие retry независимо от
пятисекундного первого backoff (короткий probe не выдаётся за полный backoff).
Нужен bounded cleanup retry без повторной сетевой отправки; отдельно проверить
quarantine/compact failure. [ARCH-2](artifacts/independent-api-review-20261018/architecture-review.md).

### R13 — Filesystem deletion откатывается только в SQLite

`server/telemetry-ingest/app/store.py:367-379` удаляет файлы внутри общей DB
транзакции. Отказ второго unlink откатывает строки, но первый файл уже исчез.
`capture_by_hash` и upload duplicate shortcut всё ещё дают successful receipt.
Три неистёкших архива, масштабированная quota и injected второй unlink:
`first_file_exists=false first_hash_still_acknowledged=true`.

Prune вызывается при startup и после каждого нового upload; повтор того же
digest выходит до cleanup и сам состояние не чинит. Нужна восстанавливаемая
двухфазная очистка и проверка действительного наличия принятого артефакта перед
acknowledgement. [SC-05](artifacts/independent-api-review-20261018/server-cloud-review.md).

### R14 — Provenance нельзя уменьшить параметром limit

`src/LandingStats.Runtime/IntegrationApiData.cs:248-257` всегда возвращает весь
SourceEvidenceTimeline. Валидная v7 запись на 3000 отсчётов: encoded source
516 012 символов превышает response cap 480 KiB. `landings.get` проходит;
`landings.provenance` и тот же запрос с limit1 всегда ResponseTooLarge.
Нужно разделить небольшие сведения происхождения и страничный timeline либо
дать явный field selection. Проверить длинную обычную запись и получение всех
страниц без потери mapping. [D3](artifacts/independent-api-review-20261018/data-review.md).

### R15 — Fresh telemetry помечается disconnected после overflow recovery

`src/LandingStats.Runtime/IntegrationTelemetrySnapshot.cs:40-41` фиксирует
false после RecorderState.Waiting. BufferOverflow-ветка
`RecordingRuntimeState.cs:311-321` затем восстанавливает тот же stream и metadata
без нового Connected lifecycle. Следующие samples меняют числа, но не флаг.
В probe fresh age около 7 мкс и ground speed123 сочетаются с disconnected/stale.
Он воспроизводит эффекты production-ветки; реальное переполнение worker не
инжектировалось. Нужен committed connection state или явное событие восстановления.
[Дополнительная проверка координатора](artifacts/independent-api-review-20261018/root-review.md).

### R16 — Незаметная подмена физического pitch

`src/LandingStats.App/Models/LandingRecord.cs:297-312` для legacy raw-источника
возвращает PilotPitchPercent, когда физический sample invalid/missing. График
по-прежнему подписан raw. Actual record roundtrip и actual chart accessor:
invalid raw даёт chart20, replay unavailable. Дополнительно accepted old sample
age1s/valid=true даёт chart80, хотя replay отвергает его как устаревший.
Последний случай не приписывается нынешнему native writer.
Нужны одинаковые freshness/validity правила и пропуск либо отдельная явно
названная simulator-series. [UI-002](artifacts/independent-api-review-20261018/ui-review.md).

### R17 — Фильтрация одного колеса скрывает следующие

`src/LandingStats.App/Controls/LandingGearSeriesBuilder.cs:72-90,140-148`
сначала убирает inactive CP, затем обрывает список на первой дыре в индексах.
Из существующих CP0/1/2 с активными CP0 и CP2 возвращается только CP0.
Наличие helper исключается по неверному признаку активности предыдущего колеса.
Нужна классификация по геометрии/типу либо сохранение нераспознанных активных
серий. Проверить асимметричный контакт и известные правила ToLiss отдельно.
[UI-003](artifacts/independent-api-review-20261018/ui-review.md).

### R18 — Общая шкала Loads учитывает horizontal только снизу

`src/LandingStats.App/Controls/LandingChart.Drawing.cs:198-211` берёт верхнюю
границу только из vertical G. Actual GetValueRange: vertical1.2/lateral2 даёт
max1.28; MapY обрезает horizontal пик. В isolated mode он отображается правильно.
Нужно считать оба экстремума всех видимых каналов. Проверить оба знака,
каждый канал, combined и isolated. [UI-004](artifacts/independent-api-review-20261018/ui-review.md).

### R19 — Оpaque future timeline интерпретируется как v1

`src/LandingStats.App/Controls/LandingChart.cs:184` без проверки версии обращается
к `Series[transition[0]]`. Storage намеренно сохраняет неизвестную версию,
replay корректно считает её unavailable. Accepted version2 с другим индексом
вызывает ArgumentOutOfRangeException; выбор записи очищает всё представление.
Это воспроизведённая несовместимость optional envelope, не существующий формат
текущего writer. Требуется проверка supported-version и сохранение обычных
метрик при opaque metadata. [UI-C01](artifacts/independent-api-review-20261018/ui-review.md).

### R20 — Неверное описание приватности

`README.md:32` говорит о создании новых diagnostic reports только после Report bug.
Фактически автоматическая отправка по умолчанию включена, что другие разделы
README и PRIVACY описывают правильно. Исправить именно противоречивое обещание.
Сопутствующая фраза `docs/app-storage.md:41` об отсутствии точных координат тоже
неверна: записи содержат lat/lon; **этот файл локальный/ignored, не доказано,
что он поставляется или опубликован**. Здесь не утверждается иной обход согласия
помимо отдельно подтверждённого R01.
[SURFACE-1](artifacts/independent-api-review-20261018/surface-review.md).

## Отдельно: локальные исследовательские инструменты

`.gitignore` явно исключает Analyzer, Probe, community solution/build и несколько
диагностических файлов Core. Поэтому эти дефекты не означают, что App не собирается.
Все четыре перенесены в LS-030 с отдельными критериями.

| ID | Место | Воспроизведение и необходимая проверка |
|---|---|---|
| L01 | Analyzer.csproj:8 / Core.csproj:7 | Diagnostic classes исключены из Core, но не включены Analyzer: CS0246 ContactPointResult. Восстановить compile ownership, собрать local solution. |
| L02 | ProbeForm.cs:312-376 | 33 CS0117 по удалённым TelemetrySample полям. Мигрировать schema без молчаливой потери исследовательских каналов, проверить capture. |
| L03 | TelemetryDownsampler.cs:30-37 | Два сегмента 50 Hz со сбросом clock: 200 строк/2 контакта превращаются в100/1 даже при50 Hz. Сбросить sampling grid на физических границах. |
| L04 | TelemetryDownsampler.cs:47-51 | Accepted CSV с infinity clock и обычными50 Hz навсегда зацикливает target. Проверить конечность и строгий ограниченный прогресс цикла. |

Для L03/L04 неизменённая реализация скомпилирована в частном harness с исправленной
только связью compile items, поскольку L01 иначе мешает её запуску.
[Core report](artifacts/independent-api-review-20261018/core-review.md),
[Native/local Probe report](artifacts/independent-api-review-20261018/native-review.md).

## Проверки и ограничения

- App: суммарно **293 уникальные regression groups PASS, 0 явных FAIL**.
  Первый общий процесс остановился после147 PASS с exit1 без FAIL/итоговой
  диагностики; причина не установлена. Прерванная группа отдельно прошла,
  остальные145 групп завершены отдельными процессами. Это **не** утверждение
  об успешном непрерывном полном прогоне. Журналы `full-app-tests.log`,
  `permanent-storage-retry.log`, `resumed-app-tests.log` сохранены без исправлений.
- API baseline5 групп, Runtime/Archives net48 и отдельные fault probes прошли.
  Retained legacy-engine test без настроенного пути и opt-in native/MSFS test
  не выполнены. Реальные посадка, UI camera smoothness, пауза/loading, обновление
  подписанного пакета этим проходом не проверялись.
- Частная сборка текущих product projects у server/cloud reviewer:0 ошибок,
  0 предупреждений. Координатор повторил data/UI probes с этими свежими DLL:
  `root-data-fresh-build-recheck.log`, `root-ui-fresh-build-recheck.log`.
- 28 существующих Drive/OAuth методов PASS; новый race воспроизведён отдельно.
  Fake Drive не обращается в аккаунт. В копии fixture сокращены только имена
  временных директорий для Windows path limit; первоначальные ошибки окружения
  не считаются дефектами продукта.
- Python:101 тест, **98 PASS, 3 SKIP** (не заданы внешние .NET fixtures).
  Полные FastAPI access/pacing endpoint suites не повторялись: доступные
  Python3.12/3.13 имеют разные наборы FastAPI/Zstd. Пакетный набор Docker
  не воспроизведён. Все эти пути просмотрены в исходниках.
- Actual API worker проверен на malformed/partial/slow clients, handshake,
  ACL shape, subscription cancellation и независимом Python-клиенте.
  Sandbox ACL failure опровергнут успешным тем же probe под обычным токеном.
  Отдельная Windows-учётная запись не использовалась.
- Повторный агент подтвердил ключевые race/retention/deadline findings и
  воспроизвёл retention/watchdog с положительными и отрицательными контролями.
  Его уточнения границ включены выше; неподтверждённые предположения не посчитаны.

## Отложенные наблюдения, не включённые в 24 дефекта

- Legacy ZIP reader принимает изменённый payload с неверным CRC; Python reader
  отвергает. Не доказаны принятие повреждённых данных сервером или искажение
  анализа: production пути имеют дополнительные проверки. Исследовать контракт
  целостности отдельно; LS-036.
- Impact preprocessing повторяется на каждый контакт, O(N*C). На искусственном
  чередовании OnGround8000 строк/4000 контактов выделено около2690 MiB суммарно,
  не одновременно. Реальный вред в допустимом capture не установлен; LS-036.
- В local Probe замечены simulation-time-only pre-roll и close из callback;
  из-за текущего build failure runtime не воспроизведён. LS-030, исследование
  после восстановления сборки, без объявления доказанной аварии.

Первым циклом исправлений целесообразно закрыть R01–R08 и R13: политика согласия,
работоспособность поставки, бюджет памяти, изоляция необязательного API и
согласованность данных. Остальные задачи имеют собственные воспроизведения и
не должны закрываться одним общим утверждением о завершении рефакторинга.
