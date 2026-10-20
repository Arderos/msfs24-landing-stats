# Исправления полного код-ревью — 18.10.2026

Основание: [независимое ревью всего кода](code-review-report-2026-10-18.md),
20 подтверждённых замечаний продукта/сервера/документации и 4 замечания
локальных инструментов. Пользователь поручил исправить все подтверждённые
замечания, разрешил параллельную работу и загрузку недостающих пакетов.

Правки выполнены в существующем dirty checkout с HEAD
`3297bf38db7810375f7b97ba2d735119df450b30`. Изменения до этого поручения
сохранены; `artifacts/fix-review-20261018/before-tracked.patch` фиксирует
начальное состояние tracked diff. Оригинальный отчёт и воспроизведения
сохранены как история. Коммитов, подписи, публикации и деплоя нет.

## Реестр исправлений

| ID | Бэклог | Исправление и проверка |
|---|---|---|
| R01 | LS-023 | Publication/startup policy согласованы с изменением настройки. Источники и automatic upload включаются только после применения последней ревизии. Actual MainWindow/LandingRuntime: opt-in/opt-out при задержках конструктора и journal restore, без блокирования dispatcher. |
| R02 | LS-027 | Dashboard Dockerfile включает import closure. Точная COPY-раскладка импортирует dashboard/snapshot и обслуживает ASGI `/health`. |
| R03 | LS-029 | Result JSON сканируется потоково; auxiliary JSON ограничивается до materialization; один validator на процесс. Исходные 4 млн объектов отклонены без роста peak. Current C# writer с 65 536 строками принят. |
| R04 | LS-022 | Reconcile объявляет operationId; gate waits ограничены и отменяемы, busy response сохраняет request ID. Actual API owner/worker выдерживает 12 с блокировки истории без escalation. |
| R05 | LS-024 | Перед cloud commit под gate повторно проверяется локальная ревизия/fingerprint. Concurrent edit сохраняется при replace, repair, merge и последующем sync. |
| R06 | LS-025 | Durable import intent связывает detail bytes и поколение индекса. Поколение атомарно сохраняется в стандартном gzip FEXTRA, JSON-массив не меняется. Reopen/identical retry восстанавливают прерванный import, поздние index-only refinements и точные возвраты прежних значений сохраняются. Проверены detail/index/cleanup faults и оба порядка правок в batch. |
| R07 | LS-022 | Projection gate упорядочивает deletion epoch, append и query; repository gate не удерживается при event-store I/O. Исходная гонка и последующий reconcile не возвращают удалённый контакт. |
| R08 | LS-022 | Retention удаляет непрерывный sequence prefix; старый журнал с дырой требует resync. Late revision/import с более ранним временем больше не скрывает непрочитанное событие. Сохранившийся старый эпизод получает позднюю revision 2, даже если его время ниже retention floor. |
| R09 | LS-014 | Независимые system-ответы имеют finite deadline и при suspended physics: 120 с; running deadline 30 с сохранён. Все suspended fake-SDK controls проверены с ответами и без. |
| R10 | LS-026 | Geometry cache учитывает attachment/root/inheritance и значимые отсутствующие файлы; v2 перестраивается в v3. Warm/new resolver после cfg edit совпадает с uncached результатом. |
| R11 | LS-028 | Closed enrollment разрешает refresh существующей identity; новая отклоняется, server-pruned identity теряет enrolled marker без удаления pending bytes. Client/server проверки согласованы. |
| R12 | LS-020 | Delivered/QuarantinePending/Quarantined cleanup повторяется с backoff без повторного upload. Actual file locks, startup failure и compaction проверены. |
| R13 | LS-020 | Durable deletion journal отделяет DB commit от unlink; duplicate receipt требует существующий артефакт. Fault injection и reopen не подтверждают отсутствующий файл. |
| R14 | LS-022 | Provenance получает section/offset/limit, totals и advertised limits. Timeline, catalogs и transitions длинной записи восстанавливаются из страниц без превышения 480 KiB. |
| R15 | LS-022 | API использует committed connection state reducer. Same-stream BufferOverflow recovery показывает fresh connected telemetry; quit/disconnect остаются disconnected. |
| R16 | LS-032 | Raw pitch chart использует общие validity/freshness/finite/range правила; недоступность даёт gap. Legacy roundtrip не подменяет physical input командой симулятора. |
| R17 | LS-033 | Активные контакты неизвестного самолёта не теряются после промежуточной inactive точки. Явная ToLiss A340 grouping/helper policy сохранена. |
| R18 | LS-034 | Обе границы Loads scale включают все видимые vertical/horizontal extrema. Проверены оба знака и combined/isolated режимы. |
| R19 | LS-035 | Future controller timeline остаётся opaque; обычные метрики/графики рендерятся. DTO roundtrip и actual chart render проходят. |
| R20 | LS-031 | README/storage docs согласованы с default-on automatic reporting, выключателем, manual reports и сохранением точных координат ограниченного окна. |
| L01 | LS-030 | Analyzer явно компилирует research-классы, исключённые shipping Core. Локальное community solution собирает инструменты; App имеет отдельное полное решение. |
| L02 | LS-030 | Все 33 дополнительных Probe SDK-канала сохранены в versioned sidecar с sequence/host keys. Capture ZIP roundtrip проверяет значения, unavailable и соответствие строк. |
| L03 | LS-030 | Downsampler начинает отдельную grid на каждом физическом сегменте и сохраняет inactive barriers. Исходные 200 samples/2 contacts больше не превращаются в 100/1. |
| L04 | LS-030 | Finite rate/phase/clock validation и арифметическое продвижение O(rows) исключают бесконечный цикл. Проверены NaN/infinity, крайние интервалы и period. |

