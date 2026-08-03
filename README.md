# MSFS 2024 Landing Stats

A Windows desktop application that records, analyzes, and visualizes landings in
Microsoft Flight Simulator 2024 through SimConnect.

## Features

- Automatic recording from the descending 500 ft AGL crossing through rollout.
- Per-contact analysis for normal landings and bounces.
- Inertial vertical speed at contact, MSFS surface-normal touchdown velocity,
  surface-relative delta, terrain contribution, and unresolved remainder.
- Vertical, longitudinal, and lateral load-factor graphs.
- Synchronized hover and zoom across flight-path, controls, attitude, engine,
  and landing-gear charts.
- Flight-control inputs and surfaces, engine power and throttles, flare,
  attitude, wind, gear compression, and rollout data.
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
