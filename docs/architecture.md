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

Changes should stay with the component that owns the state. File reads,
SimConnect callbacks, controller polling and network requests must not be moved
into WPF rendering or input handlers.

## Controller values and assignments

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

Diagnostic reporting and Google Drive backup are separate operations. Automatic
diagnostics are enabled by default and can be disabled in Settings. Drive backup
is optional and contains compact history, not raw diagnostic archives. The local
API is separately opt-in. See [privacy](../PRIVACY.md).

The server lives in `server/telemetry-ingest`: ingestion validates and retains
signed archives, while the dashboard reads retained data and writes its own
index cache. Their application code, static dashboard assets, Docker definitions
and tests are versioned together in this repository.
