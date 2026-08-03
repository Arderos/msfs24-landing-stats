# Changelog

All notable changes to MSFS Landing Stats are documented in this file.

## [0.4.2] - 2026-08-04

### Fixed

- Live landing analysis now deduplicates repeated simulation instants with the
  same keep-last policy as the offline analyzer.
- A five-minute approach-window timeout now immediately re-arms capture instead
  of silently losing a later touchdown below 1,000 ft AGL.
- Debug RAW capture rotates automatically every 30,000 frames, bounding memory
  use while continuing into a new archive without a sampling gap.
- Full airport-facility refreshes now begin after an episode instead of during
  the 500 ft-to-touchdown recording window.
- Joysticks connected after application startup are detected automatically,
  and controller topology changes can no longer mix devices inside pre-roll.
- A transient SimConnect exception no longer leaves the application status in
  an error state after valid telemetry resumes.
- New landing records are inserted into the in-memory history without loading,
  resolving, and rewriting every stored landing on the UI thread.

### Changed

- Removed the obsolete SimConnect controller-enumeration path superseded by
  direct WinMM polling.
- Added regression checks for deduplication, approach timeout re-arming, RAW
  rotation, status recovery, and removal of the legacy controller path.

## [0.4.1] - 2026-08-04

### Added

- A purpose-built multi-resolution application icon embedded in the launcher,
  executable, taskbar window, and application title bar.

### Changed

- Replaced the visible measurement-status badges with the landing-character
  labels `SMOOTH` (up to 240 fpm) and `FIRM` (above 240 fpm); detailed data
  provenance remains available in tooltips.

## [0.4.0] - 2026-08-04

### Added

- A data-dense dark landing dashboard with synchronized hover, shared zoom,
  click-to-isolate legends, a common time cursor, and a compact event timeline.
- Automatic recording from the descending 500 ft AGL crossing through 15
  seconds of rollout, including per-contact records for bounced landings.
- Persistent nearest-airport resolution based on MSFS facility data, including
  backfilling of previously unresolved local landing records.
- Vertical, longitudinal, and lateral loads; flight controls and surfaces;
  attitude, AoA and sideslip; engine power, throttles and reverse; gear-contact
  compression; wind, brakes, spoilers, and rollout channels.
- Confidence metadata for extrapolated inertial rate, touchdown-latch timing,
  contact-time estimation, and surface-delta decomposition.
- Direct Windows joystick polling and automatic airborne correlation for raw
  pitch input when an aircraft exports a processed SimConnect pitch command.
- Telemetry schema v4 with controller provenance and physical control-surface
  diagnostics while retaining support for older capture schemas.
- Crash logging under `%LOCALAPPDATA%\MSFS Landing Stats\crash.log`.

### Changed

- Landing history now stores compact record format v6 with only dashboard data.
- Vertical speed is signed throughout the graphs; ranges are data-driven.
- Chart scale selection remains active while switching landings or bounce
  contacts.
- Weight is displayed in kilograms.
- The custom window frame is fully dark and remains resizable.
- Application version, author, and raw-capture provenance share assembly
  metadata from one source.

### Fixed

- Prevented Fenix's post-touchdown processed pitch signal from being presented
  as the pilot's physical sidestick input.
- Made rewrites of migrated landing records atomic.
- Removed misleading or redundant status text from the title bar.

## [0.3.0] - 2026-08-03

### Added

- Optional full-rate raw telemetry capture and single-executable packaging.

### Fixed

- Clean-build support for raw capture and automated release builds.

## [0.2.0] - 2026-08-03

### Fixed

- Automated extraction of the public MSFS 2024 SimConnect SDK dependency.

## [0.1.0] - 2026-08-03

### Added

- Initial MSFS Landing Stats desktop application.
