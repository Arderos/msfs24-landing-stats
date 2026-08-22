# Changelog

All notable changes to MSFS Landing Stats are documented in this file.

## [0.8.2] - 2026-08-22

### Added

- Added an optional **Start with Microsoft Flight Simulator** setting for both
  MSFS 2024 and MSFS 2020, including Steam and Microsoft Store installations.
- Added a one-time choice after installing or updating. The same option remains
  available in Settings and can be turned off at any time.

### Fixed

- Preserved every existing simulator auto-start entry byte-for-byte while
  adding or removing the entry owned by MSFS Landing Stats. Changes use an
  atomic replacement, keep a one-time backup of the original `exe.xml`, and
  fail without overwriting unsupported, malformed, or concurrently changed
  files.

## [0.8.1] - 2026-08-21

### Added

- Added a user-initiated **Report bug** workflow. The button appears only while
  the latest analyzed landing still has reportable telemetry and securely
  bundles that telemetry with the exact calculated landing result.
- Added backwards-compatible server ingestion for the new three-member bug
  report archive while retaining support for older RAW diagnostic captures.

### Changed

- Removed the full-flight RAW telemetry toggle from the interface. Bug reports
  now retain only the latest landing episode until the next sequence begins.
- Prioritized newly submitted bug reports ahead of consented legacy RAW files
  and made the upload queue drain its durable backlog as capacity becomes free.

### Fixed

- Persisted bug reports before any network or enrollment attempt, so an offline
  submission remains on disk and retries automatically after recovery or an app
  restart; expired enrollment is renewed with rate-compatible backoff.
- Waited for the local report ZIP to finish during application shutdown, so an
  immediate close cannot leave the only copy half-written.
- Enforced the upload queue's file-count and 256 MiB limits before scheduling,
  and removed an enrollment race that could strand an existing capture.
- Preserved an older user's explicit refusal to upload full-flight RAW data when
  submitting a new last-landing bug report.
- Rejected malformed archives without `session.txt` as validation errors instead
  of allowing an internal server error.
- Prevented long upload status paths from crowding the application footer.

## [0.8.0] - 2026-08-17

### Added

- Added CFG-aware landing-gear geometry. The application discovers readable
  `flight_model.cfg` files in Microsoft Store/Xbox and Steam installations,
  follows safe `base_container` and MSFS 2024 modular attachment layers, and
  uses exact aircraft-title matching rather than guessing from an ATC model.
- Added per-wheel main/nose roles and longitudinal arms when the final CFG
  indices agree with live SimConnect compression channels. Complex or
  encrypted aircraft retain the telemetry fallback; observed A380 and ToLiss
  A340 layouts are both supported.
- Added explicit replay handling. MSFS replay flow events disable capture and
  show a dismissible in-app warning, while an independent kinematic detector
  rejects replay-like episodes when flow events are unavailable. Live capture
  re-arms automatically after the aircraft is airborne.
- Added physical-strut grouping to the landing-gear chart, including four
  A340 gear groups, helper-contact filtering, and enough distinct colors for
  multi-gear and four-engine aircraft.

### Changed

- Reconstruction now records whether landing geometry came from the installed
  flight model or telemetry. CFG wheel coordinates receive the independently
  recovered simulator-datum correction before the contact-time pitch component
  is calculated.
- Three-to-five settled contact-point layouts are supported with delayed-nose
  and positive-derotation checks. Larger multi-bogie layouts use conservative
  clustered inference instead of being rejected solely by point count.
- Full SimConnect telemetry is gated below 3,000 ft AGL with hysteresis; cruise
  uses a compact five-field frame guard, while `DEBUG RAW` still forces the
  complete full-rate stream.
- Landing analysis, installed-aircraft scanning, persistence, and telemetry
  consent initialization now run away from the UI thread. Aircraft metadata is
  requested only when changed, and the global airport list is refreshed only
  when the local cache cannot resolve the landing safely.
- The single-file launcher keys its private runtime to the embedded payload,
  not just the visible version number, and safely removes obsolete runtime and
  packaging directories.

### Fixed

- Preserved A340 reconstruction when an add-on exposes extra visual-bogie
  compression channels whose numbering does not match its readable CFG. The
  configured median arm is retained while gear roles fall back to telemetry.
- Prevented an airport-facility refresh racing landing analysis from leaving a
  new record as `Unknown airport` or rolling the persistent cache backward.
- Drained in-flight episode persistence during shutdown so a landing completed
  while the application closes is not lost.
- Kept replay frames out of both landing history and the consented raw
  telemetry queue.