## Проверки

Все 24 подтверждённых замечания исправлены локально. Общий прогон на точных
бинарниках пакета и заключительные проверки завершены без ошибок.

| Проверка | Результат и доказательство |
|---|---|
| Полное App solution + unsigned package | 0 warnings / 0 errors, `package-build-final.log` |
| App regressions на бинарниках пакета | 302 PASS, 0 FAIL; `app-full-package.log`. Retained legacy engine без настроенного пути пропущен; live native-source smoke — отдельная opt-in команда |
| Runtime contracts и Archives | PASS на net48 и net10.0, `runtime-net48.log`, `runtime-net10.log`, `archives-net48.log`, `archives-net10.log` |
| Server, pinned Python dependencies | 148/148, без skips; повтор на архивах точного пакета — `server-python-package-fixtures.log` |
| Drive/enrollment | 30/30 методов, `server-cloud/dotnet-tests-final.log`; новые проверки также включены в общий App-прогон |
| Local Analyzer/Probe | Сборки 0 warnings / 0 errors; local-tools и original clock-reset CSV end-to-end PASS, `community-build-final.log`, `local-tests.log`, `analyzer-reset.log` |
| Независимые перепроверки | Все исходные R06/R08 probes после исправлений PASS; native/local fresh builds и проверки PASS. Scanner: 2500 валидных документов, 10000 мутаций, 21 chunk-boundary control без расхождений |
| Упаковка/обновление | BundlePayload, GuardianBundle, UpdateCompatibility PASS, `bundle-final-tests.log`, `guardian-bundle-final-tests.log`, `update-final-tests.log` |
| Соответствие пакета тестам | Все 31 вложенный EXE/DLL побайтово совпадают с обычным Release test root; `package-tested-binary-comparison.json` |

Все перечисленные логи находятся в `artifacts/fix-review-20261018/`.
Заключительная сверка 527 source/test/docs/build файлов: изменений после
проверяемого снимка нет (`final-source-manifest.json`, `final-verification.json`).
`git diff --check` прошёл.
Первый общий App-прогон дал 300 PASS и два отказа из-за слишком длинного
workspace TEMP. Отдельная net48-проба установила границу: destination длиной
259 символов публикуется, 260 даёт PathTooLongException. Тот же lifecycle и
geometry binary проходит на обычном TEMP; последующий полный прогон дал
302 PASS. После усиления атомарности поколения индекса полный прогон
повторён на точных бинарниках пакета: снова 302 PASS, 0 FAIL
(`app-full-package.log`). Таймауты продукта
и проверок ради прохождения тестов не увеличивались.

Частные отчёты с точными командами и сценариями:

- [Runtime, storage, API](artifacts/fix-review-20261018/runtime-result.md).
- [UI и startup policy](artifacts/fix-review-20261018/ui-result.md).
- [Server и cloud](artifacts/fix-review-20261018/server-cloud-result.md).
- [Независимая проверка native/local fixes](artifacts/fix-review-20261018/verify-native-local.md).
- [Независимая проверка data/server fixes](artifacts/fix-review-20261018/verify-data-server.md).

