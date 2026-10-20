# Проверка поведения после рефакторинга против 0.9.4

Дата: 2026-10-18. **Шесть подтверждённых замечаний: пять регрессий поведения относительно 0.9.4 и одна невыполненная граница opt-out из плана.** При исходном аудите production-код не изменялся. После отдельного поручения пользователя все шесть исправлены локально без агентов; [изменения и новые проверки](docs/behavior-fixes-2026-10-18.md). Опубликованной версии исправлений нет. Ниже сохранены исходные воспроизведения и ограничения аудита.

## База и способ проверки

- База — опубликованный тег `v0.9.4`, peeled commit `08fe4e695547c694ef52c236ddd7b369b7f393b3`. Единственный коммит между ним и HEAD `3297bf38db7810375f7b97ba2d735119df450b30` меняет документацию; `git diff v0.9.4 HEAD -- src` пуст. Текущий объект проверки — весь рабочий исходник, включая untracked production-файлы, а не только последние изменения.
- Три независимых агента без истории разговора разделили acquisition/recording, UI/history/replay/update и delivery/storage/server/API/local tools. Четвёртый агент без истории оспаривал выводы по исходникам и плану. Основной агент повторил четыре изолированных воспроизведения, проверил охват, launcher/supervisor/build и выполнил сравнение полного корпуса.
- Предыдущие разрешённые исправления замечаний ревью тоже входят в согласованный объём. Само отличие от 0.9.4 не объявляется нарушением: ниже отдельно перечислены существенные намеренные изменения.
- Перед правкой документов повторно проверены SHA-256 всех 575 файлов из двух manifests: изменений нет. Исходный состав, diff и хеши находятся в `artifacts/behavior-audit-20261018/`. Артефакты probes используют реальные собранные классы, но отдельные искусственные данные и каталоги. Исторические PASS не использовались вместо новых проверок.

## Замечания исходного аудита

### B-01 · P1 · После переполнения IPC replay может стать обычной посадкой

**Сценарий:** получен SDK `REPLAY_START`; соединение с симулятором не закрывается, но теряется диапазон сообщений (`BufferOverflow`). Дальше replay передаёт физически согласованный заход и касание. RAW выключен, автоматическая отправка посадок разрешена.

В 0.9.4 replay исключался из захвата до `REPLAY_END`. Теперь `RecordingRuntimeState.SourceGap` вызывает полный `Disconnect`, затем сразу восстанавливает то же соединение, запрашивая только metadata. `SourceInterrupted` сбрасывает SDK replay/pause/loading. Новые native frames сами эти поля не содержат, а состояние SDK не запрашивается заново. Это не подтверждённое завершение replay.

**Воспроизведено:** без gap — 0 посадок; с gap — 1 посадка, `ReplayLike=false`, сохранённая история и автоматический ZIP из 201 кадра. RAW оставался false. Получение Full ниже 3000 ft не зависит от RAW, поэтому путь достижим в production. Основной агент повторил probe, включая восстановление checkpoint. Фактической отправки ZIP не было.

Тот же сброс затрагивает pause/loading. Искусственные движущиеся кадры проверяют потерю блокировки, но не доказывают, что стоящий на паузе живой MSFS сам выдаст такое касание. Частота overflow в реальных полётах не измерялась.

**Места:** `src/LandingStats.App/Runtime/RecordingRuntimeState.cs:313`, `Telemetry/LandingEpisodeCoordinator.cs:88`, `Telemetry/LandingEpisodeCoordinator.Lifecycle.cs:193`, `Telemetry/SimConnectSource.cs:589`; реальный producer — `src/LandingStats.WorkerHost/WorkerChannel.cs:105`.

**Нарушение:** §4.3/§6.2/P7/P11 — транспортный разрыв не разрешает тихо превращать replay/loading/pause в нормальную физику. Исправление должно консервативно восстанавливать lifecycle, включая переход, потерянный внутри gap; одного возврата старого флага недостаточно.

