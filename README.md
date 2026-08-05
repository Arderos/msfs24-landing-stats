# MSFS 2024 Landing Stats

![MSFS Landing Stats dashboard](assets/application.png)

A Windows desktop application that records, analyzes, and visualizes landings in
Microsoft Flight Simulator 2024 through SimConnect.

## Why it is different

Most landing trackers reduce a touchdown to one FPM value without making clear
whether it is the aircraft's actual vertical motion, an indicated VSI value, or
the rate at which the gap to the runway closed. Those values can differ by
hundreds of feet per minute on the same landing.

MSFS Landing Stats keeps the measurements separate and preserves the evidence
behind them:

| | Typical single-number tracker | MSFS Landing Stats |
| --- | --- | --- |
| Landing rate | One FPM value | Aircraft vertical rate and surface closure rate, with explicit signs and definitions |
| Sloped or uneven scenery | Can be folded into the displayed FPM | Local terrain contribution is measured and shown separately |
| Simulator latch differences | Left unexplained | Experimental telemetry reconstruction separates effective timing, terrain, and pitch rotation with an explicit uncertainty band |
| Bounces | Often only the first or last contact survives | Every contact is analyzed and retained as its own event |
| G-load | One value with an unspecified sampling window | Peaks are calculated in declared 150 ms and 2 s windows, bounded by the next contact |
| Evidence | Summary values only | Synchronized black-box traces for motion, loads, controls, power, attitude, and gear |
| Data quality | A number is shown even when its source is ambiguous | Extrapolation, fallback, latch verification, and unavailable data are identified explicitly |
| Ownership | Often cloud-backed | Local, portable landing records; diagnostic telemetry is sent only after explicitly enabling `DEBUG RAW` |

The result is not a landing score or a structural inspection verdict. It is an
engineering view of what the simulator published, how the result was derived,
and where uncertainty remains. See the [measurement methodology](docs/methodology.md)
for the formulas, timing rules, validation traces, and known limitations.

## Features

- Automatic recording from the descending 500 ft AGL crossing through rollout.
- Per-contact analysis for normal landings and bounces.
- Automatic nearest-airport resolution that remains available after MSFS exits.
- Inertial vertical speed at contact and the raw MSFS surface-normal touchdown
  velocity remain separate headline measurements.
- Experimental detail reconstructs the raw latch from effective timing, local
  terrain motion, and pitch rotation around a telemetry-recovered gear arm; it
  reports the remaining residual and a conservative uncertainty band.
- Vertical, longitudinal, and lateral load-factor graphs.
- Synchronized hover and zoom across flight-path, controls, attitude, engine,
  and landing-gear charts.
- Flight-control inputs and surfaces, engine power and throttles, flare,
  attitude, wind, gear compression, and rollout data.
- Hardware pitch-input capture with automatic source matching when SimConnect
  exposes an aircraft-processed command instead of the pilot's controller axis.
- Optional, explicitly consented full-rate diagnostic contribution with a
  15-second pre-roll and authenticated upload.
- Compact local landing history containing only the data used by the dashboard.
- One-file distribution with signed automatic updates and restart.

The application keeps inertial and surface-relative landing rates separate.
This avoids treating runway slope or local scenery elevation changes as aircraft
vertical motion. A simulator touchdown latch is used only when its update can be
attributed to the current contact; otherwise dependent values are reported as
unavailable while independent terrain measurements are retained.

## Requirements

To run the application:

- Windows 10 or Windows 11;
- Microsoft Flight Simulator 2024.

To build locally:

- .NET SDK 10 or Visual Studio 2022 Build Tools;
- Microsoft Flight Simulator 2024 SDK with the SimConnect SDK installed.

## Build

```powershell
.\build-app.ps1 -MsfsSdkRoot "D:\MSFS 2024 SDK"
```

The resulting user download and standalone update helper are written to:

```text
artifacts\MSFS-Landing-Stats.exe
artifacts\MSFS-Landing-Stats.Updater.exe
```

Download and run `MSFS-Landing-Stats.exe`. Nothing needs to be extracted or
kept beside it. The executable prepares its private runtime under LocalAppData;
the SDK is not required at runtime. The updater executable is downloaded only
when a newer signed version exists.

## Automated builds

GitHub Actions resolves the current MSFS 2024 Core SDK from Microsoft's public
SDK manifest, extracts and caches only the SimConnect build dependency, and
builds the application on `windows-latest`. Tags matching `v*` publish the
single application executable, a temporary standalone updater, and an
RSA-signed format-3 manifest binding the exact version, names, sizes, and
SHA-256 hashes of both executables.

For an update, the running application first verifies the pinned manifest
signature and updater hash. The verified updater then re-downloads the manifest
from the immutable versioned release URL, repeats signature and self-hash
verification, verifies the package size and SHA-256 while streaming, accepts
only a complete single-file bundle with the signed assembly version, and
confirms both the requesting process and replacement target. After the
application exits, it atomically replaces that one executable with rollback on
failure, restarts the new version, and is removed by that new process. No shell
or script participates in the update.

## Data storage

Landing records are stored under:

```text
%LOCALAPPDATA%\MSFS Landing Stats\Landings
```

Ordinary landing records are never uploaded. Removing a landing from the local
history removes its compact stored record.

`DEBUG RAW` is a separate full-rate local recording mode. On first use, the
application explains that telemetry includes aircraft state, coordinates, and
controller/input channels and separately asks whether those captures may be
uploaded. Saying no keeps local recording active. Saying yes registers an
anonymous random installation ID and Windows-protected public key
automatically. No hardware UUID is collected. Closed diagnostic chunks are
written under:

```text
%LOCALAPPDATA%\MSFS Landing Stats\Telemetry Queue
```

`DEBUG RAW` streams every received `SIM_FRAME` sample directly into a ZIP and
rotates every 30,000 frames. Local capture never depends on registration or
network success. When upload is allowed, the app signs with a per-installation
private key protected by Windows and deletes a queue copy only after the server
accepts it. The uploader schedules at most 256 MiB at once; excess or failed
captures remain local and full-rate capture continues. If `DEBUG RAW` is never
enabled, the telemetry uploader is not initialized and sends no requests.

The receiver accepts signed, automatically enrolled installations and exact
schema-v5 archives.
It limits one upload to 16 MiB compressed and 64 MiB expanded, one installation
to 512 MiB per day (counting rejected signed attempts too), one source address
to 1 GiB/day, and all ingress to 4 GiB/day. The retained corpus is capped at
20 GiB while preserving a 2 GiB disk reserve. Automatic enrollment is limited
to 10/source/hour and 1,000 globally/hour; the durable registry is atomically
capped at 100,000 identities and removes stale unreferenced identities after
30 days. Invalid or incomplete files are never added to the corpus.
New captures use telemetry schema v5 with the documented 20 numeric contact
points; the parser remains compatible with earlier schema v4 captures. Landing
details use the documented [columnar v7 format](docs/format-v7.md), while v1-v6
record files remain readable.

See [CHANGELOG.md](CHANGELOG.md) for release history.
