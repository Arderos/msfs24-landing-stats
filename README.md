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
| Bounces | Often only the first or last contact survives | Every contact is analyzed and retained as its own event |
| G-load | One value with an unspecified sampling window | Peaks are calculated in declared 150 ms and 2 s windows, bounded by the next contact |
| Evidence | Summary values only | Synchronized black-box traces for motion, loads, controls, power, attitude, and gear |
| Data quality | A number is shown even when its source is ambiguous | Extrapolation, fallback, latch verification, and unavailable data are identified explicitly |
| Ownership | Often cloud-backed | Local, portable, versioned records with no flight-data upload |

The result is not a landing score or a structural inspection verdict. It is an
engineering view of what the simulator published, how the result was derived,
and where uncertainty remains. See the [measurement methodology](docs/methodology.md)
for the formulas, timing rules, validation traces, and known limitations.

## Features

- Automatic recording from the descending 500 ft AGL crossing through rollout.
- Per-contact analysis for normal landings and bounces.
- Automatic nearest-airport resolution that remains available after MSFS exits.
- Inertial vertical speed at contact, MSFS surface-normal touchdown velocity,
  surface-relative delta, terrain contribution, and unresolved remainder.
- Vertical, longitudinal, and lateral load-factor graphs.
- Synchronized hover and zoom across flight-path, controls, attitude, engine,
  and landing-gear charts.
- Flight-control inputs and surfaces, engine power and throttles, flare,
  attitude, wind, gear compression, and rollout data.
- Hardware pitch-input capture with automatic source matching when SimConnect
  exposes an aircraft-processed command instead of the pilot's controller axis.
- Optional full-rate diagnostic capture with a 15-second pre-roll.
- Compact local landing history containing only the data used by the dashboard.
- One-file distribution for end users.

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
workflow artifact. Tags matching `v*` also create a GitHub release with the EXE.

## Data storage

Landing records are stored under:

```text
%LOCALAPPDATA%\MSFS Landing Stats\Landings
```

The app does not upload flight data. Removing a landing from the local history
removes its compact stored record.

Optional diagnostic captures are stored separately under:

```text
%LOCALAPPDATA%\MSFS Landing Stats\Raw Captures
```

`DEBUG RAW` streams every received `SIM_FRAME` sample directly into a ZIP and
rotates every 30,000 frames. The writer uses a bounded queue, so capture length
does not grow application memory; long captures can still consume substantial
disk space, so keep this mode for short diagnostic flights.
New captures use telemetry schema v5 with the documented 20 numeric contact
points; the parser remains compatible with earlier schema v4 captures. Landing
details use the documented [columnar v7 format](docs/format-v7.md), while v1-v6
record files remain readable.

See [CHANGELOG.md](CHANGELOG.md) for release history.