Независимая перепроверка обнаружила дополнительные последовательности R06:
оставшийся intent следующего импорта, точный возврат прежнего summary после
успешного commit, тот же возврат внутри batch и metadata-before-import перед
неудачным index commit. Все они закрыты поколением, публикуемым атомарно с
индексом, и повторно проверены исходными probes. Прежняя DLL Storage и Python
gzip/JSON читают новый индекс. Для R08 отдельно закрыта поздняя revision 2
сохранившегося старого эпизода. Исходные failing и последующие passing логи
сохранены в `verify-data-server/`; неуспешные итерации не удалялись.

Для зависимостей Python создана отдельная папка `server-cloud/python-deps`
внутри артефактов. Глобальные пакеты не изменялись. Некоторые проверки
named pipes/worker jobs и atomic file operations запускались обычным
пользовательским токеном после ограничений sandbox; использовались только
частные fixture roots. Полный App-прогон использует стандартный короткий
пользовательский TEMP с уникальными тестовыми папками. Первые неуспешные
логи сохранены и не считаются PASS.

## Итоговая сборка

[Неподписанный EXE 0.9.5](artifacts/fix-review-20261018/unsigned-final/MSFS-Landing-Stats.exe),
2 081 978 bytes. SHA-256:
`49678b2bb27b21e2e3cb4c433fe6d02acd40bb969bec42e9511f9981ef45d022`.
Пакет собран, проверен и не установлен вместо пользовательской копии.

## Границы результата

- Старые записи проходят существующие readers; формат продукта и schema v7
  не менялись. Дополнительный research sidecar локального Probe не является
  новым форматом diagnostic upload приложения.
- 120 с для suspended transport — выбранная recovery-policy. Живой MSFS,
  native Probe capture и ручная оценка камеры в этом проходе не проверялись.
  Короткая проверка настоящего controller worker с тестовым joystick backend
  прошла; физические устройства и hotplug в реальном рейсе не проверялись.
  Rendering/replay код камеры не менялся.
- Server memory измерена в Windows Python с импортом полного `main`:
  max-sample fixture — 83 283 968 bytes (~79,4 MiB), original attack —
  47 198 208 bytes baseline без прироста. Это не Linux cgroup/OOM тест.
  Проверка Docker ограничена COPY-layout/import/ASGI; daemon не запускался.
- File-lock/DB fault injection и reopen не эквивалентны физическому power loss.
  API ограничивает ожидание входа в gate; начатый синхронный filesystem I/O
  остаётся под существующим operation supervision.
- Провенанс читается страницами; конкурентная замена записи между страницами
  требует клиентской проверки/retry, snapshot-export token не добавлен.
- LS-036 и статические наблюдения Probe pre-roll/callback-close остаются
  отложенными: это не подтверждённые замечания из списка 24. LS-001 остаётся
  best effort закрытым. Старые отдельные исследования бэклога не объявляются
  завершёнными этим проходом.
- Реальные пользовательские история/настройки, Drive, telemetry receiver и
  симулятор не изменялись. Публикации исправленной версии нет.

## Последующее сообщение пользователя: Drive waiting for first sync

После передачи первого пакета подтверждён LS-037: в нём отсутствовала native
OAuth-конфигурация, а cloud owner не передавал в окно ход и ошибки фоновой
синхронизации. Исправлены оба пути, добавлена обязательная проверка конфигурации
Delivery DLL при обычной упаковке. Подробности и история — в BACKLOG.md, LS-037.

Исправленный пакет: `artifacts/drive-sync-20261018/unsigned/MSFS-Landing-Stats.exe`.
SHA-256: `30a4ef0decee010ff8f989244ceb6159e2d6df07c2d26a4c12652beac7933064`.
Проверены 43 различные группы профильных регрессий, 5 групп build guard в двух
редакциях PowerShell, bundle/guardian/update. Все 31 EXE/DLL в пакете совпали
с протестированными бинарниками; metadata OAuth сверена с локальным подписанным
v0.9.4. Доказательства: `artifacts/drive-sync-20261018/package-verification.json`
и соседние логи. Живой Google Drive этим прогоном не вызывался.

