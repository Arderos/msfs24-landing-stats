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
- One-file distribution for end users with signed automatic updates.

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

The resulting single executable is written to:

```text
artifacts\MSFS-Landing-Stats.exe
```

The executable verifies and extracts its private runtime under
`%LOCALAPPDATA%\MSFS Landing Stats\Runtime\App` on first launch. End users only
need `MSFS-Landing-Stats.exe`; the SDK is not required at runtime.

## Automated builds

GitHub Actions resolves the current MSFS 2024 Core SDK from Microsoft's public
SDK manifest, extracts and caches only the SimConnect build dependency, builds
the application on `windows-latest`, and publishes the single executable as a
workflow artifact. Tags matching `v*` create a GitHub release containing the
EXE plus an RSA-signed update manifest. The application pins the corresponding
public key and verifies the signature, asset size, SHA-256 hash, and application
bundle before replacing the launcher atomically for the next start.

## Data storage

Landing records are stored under:

```text
%LOCALAPPDATA%\MSFS Landing Stats\Landings
```

Ordinary landing records are never uploaded. Removing a landing from the local
history removes its compact stored record.

`DEBUG RAW` is a separate, opt-in contribution mode. Before it can be enabled,
the application states that the full-rate telemetry includes aircraft state,
coordinates, and controller/input channels and asks for explicit consent and a
one-time invitation code. Closed diagnostic chunks are staged temporarily under:

```text
%LOCALAPPDATA%\MSFS Landing Stats\Telemetry Queue
```

`DEBUG RAW` streams every received `SIM_FRAME` sample directly into a ZIP,
rotates every 30,000 frames, signs the upload with a per-installation private key
protected by Windows, and deletes the queue copy only after the server accepts
it. The local queue is capped at 256 MiB; if it cannot accept more data, capture
is disabled instead of growing without bound.

The receiver accepts only invited installations and exact schema-v5 archives.
It limits one upload to 16 MiB compressed and 64 MiB expanded, one installation
to 512 MiB per day (counting rejected signed attempts too), one source address
to 1 GiB/day, and all ingress to 4 GiB/day. The retained corpus is capped at
20 GiB while preserving a 2 GiB disk reserve. Invalid or incomplete files are
never added to the corpus.
New captures use telemetry schema v5 with the documented 20 numeric contact
points; the parser remains compatible with earlier schema v4 captures. Landing
details use the documented [columnar v7 format](docs/format-v7.md), while v1-v6
record files remain readable.

See [CHANGELOG.md](CHANGELOG.md) for release history.