**Критерий:** replay/pause/loading после gap не создают обычную историю/автоархив; настоящий live-поток восстанавливается; checkpoint даёт тот же результат. Учёт: [LS-008](BACKLOG.md#ls-008--диагностика-несогласованных-первичных-каналов-перед-оценкой-посадки). Доказательства: [capture/report.md](artifacts/behavior-audit-20261018/capture/report.md), `capture/root-flow-recheck.txt`.

### B-02 · P2 · Исчезает simulator pitch при отсутствии физического источника

В 0.9.4 при `HasRawPitchInput=false` Controls показывал `PilotPitchPercent` с simulator-меткой. Новый joiner записывает даже пустой каталог; его non-null timeline выбирает physical-серию, где все значения отсутствуют. Simulator-ветка больше не используется, легенда при этом остаётся raw.

**Воспроизведено реальным WPF chart без запуска окна:** `HasRawPitchInput=false`, simulator command=20; без timeline chart=20, с пустой timeline chart=NaN. Основной агент повторил. Это отдельный случай от намеренных gaps у уже выбранного, но устаревшего/недоступного физического источника.

**Места:** `Runtime/ControllerJoiner.cs:198`, `Models/ControllerCatalogTimeline.cs:26`, `Controls/LandingChart.cs:185`, `Controls/ReplayPresentationData.cs:28`, `Controls/LandingChart.Drawing.cs:139` — под `src/LandingStats.App/`.

**Нарушение:** сохранение представления при P3/P6; LS-032 прямо обещает simulator series без выбранного raw source. **Критерий:** такие новые и старые записи показывают simulator command с верной меткой; пропуски выбранной physical-серии остаются пропусками. Учёт: LS-032. Доказательства: [ui/report.md](artifacts/behavior-audit-20261018/ui/report.md), `ui/root-pitch-recheck.json`.

### B-03 · P2 · Старый Unknown airport обновляется только после перезагрузки истории

В 0.9.4 получение новой airport facility list разрешало неизвестный аэропорт у всех уже загруженных посадок по сохранённым координатам, обновляло detail/summary и поиск. Сейчас runtime сохраняет airport cache; report обновляет только текущий эпизод. Обогащение всей истории осталось в startup/load-history пути, но исчезло из обработки свежих facilities.

**Воспроизведено:** format-6 запись с координатами LBSF после сохранения свежего airport cache остаётся `Unknown airport`; тот же record после штатного `HistoryCommands.LoadSummaries` получает LBSF. Основной агент повторил. Подписка baseline и отсутствие заменяющего события проверены отдельно по коду. Это не прежняя проблема поздней полосы текущего эпизода из LS-017 и не поиск cfg из LS-026.

**Места:** baseline `MainWindow.xaml.cs:1671,2074`; current `Runtime/LandingRuntime.cs:94`, `MainWindow.Runtime.cs:104,153`, `Runtime/HistoryCommands.cs:17`, `Runtime/EpisodeWorkCoordinator.cs:94`.

**Критерий:** новые facilities исправляют ранее неизвестный airport загруженной записи, detail и поиск без перезапуска; изменение долговечно, известные названия и runway geometry чужого эпизода не подменяются. Учёт: LS-039. Доказательства: `ui/report.md`, `ui/root-airport-recheck.json`.

### B-04 · P2 · Отложенное обновление забывается

Новый safe-restart gate правильно запрещает обновление при небезопасном состоянии. Но единственная проверка обновления запускается параллельно runtime startup. Если проверенный updater готов до `runtime.Ready` либо приложение запущено в полёте, `ReleaseUpdater` возвращает `Update.DeferredUnsafe`, удаляет временный updater и завершает работу. События ready/idle не повторяют попытку; ручного retry этой операции нет. Надпись обещает отсрочку до завершения записи, а реально нужен новый запуск приложения.

В 0.9.4 был один startup check без нового временного readiness-условия. Ошибка — потеря отложенной операции, а не сама защита записи.

**Места:** `MainWindow.xaml.cs:175,187`, `MainWindow.Startup.cs:67`, `Runtime/LandingRuntime.cs:142`, `Updates/ReleaseUpdater.cs:116,208` — под `src/LandingStats.App/`. Все call sites проверены основным и независимым агентами. Настоящее подписанное обновление не устанавливалось; подтверждение по полному control flow.

**Критерий:** успешно проверенное обновление при переходе к безопасному состоянию получает ровно одну новую попытку; отмена/выход/поколения и защита активной записи сохраняются. Учёт: LS-038. Доказательства: `ui/report.md`, [challenge.md](artifacts/behavior-audit-20261018/challenge.md).

### B-05 · P3 · Старое AXIS_ELEVATOR_SET сохраняется после границы, обнаруженной родителем

При обратном скачке sim time без отдельного SDK flow/reconnect старый recorder сбрасывал cached simulator-control event. Новый coordinator сбрасывает своё состояние, но не сообщает о границе source-процессу. Следующие frames продолжают содержать старые `AxisElevatorSetValid=true` и значение. Position discontinuity идёт тем же путём; динамически проверен time reversal.

**Воспроизведено:** после AXIS=50% и скачка 100.1→1 следующие 4/4 samples остаются valid; reset-команд нет. После factory/save/load detail старое значение сохраняется в 2/2 прореженных точках. Явный native `ResetControlContext` даёт valid=false/0. Основной агент повторил.

Это simulator event, не физические WinMM-оси. Неверна сохранённая диагностическая телеметрия; влияние на обычный pitch chart или метрики посадки не установлено. Reconnect и полученный SDK flow уже корректно сбрасывают context и не входят в замечание.

**Места:** baseline `Telemetry/SimConnectLandingRecorder.cs:1152`; current `Telemetry/LandingEpisodeCoordinator.cs:532`, `Telemetry/SimConnectSource.cs:118,644`, `Storage/LandingRecordFactory.cs:197`, `Storage/LandingRecordFile.cs:305`.

**Критерий:** последующие кадры нового физического сегмента не наследуют прежний simulator event; новая AXIS-команда вновь становится valid; поздняя reset-команда не стирает событие нового сегмента. Учёт: LS-040. Доказательства: `capture/report.md`, `capture/root-axis-recheck.txt`.

### B-06 · P2 · Opt-out завершается раньше подтверждённой остановки отправителя

План §6.5 требует считать отключение завершённым после worker ACK о запрете новых HTTP либо confirmed exit; до этого UI показывает отмену. `LandingRuntime.SetAutomaticDeliveryAsync(false)` отменяет parent CTS, ждёт сохранения политики и применения её recorder, но не ждёт текущий `AttemptAsync`, остановку child и drain callbacks. UI тоже сразу обновляет состояние, сохраняя task лишь для последующего shutdown.

Новые automatic claims и файловые эффекты позднего результата уже правильно блокируются. Однако parent CTS не является HTTP token дочернего процесса. До обработки Stop/exit уже принятое задание ещё может перейти между config/enrollment/upload. Это отсутствие границы завершения opt-out; фактический новый HTTP после отключения в этом прогоне не проверялся.

**Места:** `Runtime/LandingRuntime.cs:212`, `Runtime/UploadRuntimeOwner.cs:41,111`, `Runtime/ProcessJobExecutor.cs:47,59`, `MainWindow.TelemetryReports.cs:16`; child — `src/LandingStats.WorkerHost/Program.cs:97` и upload role. По исходникам независимо подтверждено двумя агентами и основным агентом. Дополнительный процессный probe не дошёл до проверки opt-out: тестовый child завершился по таймауту подключения named pipe до hello/HTTP. Это ограничение воспроизведения, не новый product finding и не успешная динамическая проверка B-06; лог сохранён в delivery-отчёте.

В 0.9.4 отмена происходила в процессе с HTTP token. Новый процесс требует дополнительного подтверждения. Поэтому это невыполненная часть P8, а не заявление, что все старые opt-out гарантии были идеальны.

**Критерий:** task отключения и UI завершают отмену только после запрещающего ACK/exit; зависший worker, незавершённый callback, повторное включение и параллельная смена policy проверены. Новые claims запрещаются сразу; manual upload остаётся отдельной политикой. Учёт: LS-012. Доказательства: [delivery/report.md](artifacts/behavior-audit-20261018/delivery/report.md), `challenge.md`.

## Сравнение полного корпуса

Одинаковый harness отдельно запущен на сохранённых бинарных файлах 0.9.4 и текущем source build. Каждый ZIP проверен по SHA-256 manifest. Результат обеих сторон: **1496 архивов, 2 237 624 кадра, 1519 подготовленных физических сегментов, 948 контактов**. У 8 архивов одинаковый прежний `Unsupported landing detail layout 0`; новых ошибок нет. Это сравнение доступного корпуса, а не утверждение о поддержке произвольного будущего формата.

- Совпали разбиение, replay classification, число/границы контактов, сохранённые результаты внутри архивов и проверяемая history projection.
- Все поля рассчитанных результатов совпали побитово, кроме **пяти полей M01 у 208 контактов в 186 архивах**: reconstructed closure, pitch contribution, residual, longitudinal arm и geometry quality. Это разрешённое исправление знака datum-калибровки, отдельно версионированное; maximum absolute closure difference — **12.384 fpm**. Не следует выдавать старую оценку «меньше 1 fpm на 16 fixtures» за оценку полного корпуса.
- Различается также input fingerprint: согласованный переход с CSV v1 на binary64 v2 из LS-002/P2. Это изменение формата контрольной суммы. Roundtrip каждого поддержанного snapshot внутри своей версии не меняет анализ. Старые сохранённые показатели не пересчитывались.
- Прогон не моделирует живые IPC gaps/события UI и потому не покрывает найденные выше дефекты сам по себе.

Доказательства: `core-baseline.jsonl.summary.json`, `core-current.jsonl.summary.json`, `core-comparison.json`, `core-field-comparison.json` в [каталоге проверки](artifacts/behavior-audit-20261018/). Время параллельных прогонов не является benchmark производительности приложения.

## Существенные намеренные изменения и отвергнутые кандидаты

| Изменение относительно 0.9.4 | Основание / проверенная граница |
|---|---|
| Компоненты в отдельных процессах, bounded queues, gap/provenance, journal/checkpoint, восстановление и безопасный update | Основной план. Не разрешает B-01/B-05/B-06. |
| Независимый controller poll 100 Hz, TTL 250 ms, versioned catalog, gaps недоступной выбранной physical-серии | P6 и разрешённые R16/LS-032 corrections. Не разрешает удалять simulator series при отсутствии выбранного источника. |
| Удалённые sibling revisions Drive получают deterministic winner; остальные trash | Явно разрешённое исправление G-03 из ревью 17 октября. Current winner выбирается по hash, не по времени «самый свежий». Это существенная смена политики конфликта, не новое неподтверждённое нарушение. Реальный Drive не запускался. |
| Datum model revision 2 и новый input fingerprint | Разрешённые M-01 и LS-002. Полный численный эффект измерен выше. |
| Внешний API и новые metadata, Zstd format 2 после проверки receiver capabilities | Прямой запрос пользователя, LS-022/P10; API по умолчанию выключен. Старые ZIP остаются допустимы. |
| Rolling journal содержит недавние кадры даже без RAW | Предусмотренное восстановление. Probe production journal за 120 s оставляет секунды105–120:16 записей/4 сегмента, checkpoint120, а не полный полёт. Это не непрерывный RAW-архив/отправка. |
| Cloud recovery, серверные лимиты/валидация, build/signing guards и исправления local tools | Разрешённые замечания предыдущих ревью. Server:25 случаев PASS, включая24 сравнения фактов legacy/v2 и точных members; основной агент повторил. Local tools: физические сегменты, sampling,33research channels и Analyzer проверены отдельно; детали в delivery-отчёте. |

Ранее найденная связь automatic consent→continuous RAW уже исправлена до этого среза и не включена повторно в шесть новых пунктов. B-01 воспроизведён при явном RAW=false. Удалённые `RunwayLoadingText`/`OnResetZoomClick` не признаны регрессиями: соответствующие старые producer/handler не были подключены. Replay останавливается при минимизации окна — это изменение фоновой работы явно отмечено в исправлении R-10 (`artifacts/review-remediation-20261017/findings.json`); кадровые данные не теряются.

## Охват и пределы

Проверены source/coordinator clocks/gates/metadata/facilities/controller joining; durable recording/recovery/history/outbox; upload/consent/Drive; server ingestion/dashboard и archive formats; integration API; UI/Charts/3D camera/time/settings/localization/startup/shutdown/update; launcher/supervisor/build и локальные Analyzer/Probe. Полные scoped отчёты: [capture](artifacts/behavior-audit-20261018/capture/report.md), [UI](artifacts/behavior-audit-20261018/ui/report.md), [delivery](artifacts/behavior-audit-20261018/delivery/report.md), [независимый challenge](artifacts/behavior-audit-20261018/challenge.md).

У ignored local tools нет исходника в теге 0.9.4: проверены текущие контракты, строгая baseline parity для них не заявлена. В этом аудите не было живого полёта, hotplug устройств, реального Drive sync/HTTP upload, подписанной установки, Windows logoff или нового измерения экранной плавности 3D. Статическая проверка камерных обработчиков не доказывает равенство FPS. Нулевых будущих сюрпризов такой аудит не гарантирует; воспроизведения и критерии выше нужны как обязательные регрессии перед закрытием соответствующих этапов.
