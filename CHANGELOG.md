# Changelog

All notable changes to MSFS Landing Stats are documented in this file.

## [0.7.1] - 2026-08-05

### Fixed

- Replaced the custom self-extracting launcher, which Microsoft Defender could
  classify as `Wacatac`/`Sabsik`, with a normal portable ZIP and standalone
  updater. Both executable roles now scan clean with Internet Zone metadata.
- A successfully verified update now shuts down the old process, transactionally
  replaces the five application files, starts the new version, and removes its
  temporary updater without invoking a shell.

### Security

- Introduced a strict format-2 release manifest binding both the portable ZIP
  and updater by name, size, and SHA-256 under the pinned RSA release key.
- The updater independently repeats manifest verification from the immutable
  versioned release URL, verifies its own signed hash, accepts only the exact
  five-file package with bounded expansion, confirms the requesting PID and EXE,
  checks the installed assembly version, and rolls back partial replacement.

### Changed

- v0.7.0 used the removed format-1 self-extracting bundle and therefore needs a
  one-time manual extraction of v0.7.1. Automatic update-and-restart applies to
  subsequent format-2 releases.

## [0.7.0] - 2026-08-05

### Added

- Added a silent GitHub release updater. Every update is accepted only after
  verifying a pinned RSA signature, the signed asset size and SHA-256 hash, and
  the downloaded single-file bundle; installation is atomic and takes effect on
  the next application start.
- `DEBUG RAW` is now an explicit opt-in telemetry contribution mode. Each
  installation creates its own Windows-protected signing key, enrolls once with
  an invitation code, signs every archive request, and removes the temporary
  local queue copy only after the receiver acknowledges it.
- Added a containerized telemetry receiver with Cloudflare Tunnel ingress,
  strict schema-v5 ZIP validation, replay protection, per-source rate limits,
  per-installation and global storage quotas, free-space reserve, retention,
  and bounded container logs.

### Changed

- Diagnostic archives are no longer retained as a local `Raw Captures` corpus.
  While contribution mode is enabled, closed chunks use a bounded temporary
  queue and are uploaded automatically; ordinary landing records remain local.
- Tagged releases now fail closed unless the update manifest can be signed and
  publish the executable, canonical manifest, and detached signature together.

### Security

- Telemetry enrollment is invite-only. The public endpoint has no generic file
  upload path and rejects unsigned, stale, replayed, oversized, malformed, or
  quota-exceeding captures before they enter the accepted corpus.

## [0.6.0] - 2026-08-05

### Added

- Added an experimental surface-closure reconstruction that combines a frozen
  pre-contact temporal model, local terrain motion, pitch rotation, and a signed
  main-gear arm recovered entirely from telemetry.
- Added a reconstruction detail panel with modeled closure, raw-minus-model
  residual, components, uncertainty, geometry provenance, and dark inline help
  tooltips; the raw simulator latch remains the headline surface value.
- Landing history v7 now persists the optional reconstruction result while
  remaining compatible with existing v7 files that do not contain those fields.
- Expanded the public methodology with the frozen equation, effective timing,
  telemetry-only geometry calibration, validation corpus, and accuracy limits.

### Changed

- Reconstruction no longer assigns nose/main roles from contact-point indices.
  It infers a sustained two- or three-main sequence followed by the nose, rejects
  nose-first and ambiguous contacts, and leaves all legacy metrics unchanged.
- Quadratic reconstruction now requires at least five pre-contact timestamps;
  sparse 3-4 frame fits are reported as unavailable instead of extrapolating
  quantization noise with a primary uncertainty label.
- Rigid-body world-vertical projection is shared by geometry calibration and
  reconstruction, while the validated v1 model explicitly keeps its `omega_y`
  term disabled.

## [0.5.0] - 2026-08-04

### Added

- The landing header now shows wind direction and speed from the sample nearest
  the selected contact.
- Added a rebuildable summary index so history opens without inflating every
  landing detail, while the selected record is loaded lazily.
- Documented the strict columnar landing-detail format v7, including non-finite
  value encoding, column-length invariants, and v1-v6 read compatibility.
- The footer version and author now link directly to the GitHub Releases page.
- Added a current application screenshot and a feature-by-feature comparison to
  the README.
- Added a public measurement-methodology document covering contact timing,
  aircraft and surface-relative rates, terrain decomposition, G windows,
  validation evidence, and known limitations.

### Changed

- Reduced the full-rate SimConnect frame from more than 2 KB to 892 bytes and
  123 values by removing channels unused by analysis, storage, or charts.
- Reduced contact-point capture from 64 speculative indices to the documented
  SimConnect range of 0 through 19 while retaining all four supported engines.
- Debug RAW telemetry now uses schema v5; schema v4 captures with 64 contact
  columns remain readable.
- A 32-byte guard request now runs continuously; the full 892-byte request is
  enabled below 3,000 ft AGL and disabled above 3,500 ft, with DEBUG RAW as an
  explicit override.
- Aircraft metadata uses SimConnect's changed-only delivery, and the persisted
  airport cache avoids a world facility-list request unless it is empty or no
  cached airport lies within 20 NM of the landing.
- Debug RAW frames stream through a bounded queue directly into rotating ZIP
  archives instead of accumulating an entire chunk in application memory.
- Landing detail v7 stores time series in parallel columns and stores only raw
  controller sources that were live during the recorded window.
- Bounced landings are ordered newest-contact-first, with the last contact at
  the top of the history and selected by default.
- Replaced internal SimConnect terminology in the dashboard with aircraft
  vertical rate and surface closure rate; shortened the raw-pitch source label.
- The surface closure metric now forms a matching dashed horizontal/vertical
  marker at touchdown, and dashed control-surface/throttle curves have hover
  readouts.
- DEBUG RAW uses an explicit dark-red checked state with high-contrast text.

## [0.4.3] - 2026-08-04

### Fixed

- Included the keep-last telemetry deduplicator in clean source checkouts, so
  the application and its regression suite build correctly on GitHub Actions.

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
