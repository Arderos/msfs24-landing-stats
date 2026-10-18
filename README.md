# MSFS 2024 Landing Stats

[![Build](https://github.com/Arderos/msfs24-landing-stats/actions/workflows/build.yml/badge.svg?branch=main)](https://github.com/Arderos/msfs24-landing-stats/actions/workflows/build.yml)
[![Latest release](https://img.shields.io/github/v/release/Arderos/msfs24-landing-stats?display_name=tag&sort=semver)](https://github.com/Arderos/msfs24-landing-stats/releases/latest)
![Language](https://img.shields.io/badge/language-C%23-512BD4?logo=dotnet&logoColor=white)
![Platform](https://img.shields.io/badge/platform-Windows%2010%20%7C%2011-0078D4?logo=windows&logoColor=white)

![MSFS Landing Stats dashboard](assets/application-2026-08-10.png)

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
| G-load | One value with an unspecified sampling window | An adaptive window isolates each contact's load response; SMOOTH / NORMAL / FIRM uses its recorded G peak, not descent rate |
| Evidence | Summary values only | Synchronized black-box traces for motion, loads, controls, power, attitude, and gear |
| Data quality | A number is shown even when its source is ambiguous | Extrapolation, fallback, latch verification, and unavailable data are identified explicitly |
| Ownership | Often cloud-backed | Local, portable landing records with optional backup to the user's own Google Drive; automatic diagnostic reporting is enabled by default and can be disabled in Settings, with manual **Report bug** submission available when disabled |

The result is not a landing score or a structural inspection verdict. It is an
engineering view of what the simulator published, how the result was derived,
and where uncertainty remains. See the [measurement methodology](docs/methodology.md)
for the formulas, timing rules, validation traces, and known limitations. See
the [privacy policy](PRIVACY.md) for Google Drive backup and diagnostic-report
data handling.

## Features

- Automatic recording from the descending 500 ft AGL crossing through rollout.
- Per-contact analysis for normal landings and bounces.
- Automatic nearest-airport resolution that remains available after MSFS exits.
- Inertial vertical speed at contact and the raw MSFS surface-normal touchdown
  velocity remain separate headline measurements.
- Experimental detail reconstructs the raw latch from effective timing, local
  terrain motion, and pitch rotation around a gear arm read from the installed
  flight model when available. Readable wheel indices and per-wheel positions
  are used when they agree with live contact channels; complex or encrypted
  aircraft fall back safely to telemetry-derived gear roles or geometry. The
  result includes the remaining residual and a conservative uncertainty band.
- Vertical, longitudinal, and lateral load-factor graphs.
- Synchronized hover and zoom across flight-path, load, attitude, engine,
  and landing-gear charts.
- 3D reconstruction of new landings: a complete recorded track, generic aircraft
  moving with recorded position and attitude, and runway geometry from MSFS when
  it can be identified confidently. Orbit/zoom the camera, play the landing, or
  scrub time. Older files without recorded coordinates retain their charts.
- Flight-control inputs and surfaces, engine power and throttles, flare,
  attitude, wind, gear compression, and rollout data.
- All available physical joystick axes, including separate pedal devices, shown
  independently from simulator commands. Legacy raw pitch-source matching is
  retained; an axis name never implies a particular simulator binding.
- Automatic diagnostic reports (switchable in Settings) that securely bundle landing telemetry with
  its calculated results; no full-flight recording mode is exposed in the UI.
- Compact local landing history containing only the data used by the dashboard.
- Optional Google Drive backup for saved landings, language, and the MSFS 2024
  auto-start preference. Raw telemetry and bug reports are not included.
- Optional auto-start with detected Steam or Microsoft Store installations of
  MSFS 2024.
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

Normal packages require `GOOGLE_OAUTH_CLIENT_ID` and `GOOGLE_OAUTH_CLIENT_SECRET`
in the build environment. Official CI builds obtain them from GitHub Actions
Secrets. The build validates both inputs and the metadata in the actual
`LandingStats.Delivery.dll` before packaging or signing. Do not commit either
value. Existing `-RequireGoogleOAuthCredentials` calls remain supported.

For an unsigned developer/test package without Google Drive sign-in, opt out
explicitly with `-AllowMissingGoogleOAuthCredentials`. Pull-request CI uses this
option because it has no credentials. This option cannot be used for a signed
package; passing `-RequireGoogleOAuthCredentials:$false` alone does not disable
the normal-package guard.

The resulting user download and standalone update helper are written to:

```text
artifacts\MSFS-Landing-Stats.exe
artifacts\MSFS-Landing-Stats.Updater.exe
```

Download and run `MSFS-Landing-Stats.exe`. Nothing needs to be extracted or
kept beside it. The executable prepares its private runtime under LocalAppData;
the SDK is not required at runtime. The updater executable is downloaded only
when a newer signed version exists.

On the first launch after installing this feature, the app asks whether Landing
Stats should open automatically with MSFS 2024. The choice can be changed later
in Settings, and works with detected Steam and Microsoft Store installations.
When started with the simulator, Landing Stats also closes when MSFS exits.
Manually launched sessions stay open. Existing auto-start entries are updated
when the app runs; the new behavior applies from the next simulator launch.

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

Saved history stays local unless Google Drive backup is enabled. Removing a
landing from the local history removes its compact stored record.

Diagnostic telemetry is uploaded automatically by default. Settings can disable
it; automatic uploads are silent and the **Report bug** button is hidden while
they are enabled. Each archive contains `telemetry.csv`, `session.txt`,
`landing-results.json`, and `decisions.json`: raw recorded samples, calculated
results, and a bounded journal of the analyzer and 3D reconstruction decisions.
With automatic reporting disabled, the latest episode is retained in memory
until the next approach and can be submitted explicitly with **Report bug**.

Bug reporting registers an anonymous random installation ID and
Windows-protected public key automatically. No hardware UUID is collected.
Queued reports are written under:

```text
%LOCALAPPDATA%\MSFS Landing Stats\Telemetry Queue
```

The app signs reports with a per-installation private key protected by Windows
and deletes a queue copy only after the server accepts it. Failed reports remain
local for automatic retry. Existing RAW diagnostic captures left by older builds
use the same retry queue only when that user had previously consented to
diagnostic uploads, and may resume automatically at startup. A previous refusal
remains in force for those legacy files. New automatic capture applies to new
landing episodes. Opting out stops both new automatic reports and previously
consented legacy RAW retries; explicit manual bug reports remain independent.
Opting back in resumes eligible reports. The automatic queue is bounded to 128 archives / 256 MiB (including
quarantined files); a full queue skips new diagnostic copies, not saved landings.
New-format reports remain queued until the server advertises support.

The receiver accepts signed, automatically enrolled installations and exact
schema-v5/v6 archives, including user-initiated bug-report bundles with calculated
landing results.
It limits one upload to 16 MiB compressed and 64 MiB expanded, one installation
to 512 MiB per day (counting rejected signed attempts too), one source address
to 1 GiB/day, and all ingress to 4 GiB/day. The retained corpus is capped at
20 GiB while preserving a 2 GiB disk reserve. Automatic enrollment is limited
to 10/source/hour and 1,000 globally/hour; the durable registry is atomically
capped at 100,000 identities and removes stale unreferenced identities after
30 days. Invalid, incomplete, and replay-like files are never added to the
corpus. The receiver checks replay kinematics independently of the client by
requiring recorded position and altitude motion to agree with the reported
flight velocities.
New captures use telemetry schema v6 with the documented 20 numeric contact
points and all six WinMM axes per controller; the parser remains compatible with
earlier schema v4/v5 captures. The v5/v6 receiver must be deployed before releasing
the v6 client; older queued captures retain their original signed schema. Landing
details use the documented [columnar v7 format](docs/format-v7.md), while v1-v6
record files remain readable.

## Local integrations

External Windows applications can read landing results, historical telemetry,
aircraft and runway information, and component status through the optional local
API. Subscriptions deliver complete landing results before history/archive
compression and can resume after a disconnect.

Enable **Settings → Local API → Allow local applications to read landing data**.
It is off by default, applies only to this computer, and can be switched off
without restarting Landing Stats. Access is read-only and limited to local
applications running as the same Windows user.

See the [API protocol and data reference](docs/integration-api.md) and the
[standalone Python client](docs/examples/integration_client.py) for discovery,
queries, subscriptions, cursor recovery, limits and data availability.

See [CHANGELOG.md](CHANGELOG.md) for release history.