- Corrected A340 crosswind/touch-and-go gear grouping, helper contacts with an
  unsettled nose, the fourth engine's throttle color, and missing zero labels
  on charts.
- Quarantined permanently rejected telemetry archives instead of retrying them
  forever, while retaining the local data and correct queue accounting.
- Preserved exact 32-byte binary telemetry-server peppers instead of trimming
  random boundary bytes that happen to match whitespace characters.
- Made optional CFG discovery, modular parsing, and catalog refresh failures
  fail closed to telemetry analysis instead of losing the landing.

### Security

- The telemetry receiver now rejects replay-like captures independently by
  comparing recorded position and altitude motion with reported flight
  velocities before an archive enters the accepted corpus.
- The tagged-release gate now proves that the verifier embedded in the actual
  published v0.7.9 client accepts the signed v0.8.0 channel manifest before the
  GitHub release is created.

## [0.7.9] - 2026-08-10

### Fixed

- Automatic updates now preserve browser-renamed executable names such as
  `MSFS-Landing-Stats (2).exe`. Process identity, bundle, signature, hash,
  version, atomic replacement, and rollback checks remain enforced.

## [0.7.8] - 2026-08-10

### Added

- Added complete English and Russian interface localization, including charts,
  reconstruction details, status messages, dialogs, and explanatory tooltips.
- Added an in-app language setting with automatic locale detection and explicit
  English or Russian selection. Unknown future settings are preserved.

### Changed

- Standardized interface terminology in both languages, including LANDINGS,
  FULL TELEMETRY, engine labels, and the monthly vertical-rate summary.

### Fixed

- Preserved intentional leading spaces in translated inline suffixes so times,
  contact numbers, update states, and geometry quality no longer run together.
- Preserved paragraph breaks in the full-telemetry consent explanation.
- Updated chart units immediately when the interface language changes.
- Kept the no-touchdown warning amber, including across a language switch,
  instead of reverting it to a normal green connected state.
- Reset full-telemetry status colors after an error rather than leaving later
  successful statuses red.
- Restored the explicit "saved locally" status when telemetry upload is
  unavailable; local capture remains independent from network delivery.
- Restored distinct screen-reader names and help text for information buttons.
- Made language-resource replacement independent of hard-coded assembly names,
  and load each language dictionary only once per session.
- Removed stale localization resources and display paths that could diverge
  from the values actually shown by the interface.
- Made localized update-result construction resistant to ambiguous positional
  arguments.
- Kept paused or frozen simulator frames from growing an active landing episode
  indefinitely by deduplicating frozen time and enforcing a hard sample cap.
- Corrected internal chart legend colors to match the rendered series and hover
  values in every multi-series mode.
- Kept the telemetry upload worker alive when its protected identity file is
  corrupt, without disabling local full-rate recording.
- Made telemetry enqueue and shutdown atomic so a capture finishing while the
  application closes cannot fault the writer task.
- Quarantined and rebuilt a permanently corrupt airport cache while preserving
  the existing read-tolerant behavior for transient file errors.
- Counted only the primary contact in monthly averages so bounce contacts no
  longer distort the displayed landing rate.
- Used the actual display DPI for chart and timeline text, improving alignment
  and sharpness above 100% Windows scaling.
- Removed abandoned raw-capture temporary chunks and obsolete versioned runtime
  directories without touching active or locked data.
- Batched landing-summary reconciliation so startup writes the compressed index
  once instead of once per corrected landing.

### Performance

- Replaced per-mouse-move chart sorting with binary search over the already
  ordered telemetry series.
- Cached telemetry CSV schema widths and split each data row only once.
- Removed unused demo-history generation code from the production model.

## [0.7.7] - 2026-08-08

### Added

- Added landing deletion from the session history. The delete action appears on
  row hover and requires confirmation in an in-app modal overlay.

### Changed

- Restyled the session-history scrollbar with a compact dark rail and orange
  thumb matching the rest of the application.

## [0.7.6] - 2026-08-05

This release supersedes v0.7.5 and includes its complete consolidated history
below.

### Changed

- Completed the transition back to signed manifest format 3 and direct
  single-executable updates. Release ZIPs are no longer produced or published.
- Split update metadata into an immutable bootstrap manifest and a moving
  release channel. Every v0.7.5 installation first updates to the v0.7.6
  bridge; v0.7.6 and later then follow the current signed channel.

### Security

- Added a two-stage release gate. It rejects a release unless the bootstrap
  manifest is the signed v0.7.6 bridge, the channel manifest names the exact
  release being built, and both manifests match the exact executable and
  updater bytes they authorize.
