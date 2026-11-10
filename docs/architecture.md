# Runtime architecture

The WPF application presents saved landings and runtime status. It delegates
simulator access, controller polling, analysis and network work to supervised
workers. The entry points are in `src/LandingStats.Workers.*`; their runtime
implementations are under `src/LandingStats.App/Runtime`.

## Ownership

| Component | Responsibility |
| --- | --- |
| `LandingStats.App` | WPF views, user commands, charts and 3D playback |
| `LandingRuntime` / `RuntimeSession` | Component lifetimes, recording coordination and presentation snapshots |
| Simulator worker | SimConnect dispatch, aircraft/scenery metadata and source observations |
| Controllers worker | DirectInput enumeration, device catalogs and physical-axis samples |
| Analysis worker | Landing analysis outside the UI process |
| Upload worker | Diagnostic delivery; the parent owns durable queue state |
| Cloud worker | Google Drive network operations |
| Integration API worker | Optional read-only API for the same Windows user |
| `LandingStats.Core` | Numerical analysis and telemetry codecs |
| `LandingStats.Runtime.Contracts` | Versioned messages, worker identity and IPC bounds |

`ProcessWorkerHost`, `ComponentSupervisor` and `WorkerOwner` manage worker
processes and recovery. Session/instance IDs and generations keep obsolete
worker replies out of the current session. `RecordingRuntimeOwner` coordinates
recording state; `EpisodeWorkCoordinator` and `EpisodeTransactionStore` handle
completed episodes and persistence. Recovery journals and the durable outbox
preserve pending work across restarts.

An isolated worker remains owned until Windows confirms that its process tree
has exited. The supervisor keeps reporting progress during termination and
retries within that boundary; a delayed native exit does not restart the App
or admit a second worker. Managed owners retain their child-process claims
during cleanup. An unresponsive managed owner without such a pending child
still escalates to Guardian because it shares application state.

Header health groups use these supervision snapshots. First readiness is green
immediately; recovery requires ten seconds of continuing owner progress,
including an idle owner. The thirty-second warning budget spans restarts.
Acknowledgement affects only the warning and resets after stable recovery;
it never changes recording or recovery policy.

Changes should stay with the component that owns the state. File reads,
SimConnect callbacks, controller polling and network requests must not be moved
into WPF rendering or input handlers.

## Controller values and assignments

The Controllers worker selects game-controller collections by Raw Input HID
Usage Page / Usage before reading DirectInput device identities. Joystick
configuration supplies candidate instance GUIDs, supplemented by the Windows
DirectInput instance cache behind `DirectInputInstanceCache`. Each candidate
must match a live collection through `DIPROP_GUIDANDPATH`; persisted data alone
never establishes identity. Global DirectInput enumeration is avoided because
it can query unrelated HID product strings and block inside a device driver.

The simulator worker owns `MsfsControlProfileMonitor`. It reads saved MSFS
profile selections and XML files on a background task, away from SimConnect
dispatch and the UI. A generation check rejects stale results after an aircraft
change; file changes request a fresh snapshot.