## Последующие сообщения: готовность посадки, полоса и dashboard

Локально исправлены LS-017 и дополнительный случай LS-026: прежний success
сбрасывается на смене эпизода, ожидание анализа отражается отдельно, финальные
airport/runway перечитываются без перезапуска и без сброса replay view.
Недоступная Community junction больше не отключает весь persistent geometry
cache; все зависимости проверяются с ограниченным параллелизмом. На настоящей
установке повторная загрузка каталога сократилась с 34.988 до 6.166 s при полном
совпадении fingerprint геометрии. Это время resolver, не всей посадки.
После проверки реальный пользовательский кэш заранее создан штатным resolver
через DurableFile и общую quota: 39.781 s на первый проход, 4.979 s на повторный;
fingerprint исходного каталога совпал, повторный проход не перезаписал файл.
Для точной корреляции следующих живых случаев добавлен ограниченный фоновый
`Runtime/episode-timeline.log`. Прежняя надпись не была записана в лог, поэтому
её точный момент относительно пользовательского эпизода ретроспективно не доказан.

15 профильных App-групп PASS, включая реальный WPF ListBox с заблокированным
detail I/O, позднюю полосу, сохранение камеры/времени и настоящую junction.
Старая DLL воспроизводит отключение кэша; исправленная проходит тест.
Release build: 0 warnings/errors. GuardianBundle PASS. Все 31 EXE/DLL нового
пакета совпадают с проверенными Release-бинарниками.
Текущий unsigned EXE 0.9.5: `artifacts/landing-status-20261018/unsigned/MSFS-Landing-Stats.exe`,
2 085 435 bytes, SHA-256 `b9ae0fd7d85c5f2f5eb03dd4770cfe34fb9fcb542a8bc45a5d02d71a84463781`.
Не подписан, не опубликован, работающий пользовательский EXE не заменён.

После отдельного разрешения на передачу архивов проверены два исходных ZIP
с ошибкой dashboard: оба корректны, текущий parser/ingress их принимает.
Прежний parser отклоняет журналы из 2165/2226 записей по лимиту 2048.
Перед развёртыванием этот же предел подтверждён непосредственно в старом
контейнере; версия его индекса была 3. После отдельного разрешения обновлён
только dashboard на `20261018-e3dd5652ddd5` с индексом 4 и лимитом 8192.
Linux Docker build, import обоих entrypoints и health прошли. Другие контейнеры
не перезапускались, оба ZIP сохранили SHA-256. 150 Python tests PASS.
Полная переиндексация: 831/831, pending=0, errors=0; оба проблемных архива ready.
Свежая финальная проверка с новыми поступлениями: 833/833, stale=false,
pending=0, errors=0, health=ok, список unreadable пуст.
Свидетельства: `artifacts/landing-status-20261018/server/`;
LS-004/LS-027 в BACKLOG.md. Прежние разделы отчёта сохраняют границы исходного
прохода; этот раздел описывает дополнительно разрешённые живые операции.

## Разрешённое обновление принимающего API и доставка очереди

18.10.2026 пользователь отдельно разрешил обновить API и подтвердить отправку
всей очереди. Развёрнут образ `msfs-landing-telemetry-api:20261018-c3690513103f`;
другие сервисы не перезапускались, прежний образ/Compose и SQLite backup сохранены.
Публичный config объявляет controller/source metadata и archive v2. Реальные
длинные raw captures требовали 128 MiB expanded вместо 64 MiB; новый Compose
предел проверен на неизменённых файлах в Linux с 256 MiB cgroup без swap.
Compressed/sample/member/JSON bounds сохранены. Производственный пик памяти
после отправок 138 579 968 bytes, OOM и перезапусков нет.

Все 6 исходных и 1 новый архив автоматически отправлены клиентом: Delivered
receipts, пустая очередь, серверные ZIP совпадают по SHA-256, dashboard читает
все 7. Свежий индекс 845/845 без ошибок. Посадка LBSF / RWY 27 за 17:31 Sofia
подтверждена по record ID `6c221105210811838712e69840f66b8a`; серверный capture ID
`7834091a421b36698eee1cc20a028dc4`, загрузка 18:32 Sofia. Подробности LS-008,
артефакты `artifacts/api-deploy-20261018/`. Публикации новой клиентской версии нет.