- The updater accepts only the two fixed manifest names and re-verifies the
  selected manifest from the immutable versioned release before replacement.

## [0.7.5] - 2026-08-05

This release supersedes v0.7.4 and includes its complete consolidated history
below.

### Fixed

- Restored update-manifest compatibility with every issued v0.7.x client. The
  latest release now uses the signed format-2 package contract understood by
  the original v0.7.3 verifier, while the downloaded updater securely installs
  the same one-file application. Existing clients update without cleanup,
  reinstalling, or replacing their old executable by hand.

## [0.7.4] - 2026-08-05

This release supersedes v0.7.3 and includes its complete consolidated history
below.

### Changed

- `DEBUG RAW` now registers a consenting installation automatically. The manual
  invitation-code window and server-side invite administration were removed.
- Installations remain anonymous: identity is a random installation ID plus an
  RSA key protected by Windows DPAPI. No hardware UUID, MachineGuid, account
  name, or other persistent hardware fingerprint is collected.
- Local `DEBUG RAW` capture is independent from telemetry upload. Registration,
  network, quota, or server failures never disable full-rate local recording;
  completed ZIPs remain on disk until the server explicitly accepts them.
- Telemetry is initialized lazily only after `DEBUG RAW` is enabled. A normal
  application session performs no telemetry registration or queue work.

### Security

- Open registration does not weaken the storage boundary: source registration
  is capped at 10/hour, all sources share a 1,000/hour budget, the durable
  registry is atomically capped at 100,000 identities, and stale unreferenced
  identities expire after 30 days. New identities are rejected below the 2 GiB
  disk reserve.
- Every archive still requires a signature from its enrolled key, and
  per-installation, per-source, global daily, total storage, expansion, request-
  size, and free-space limits remain fail-closed.
- A revoked installation key cannot silently re-register with the same identity.

## [0.7.3] - 2026-08-05

This is the single supported public release and supersedes v0.1.0 through
v0.7.2. Their complete historical notes are included below in this release.

### Changed

- Restored the original one-file distribution. Users download and run only
  `MSFS-Landing-Stats.exe`; there is no ZIP to extract and no group of files to
  keep together.
- Replaced the multi-file update transaction with a single-executable update.
  The application verifies a signed manifest and temporary updater, exits, and
  the updater independently verifies and atomically replaces the one public
  executable before restarting it and removing itself.
- The application payload remains inside the executable and is prepared in a
  private versioned runtime directory. A new executable prepares its own new
  runtime, so an update cannot be undone by an older bootstrap payload.

### Security

- Introduced signed update manifest format 3, binding the exact name, size, and
  SHA-256 of both the new single-file application and its standalone updater.
- The updater re-fetches the manifest from the immutable versioned GitHub
  release, verifies its own signed identity, checks the requesting process and
  target, validates the replacement as a complete PE bundle with the expected
  assembly version, and rolls back if replacement verification fails.
- The final Internet-Zone-marked single executable passes Microsoft Defender
  real-time/custom scanning and its embedded bundle self-verification.

## [0.7.2] - 2026-08-05

### Fixed

- Pre-roll retention now uses monotonic receipt time instead of simulation time
  and has a hard frame cap, so an ESC pause, frozen simulation clock, or zero-time
  loading frame cannot grow or permanently poison the buffer.
- Full-rate telemetry can no longer block the SimConnect/UI thread behind a slow
  disk or antivirus scanner. A saturated writer stops the diagnostic capture
  explicitly without silently dropping accepted frames, and shutdown has a
  bounded wait.
- Closure reconstruction now requires five distinct timestamps and brackets its
  `t_c - 75 ms` evaluation point inside the recorded history. The terrain
  sanitizer also repairs spikes at the beginning and end of short captures.
- Telemetry CSV rows must match the file header's schema, and binary64 values use
  the portable `G17` round-trip representation on .NET Framework.
- The landing summary index now reconciles itself with canonical detail files,
  recovering a landing when detail commit succeeded but index update did not.
  Landing filenames are culture-invariant.
- Airport refreshes now replace the live cache only after every pagination page
  arrives, and a transient cache read failure can no longer overwrite the
  accumulated database with one partial event.
- The selected landing header refreshes after asynchronous airport resolution;
  unavailable surface closure is no longer selectable in the outer legend, and
  non-finite telemetry no longer blanks an entire chart.
- SimConnect mode changes are guarded against the simulator exiting during a UI
  toggle, and joystick enumeration cannot remap controller slots mid-episode.

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