Profile discovery uses `SIMCONNECT_RECV_OPEN.dwApplicationVersionMajor`: 11
selects MSFS 2020 and 12 selects MSFS 2024. The display name is never used for
version selection. Unknown versions and ambiguous Steam accounts leave profiles
unavailable. This follows the [SimConnect version contract](https://devsupport.flightsimulator.com/t/documentation-for-simconnect-recv-exception-is-incorrect/13555/4).

Physical axes use the numeric `KEY` in the
[MSFS input configuration](https://docs.flightsimulator.com/msfs2024/retail/content-configuration/input/input-configuration-xml-properties/).
Full-axis IDs are `1026/1042/1058` for X/Y/Z, `770/786/802` for Rx/Ry/Rz,
and `514/530` for slider X/Y, as emitted by the Input Profile Editor and the
Asobo `asobo-input-profiles-pc` category profiles. These simulator IDs map to
our eight semantic DirectInput axes; they are not DirectInput object IDs.
Localized `Information` labels are display metadata. Unknown IDs, split axes
and conditional assignments remain unavailable.

Aircraft selection uses the normalized SimObject configuration path. Device
instance GUIDs identify controllers. Selected General, Category and Aircraft
layers are resolved in that order, with a more specific binding overriding the
same control from a less specific layer. Unreadable, conflicting or unsupported
selections are recorded as diagnostics.

`ControllerJoiner` in the parent runtime joins those assignments to DirectInput
device catalogs from the Controllers worker.
A recording keeps both facts: the measured value of an axis and the control it
was assigned to at that time. Catalog revisions preserve changes during an
episode. Diagnostic provenance includes GUIDs, profile selections, available
profile evidence, resolved bindings and read errors.

Playback uses this saved information. Opening a saved landing requires neither
MSFS nor connected controllers. It does not reinterpret the landing using today's
profiles. Correlation remains a fallback for roles without an explicit saved
assignment; see [methodology](methodology.md#10-3d-replay-controls-power-attitude-and-gear).

## Storage and delivery

Compact landing files under `%LOCALAPPDATA%\MSFS Landing Stats\Landings` contain
the series needed for history, charts and replay. The current history layout is
7; its version is independent of diagnostic CSV schema 8 and archive format 2.
See the [history format](format-v7.md) and [capture format](capture-archive-format.md).

[Landing sharing](landing-sharing.md) packages the saved contacts of one episode
in a `.lslanding` file. The storage layer validates and imports the package;
the UI serializes those operations with other history writes. Imported contacts
retain their origin through summaries and backup and are excluded from monthly
averages. Viewing them uses saved replay data and does not require live devices.
The outer portable launcher maintains the current-user file association and
queues file-open requests before acquiring its single-instance lock. The App
acknowledges a durable request after handling its import, including when the
request arrives during startup or recovery.

The history index retains a fingerprint of each known detail version alongside
index-only airport/runway corrections. Import intents precede detail publication
and are fenced by the index generation. Recovery of a damaged intent preserves
those corrections when detail bytes still match the committed fingerprint;
changed details are reconciled from their own contents. Saving computes the
fingerprint while writing the complete compressed detail, before publication;
recovery reads the record and its fingerprint through one shared file handle.
Older indexes acquire
the fingerprint atomically before their first replacement import. This internal
index metadata is omitted from portable landing details and backups.

Diagnostic reporting and Google Drive backup are separate operations. Automatic
diagnostics are enabled by default and can be disabled in Settings. Drive backup
is optional and contains compact history, not raw diagnostic archives. The local
API is separately opt-in. See [privacy](../PRIVACY.md).

Deferred diagnostic delivery has its own presentation state and never replaces
an analysis failure. Settings show its status even with automatic reporting
enabled. Legacy archive intake has one retry owner per durable request; repeated
failures are deduplicated while original captures remain available.

Settings provide explicit upload-key recovery for profiles whose Windows-protected
key is unavailable. After explicit confirmation and a fresh key check, replacement
suspends delivery, cancels the active sender and waits for its entire process tree
and accepted callbacks.
It atomically replaces the identity while retaining the exact original file,
clears enrollment and stale key-failure status, and resumes eligible queued work.
Previous consent for unclassified legacy archives, automatic-reporting preferences
and manual requests are preserved. A usable key is retained unchanged, including
its server enrollment. A post-publication status failure is reported as pending
cleanup without denying the committed recovery. Waiting for the old sender can
be cancelled before publication; its owner still confirms process exit and callback
drain before another sender is admitted. Cross-process
identity writes are serialized; senders bound to an older signing identity cannot
write enrollment for a replacement. Reading consent metadata does not decrypt the
key, and a DPAPI failure alone never triggers automatic replacement.

Runtime failures are identified by component and work item. Owners clear their
own notice only after the failed operation succeeds or its work is retired;
unrelated health updates cannot clear an active failure. Terminal server rejection
is presented separately from queued retryable reports.

Missing or invalid episode and session metadata is quarantined in place with a
durable `metadata-failure.json` receipt. The receipt preserves the reason and
required engine references without keeping a running service in a failed state.
Episode recovery and API reconciliation skip isolated metadata; changing it
permits recovery again. Inspection and quarantine share episode transaction or
session ownership with publication. Failure to persist isolation remains an active
storage error until isolation succeeds. Original source bytes remain untouched.
Receipts bind to bounded metadata content, so restoring bytes with the same size
and timestamp also permits recovery. Older receipts are reinspected and upgraded
without expanding their original engine references. Access failures remain
distinct from missing or unchanged metadata.

Delivery metadata corruption is also isolated in place, with a receipt under
`outbox/quarantine`. Retryable access failures and quarantined metadata have
separate presentation states. Recovery retains the exact failed operation and
worker identity, so repairing a claimed entry cannot bypass sender retirement.
Confirmed deletion retires its queue obligation. Identity availability is an
aggregate of current failed attempts, independent of the latest delivery notice;
verified key recovery clears only those attempts and their cleanup obligations.
This cleanup is durable even for a running attempt; a later cryptographic result
is fresh evidence. A newer recovery replaces the prior attempt's deferred cleanup.
After native process exit and callback drain, per-entry claim-release failures
remain owned by the outbox and do not reserve the transport for unrelated entries.
Startup and mutating key recovery inspect former claims before admission. A
usable existing identity requires no sender cancellation or key replacement.

Optional component retirement clears only that component's notices. API and
catalog callbacks are fenced to the activation or request that produced them.
A failed API iterator is discarded, and owner-level recovery requires a fresh
completed scan. Live API publication skips a busy reconciliation gate; a later
scan publishes the durable episode result without blocking history on that gate.
Publication contention belongs to its capture, whose failure retires only after
successful publication. Episode completion also retires post-report persistence
failures without duplicating an already published report notice.

The server lives in `server/telemetry-ingest`: ingestion validates and retains
signed archives, while the dashboard reads retained data and writes its own
index cache. Their application code, static dashboard assets, Docker definitions
and tests are versioned together in this repository.
