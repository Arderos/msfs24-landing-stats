"""Read-only, incrementally indexed view of the receiver's retained captures.

Only the private dashboard cache is writable. No ingestion Store is instantiated:
doing so would create/migrate the production database and prune uploads.
"""

import csv
import hashlib
import io
import json
import logging
import math
import os
import re
import sqlite3
import threading
import time
import zipfile
from collections import Counter, defaultdict
from contextlib import contextmanager
from datetime import datetime, timezone
from pathlib import Path


INDEX_VERSION = 3
# LandingRecord.NonFiniteStorageSentinel / LandingRecordFile.Restore use exact
# equality, not a magnitude threshold. Other finite values remain measurable.
NONFINITE_STORAGE_SENTINEL = -1.7976931348623157E308
MAX_ARCHIVE_BYTES = 64 * 1024 * 1024
MAX_EXPANDED_BYTES = 128 * 1024 * 1024
MAX_RESULTS_BYTES = 16 * 1024 * 1024
MAX_ROWS = 65536
SCAN_SECONDS = 30
UNKNOWN = "Unknown"
LOG = logging.getLogger(__name__)


def _number(value):
    if type(value) not in (int, float):
        return None
    try:
        return value if math.isfinite(value) and value != NONFINITE_STORAGE_SENTINEL else None
    except OverflowError:
        return None


def _text(value, limit=256):
    return value[:limit] if isinstance(value, str) and value.strip() else None


def _airport(value):
    text = value.strip()[:256] if isinstance(value, str) else ""
    return None if not text or text.casefold() in ("unknown", "unknown airport") else text.upper()


def _iso(timestamp):
    return datetime.fromtimestamp(timestamp, timezone.utc).isoformat().replace("+00:00", "Z") if timestamp else None


def _json(value):
    # Older result envelopes were not checked for non-finite numbers at ingress.
    if isinstance(value, dict):
        return {str(k)[:256]: _json(v) for k, v in value.items()}
    if isinstance(value, list):
        return [_json(v) for v in value]
    if isinstance(value, float):
        return _number(value)
    return value


def _distribution(values):
    values = sorted(v for v in values if _number(v) is not None)
    if not values:
        return {"count": 0, "min": None, "p50": None, "p90": None, "p95": None, "max": None, "mean": None}
    def percentile(q):
        position = (len(values) - 1) * q
        low = math.floor(position)
        high = math.ceil(position)
        fraction = position - low
        return _number(values[low] * (1 - fraction) + values[high] * fraction)
    return {"count": len(values), "min": values[0], "p50": percentile(.5), "p90": percentile(.9),
            "p95": percentile(.95), "max": values[-1], "mean": _number(math.fsum(value / len(values) for value in values))}


def _counts(values):
    return [{"name": name, "count": count} for name, count in sorted(Counter(v or UNKNOWN for v in values).items(), key=lambda x: (-x[1], x[0]))]


def _merge_counts(captures, key):
    values = Counter()
    for capture in captures:
        values.update(capture.get(key, {}))
    return [{"name": name, "count": count} for name, count in sorted(values.items(), key=lambda x: (-x[1], x[0]))]


def _read_member(archive, name, maximum):
    if archive.getinfo(name).file_size > maximum:
        raise ValueError(f"{name} exceeds dashboard read limit")
    with archive.open(name) as stream:
        content = stream.read(maximum + 1)
    if len(content) > maximum:
        raise ValueError(f"{name} exceeds dashboard read limit")
    return content


def _telemetry(archive):
    count = duplicates = backward = gaps = nonfinite = malformed = sequence_errors = 0
    duplicate_host = delivery_batches = 0
    seen = set()
    previous = previous_host = previous_sequence = None
    first = last = None
    deltas, host_deltas = [], []
    ranges = {}
    axes = set()
    motion_active = motion_inactive = motion_unknown = 0
    with archive.open("telemetry.csv") as raw:
        reader = csv.reader(io.TextIOWrapper(raw, encoding="utf-8-sig", errors="strict", newline=""))
        header = next(reader, [])
        if not header or len(header) > 1024 or len(set(header)) != len(header):
            raise ValueError("invalid telemetry header")
        columns = {name: index for index, name in enumerate(header)}
        if "simulation_time_s" not in columns:
            raise ValueError("telemetry simulation clock is missing")
        quality_channels = ("g_force", "agl_ft", "ground_speed_kt", "airspeed_indicated_kt", "velocity_world_y_fps", "pitch_deg", "bank_deg", "simulation_delta_s")
        validity = [(name[:-6], index) for name, index in columns.items() if re.fullmatch(r"controller_\d+_raw_[xyzruv]_valid", name)]
        for row in reader:
            count += 1
            if count > MAX_ROWS:
                raise ValueError("telemetry row limit exceeded")
            if len(row) != len(header) or sum(map(len, row)) > 16384:
                raise ValueError("invalid telemetry row width or size")
            parsed = []
            for cell in row:
                try:
                    value = float(cell)
                except (ValueError, OverflowError):
                    malformed += 1
                    value = None
                if value is not None and not math.isfinite(value):
                    nonfinite += 1
                    value = None
                parsed.append(value)
            current = parsed[columns["simulation_time_s"]]
            motion = parsed[columns["motion_simulation"]] if "motion_simulation" in columns else None
            motion_active += motion == 1
            motion_inactive += motion == 0
            motion_unknown += motion not in (0, 1)
            if current is not None:
                duplicates += current in seen
                seen.add(current)
                first = current if first is None else min(first, current)
                last = current if last is None else max(last, current)
                if previous is not None:
                    delta = current - previous
                    backward += delta < 0
                    if delta > 0 and math.isfinite(delta):
                        deltas.append(delta)
                        gaps += delta > 1
                previous = current
            host = parsed[columns["host_elapsed_s"]] if "host_elapsed_s" in columns else None
            if host is not None and previous_host is not None:
                delta = host - previous_host
                duplicate_host += delta == 0
                delivery_batches += 0 <= delta < .001
                if delta > 0 and math.isfinite(delta):
                    host_deltas.append(delta)
            previous_host = host
            sequence = parsed[columns["sequence"]] if "sequence" in columns else None
            if sequence is not None and previous_sequence is not None:
                sequence_errors += sequence <= previous_sequence
            previous_sequence = sequence
            for name in quality_channels:
                value = parsed[columns[name]] if name in columns else None
                if value is not None:
                    low, high = ranges.get(name, (value, value))
                    ranges[name] = (min(low, value), max(high, value))
            for name, index in validity:
                if parsed[index] == 1:
                    axes.add(name)
    duration = _number(last - first) if first is not None and last is not None else None
    sim_dt = _distribution(deltas)
    return {"rows": count, "unique_timestamps": len(seen), "columns": len(header), "duration_s": duration,
            "motion_channel_available": "motion_simulation" in columns,
            "active_motion_frames": motion_active if "motion_simulation" in columns else None,
            "inactive_motion_frames": motion_inactive if "motion_simulation" in columns else None,
            "unknown_motion_frames": motion_unknown,
            "confirmed_no_active_physics": count > 0 and motion_inactive == count,
            "sample_rate_hz": (len(seen) - 1) / duration if duration and len(seen) > 1 else None,
            "median_rate_hz": 1 / sim_dt["p50"] if sim_dt["p50"] else None,
            "max_gap_s": max(deltas) if deltas else None, "simulation_delta_s": sim_dt,
            "delivery_delta_s": _distribution(host_deltas), "duplicate_timestamps": duplicates,
            "backward_timestamps": backward, "gaps_over_1s": gaps, "nonfinite_values": nonfinite,
            "malformed_values": malformed, "sequence_errors": sequence_errors,
            "duplicate_delivery_timestamps": duplicate_host, "delivery_intervals_under_1ms": delivery_batches,
            "physical_axis_channels": sorted(axes), "channel_ranges": {name: {"min": low, "max": high} for name, (low, high) in ranges.items()}}


def _landings(results, session):
    if not isinstance(results, dict):
        raise ValueError("invalid landing results envelope")
    records = results.get("records", [])
    if not isinstance(records, list) or len(records) > 256:
        raise ValueError("invalid landing record list")
    output = []
    for record in records:
        if not isinstance(record, dict):
            raise ValueError("invalid landing record")
        summary = record.get("summary", record)
        if not isinstance(summary, dict):
            raise ValueError("invalid landing summary")
        series = record.get("series", {})
        pose = record.get("pose", {})
        legacy_series = summary.get("Series", [])
        pose_frames = len(pose.get("lat", [])) if isinstance(pose, dict) and isinstance(pose.get("lat"), list) else 0
        if not pose_frames and isinstance(legacy_series, list):
            pose_frames = sum(isinstance(row, dict) and _number(row.get("LatitudeDegrees")) is not None and _number(row.get("HeadingTrueDegrees")) is not None for row in legacy_series)
        geometry = summary.get("RunwayGeometry")
        metric_fields = {"aircraft_fpm": "InertialFpm", "surface_fpm": "SurfaceFpm", "peak_g": "ImpactPeakG", "impact_window_s": "ImpactWindowSeconds",
                         "pitch_deg": "PitchDegrees", "bank_deg": "BankDegrees", "ground_speed_kt": "GroundSpeedKnots", "airspeed_kt": "AirspeedKnots",
                         "reconstruction_residual_fpm": "ClosureReconstructionResidualFpm", "reconstruction_uncertainty_fpm": "ClosureReconstructionUncertaintyFpm"}
        metrics = {name: _number(summary.get(field)) for name, field in metric_fields.items()}
        # Old peak windows must not silently masquerade as the adaptive impact metric.
        metrics["legacy_peak_2s_g"] = _number(summary.get("PeakG2Seconds"))
        output.append({"id": _text(summary.get("Id"), 128), "timestamp": _text(summary.get("TimestampUtc")),
                       "airport": _airport(summary.get("Airport")), "runway": None if summary.get("Runway") == "—" else _text(summary.get("Runway")),
                       "airport_status": "known" if _airport(summary.get("Airport")) else "unresolved",
                       "aircraft": _text(summary.get("AircraftTitle")) or session.get("aircraft_title"),
                       "simulator": _text(summary.get("Simulator")) or session.get("simulator"),
                       "contact_number": _number(summary.get("ContactNumber")), "contact_count": _number(summary.get("ContactCount")),
                       "format_version": _number(summary.get("FormatVersion")), "pose_frames": pose_frames,
                       "runway_available": isinstance(geometry, dict) and bool(geometry),
                       "closure_available": summary.get("ClosureReconstructionAvailable") if type(summary.get("ClosureReconstructionAvailable")) is bool else None,
                       "geometry_source": _text(summary.get("ClosureReconstructionGeometrySource")),
                       "impact_status": _text(summary.get("ImpactStatus")), "metrics": metrics,
                       "summary": {k: _json(v) for k, v in summary.items() if k not in ("Series", "Engines", "ContactPoints")}})
    return output


def _index_archive(path, row):
    size = path.stat().st_size
    if size > MAX_ARCHIVE_BYTES or size != row["compressed_bytes"]:
        raise ValueError("archive size does not match retained metadata or exceeds limit")
    digest = hashlib.sha256()
    with path.open("rb") as source:
        for chunk in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(chunk)
    if digest.hexdigest() != row["sha256"]:
        raise ValueError("archive SHA-256 does not match retained metadata")
    with zipfile.ZipFile(path) as archive:
        infos = archive.infolist()
        names = [info.filename for info in infos]
        if len(names) > 4 or len(names) != len(set(names)) or not set(names) <= {"telemetry.csv", "session.txt", "landing-results.json", "decisions.json"}:
            raise ValueError("unexpected or duplicate archive entries")
        if not {"telemetry.csv", "session.txt"} <= set(names):
            raise ValueError("archive is missing required entries")
        if any(info.is_dir() or info.flag_bits & 1 for info in infos) or sum(info.file_size for info in infos) > MAX_EXPANDED_BYTES:
            raise ValueError("invalid or oversized archive")
        session = {}
        for line in _read_member(archive, "session.txt", 16384).decode("utf-8-sig").splitlines():
            if line.startswith("Generated by "):
                session["generator"] = line[:1024]
            elif "=" in line:
                key, value = line.split("=", 1)
                if len(key) <= 128 and len(value) <= 2048:
                    session[key] = value
        results_available = "landing-results.json" in names
        results = json.loads(_read_member(archive, "landing-results.json", MAX_RESULTS_BYTES)) if results_available else {}
        if results_available and (not isinstance(results, dict) or "records" not in results):
            raise ValueError("landing results are missing the record list")
        landings = _landings(results, session)
        journal = {"available": False, "truncated": None, "entries": []}
        if "decisions.json" in names:
            raw_journal = json.loads(_read_member(archive, "decisions.json", 1024 * 1024))
            if not isinstance(raw_journal, dict) or not isinstance(raw_journal.get("entries"), list) or len(raw_journal["entries"]) > 2048:
                raise ValueError("invalid decision journal")
            entries = raw_journal["entries"]
            if any(not isinstance(entry, dict) for entry in entries):
                raise ValueError("invalid decision entry")
            journal = {"available": True, "truncated": raw_journal.get("truncated"), "entries": _json(entries)}
        telemetry = _telemetry(archive)
        components = Counter(_text(e.get("component")) or UNKNOWN for e in journal["entries"])
        codes = Counter(f"{_text(e.get('component')) or UNKNOWN}.{_text(e.get('code')) or UNKNOWN}" for e in journal["entries"])
        invalid = telemetry["confirmed_no_active_physics"]
        track_failures = {"insufficient_samples", "invalid_pose", "clock_reversed", "insufficient_unique_times"}
        for landing in landings:
            landing["contact_validity"] = "confirmed_no_active_physics" if invalid else "recorded"
            landing["statistics_eligible"] = not invalid
            landing["replay_track_available"] = False if invalid else None
            # The presence of coordinate arrays is not proof that the client
            # successfully constructed a replay. Use its per-contact decision.
            for entry in journal["entries"]:
                details = entry.get("details", {})
                if entry.get("component") != "replay.track" or not isinstance(details, dict):
                    continue
                contact = details.get("contact_number")
                if (contact is None and len(landings) != 1) or (contact is not None and contact != landing["contact_number"]):
                    continue
                if not invalid and entry.get("code") == "track_created":
                    landing["replay_track_available"] = True
                elif entry.get("code") in track_failures:
                    landing["replay_track_available"] = False
        outcome = ("results_unavailable" if not results_available else
                   "invalid_contacts" if landings and invalid else "contacts" if landings else
                   "diagnostic" if codes.get("analysis.diagnostic_only") else "no_contacts")
        interruptions = [{key: e.get(key) for key in ("time_seconds", "component", "code", "reason", "details")}
                         for e in journal["entries"] if str(e.get("component", "")).startswith("recorder") and
                         e.get("code") in ("position_discontinuity", "plane_crash", "PLANE_CRASH", "teleport_started", "back_on_track_started")]
        replay_codes = Counter(f"{_text(e.get('component'))}.{_text(e.get('code'))}" for e in journal["entries"] if str(e.get("component", "")).startswith("replay."))
        axis_codes = Counter(_text(e.get("code")) or UNKNOWN for e in journal["entries"] if e.get("component") == "replay.inputs")
        roles = Counter()
        bindings = []
        for entry in journal["entries"]:
            if entry.get("component") == "replay.inputs" and entry.get("code") == "axis_selected":
                details = entry.get("details", {})
                role = details.get("role") if isinstance(details, dict) else None
                name = {0: "pitch", 1: "roll", 2: "yaw"}.get(role, UNKNOWN) if type(role) in (int, float) else UNKNOWN
                roles[name] += 1
                bindings.append({**details, "role": name})
        warnings = []
        if telemetry["rows"] != row["sample_count"]:
            warnings.append("Telemetry row count differs from receiver metadata")
        for key in ("backward_timestamps", "gaps_over_1s", "nonfinite_values", "malformed_values", "sequence_errors"):
            if telemetry[key]:
                warnings.append(f"{key}: {telemetry[key]}")
        if journal["truncated"]:
            warnings.append("Decision journal was truncated by the client")
        if landings and invalid:
            warnings.append("Saved contacts have no active-physics frames; excluded from landing statistics, not deleted")
        return _json({"session": session, "telemetry": telemetry, "landings": landings, "journal": journal,
                "results_available": results_available, "capture_outcome": outcome,
                "interruptions": interruptions,
                "members": [{"name": i.filename, "bytes": i.file_size, "compressed_bytes": i.compress_size} for i in infos],
                "warnings": warnings, "axis_bindings": bindings,
                "components": dict(components), "codes": dict(codes), "replay_codes": dict(replay_codes),
                "axis_codes": dict(axis_codes), "roles": dict(roles)})


class DashboardIndex:
    def __init__(self, root: Path, cache: Path):
        self.root, self.cache = Path(root).resolve(), Path(cache).resolve()
        if self.cache == self.root or self.cache.is_relative_to(self.root):
            raise ValueError("dashboard cache must be outside the read-only data root")
        self._lock = threading.RLock()
        self._stop = threading.Event()
        self._thread = None
        self._rows = []
        self._state = {"state": "starting", "total": 0, "indexed": 0, "pending": 0, "errors": 0,
                       "last_scan_at": None, "last_success_at": None, "error": None}
        self._source_generated_at = None
        self._storage = {}
        self._registry = {}
        self._signed_budgets = []

    @contextmanager
    def _connect_cache(self):
        connection = sqlite3.connect(str(self.cache / "dashboard.sqlite3"), timeout=5)
        connection.row_factory = sqlite3.Row
        try:
            yield connection
            connection.commit()
        except Exception:
            connection.rollback()
            raise
        finally:
            connection.close()

    def _initialize_cache(self):
        self.cache.mkdir(parents=True, exist_ok=True)
        with self._connect_cache() as connection:
            connection.execute("PRAGMA journal_mode=WAL")
            connection.execute("CREATE TABLE IF NOT EXISTS archive_index (sha256 TEXT PRIMARY KEY, version INTEGER NOT NULL, indexed_at REAL NOT NULL, payload TEXT, compact TEXT, error TEXT)")

    def start(self):
        if self._thread and self._thread.is_alive():
            return
        self._stop.clear()
        self._thread = threading.Thread(target=self._run, name="telemetry-dashboard-index", daemon=True)
        self._thread.start()

    def stop(self):
        self._stop.set()
        if self._thread:
            self._thread.join(timeout=5)

    def _run(self):
        while not self._stop.is_set():
            try:
                self._initialize_cache()
                self._refresh()
            except Exception as exc:
                LOG.exception("Dashboard index refresh failed")
                with self._lock:
                    self._state.update(state="error", error=f"{type(exc).__name__}: source database or dashboard cache unavailable")
            self._stop.wait(SCAN_SECONDS)

    def _source_rows(self):
        snapshot = os.environ.get("DASHBOARD_SNAPSHOT_PATH")
        if snapshot:
            with Path(snapshot).open("rb") as stream:
                content = stream.read(64 * 1024 * 1024 + 1)
            if len(content) > 64 * 1024 * 1024:
                raise ValueError("source snapshot exceeds size limit")
            envelope = json.loads(content)
            if not isinstance(envelope, dict) or not isinstance(envelope.get("captures"), list) or _number(envelope.get("generated_at")) is None:
                raise ValueError("invalid source snapshot")
            rows = envelope["captures"]
            if len(rows) > 100000:
                raise ValueError("source snapshot capture limit exceeded")
            text_fields = ("capture_id", "install_id", "sha256", "relative_path", "app_version", "capture_kind")
            numeric_fields = ("received_at", "compressed_bytes", "uncompressed_bytes", "sample_count", "schema_version")
            for row in rows:
                if not isinstance(row, dict) or any(not _text(row.get(key)) for key in text_fields) or any(type(row.get(key)) is not int or row[key] < 0 for key in numeric_fields):
                    raise ValueError("invalid source snapshot capture")
            if len({row["capture_id"] for row in rows}) != len(rows):
                raise ValueError("duplicate source snapshot capture")
            self._source_generated_at = envelope["generated_at"]
            storage = envelope.get("storage", {})
            self._storage = {key: _number(storage.get(key)) for key in ("quota_bytes", "retention_days", "free_bytes")} if isinstance(storage, dict) else {}
            registry = envelope.get("registry", {})
            self._registry = {key: _number(registry.get(key)) for key in ("active", "revoked")} if isinstance(registry, dict) else {}
            budgets = envelope.get("signed_attempt_byte_budgets", [])
            self._signed_budgets = [{"window_start": _number(row.get("window_start")), "bytes": _number(row.get("bytes"))} for row in budgets[:30] if isinstance(row, dict)] if isinstance(budgets, list) else []
            return sorted(rows, key=lambda row: (row["received_at"], row["capture_id"]), reverse=True)
        path = self.root / "telemetry.sqlite3"
        connection = sqlite3.connect(path.as_uri() + "?mode=ro", uri=True, timeout=3)
        connection.row_factory = sqlite3.Row
        try:
            connection.execute("PRAGMA query_only=ON")
            rows = [dict(row) for row in connection.execute("SELECT capture_id,install_id,sha256,relative_path,received_at,compressed_bytes,uncompressed_bytes,sample_count,schema_version,app_version,capture_kind FROM captures ORDER BY received_at DESC, capture_id")]
        finally:
            connection.close()
        self._source_generated_at = time.time()
        return rows

    def _refresh(self):
        rows = self._source_rows()  # Keep no source transaction open while reading ZIP files.
        now = time.time()
        with self._lock:
            initial_scan = self._state["last_success_at"] is None
            self._state.update(state="indexing", total=len(rows), indexed=0, errors=0, pending=len(rows), last_scan_at=_iso(now), error=None)
        accepted = (self.root / "accepted").resolve()
        output = []
        for row in rows:
            if self._stop.is_set():
                return
            with self._connect_cache() as connection:
                cached = connection.execute("SELECT version,indexed_at,compact,error FROM archive_index WHERE sha256=?", (row["sha256"],)).fetchone()
            compact, error = None, None
            if cached and cached["version"] == INDEX_VERSION and (not cached["error"] or now - cached["indexed_at"] < 300):
                compact = json.loads(cached["compact"]) if cached["compact"] else None
                error = cached["error"]
            else:
                try:
                    path = (self.root / row["relative_path"]).resolve()
                    if not path.is_relative_to(accepted) or not path.is_file():
                        raise ValueError("retained archive missing or outside accepted directory")
                    payload = _index_archive(path, row)
                    compact = {k: v for k, v in payload.items() if k not in ("journal", "session", "members")}
                    compact["landings"] = [{k: v for k, v in landing.items() if k != "summary"} for landing in payload["landings"]]
                    compact.update(aircraft=payload["session"].get("aircraft_title"), simulator=payload["session"].get("simulator"),
                                   journal_available=payload["journal"]["available"], journal_truncated=payload["journal"]["truncated"], journal_entries=len(payload["journal"]["entries"]))
                    compact["controllers"] = sorted({value for key, value in payload["session"].items() if re.fullmatch(r"control_input_source_\d+", key)})
                except (OSError, ValueError, zipfile.BadZipFile, KeyError, csv.Error, RecursionError, TypeError) as exc:
                    payload = None
                    error = f"{type(exc).__name__}: archive could not be indexed" if isinstance(exc, OSError) else f"{type(exc).__name__}: {str(exc)[:180]}"
                with self._connect_cache() as connection:
                    connection.execute("INSERT OR REPLACE INTO archive_index VALUES(?,?,?,?,?,?)", (row["sha256"], INDEX_VERSION, now,
                                       json.dumps(payload, allow_nan=False) if payload else None, json.dumps(compact, allow_nan=False) if compact else None, error))
            output.append({**row, **(compact or {}), "index_status": "error" if error else "ready", "index_error": error})
            with self._lock:
                if initial_scan:
                    self._rows = output.copy()
                self._state["indexed"] += not error
                self._state["errors"] += bool(error)
                self._state["pending"] -= 1
        with self._lock:
            self._rows = output
            self._state.update(state="ready", last_success_at=_iso(time.time()))
        # Bound the private cache to archives still retained by the receiver.
        hashes = {row["sha256"] for row in rows}
        with self._connect_cache() as connection:
            for cached in connection.execute("SELECT sha256 FROM archive_index").fetchall():
                if cached["sha256"] not in hashes:
                    connection.execute("DELETE FROM archive_index WHERE sha256=?", (cached["sha256"],))

    def _snapshot(self, days=0):
        cutoff = time.time() - max(0, days) * 86400 if days else 0
        with self._lock:
            rows = [row for row in self._rows if row["received_at"] >= cutoff]
            state = self._state.copy()
        last = state["last_success_at"]
        state["stale"] = not last or time.time() - datetime.fromisoformat(last.replace("Z", "+00:00")).timestamp() > SCAN_SECONDS * 3
        if self._source_generated_at:
            state["source_generated_at"] = _iso(self._source_generated_at)
            state["stale"] = state["stale"] or time.time() - self._source_generated_at > 120
        state["progress"] = (state["total"] - state["pending"]) / state["total"] if state["total"] else (1 if state["state"] == "ready" else 0)
        return rows, state

    @staticmethod
    def _item(row):
        landings = row.get("landings", [])
        eligible = [r for r in landings if r.get("statistics_eligible", True)]
        telemetry = row.get("telemetry", {})
        return {**{key: row.get(key) for key in ("capture_id", "app_version", "schema_version", "capture_kind", "compressed_bytes", "uncompressed_bytes", "sample_count", "index_status", "index_error")},
                "received_at": _iso(row["received_at"]), "aircraft": row.get("aircraft") or next((r["aircraft"] for r in landings if r["aircraft"]), None),
                "airports": sorted({r["airport"] for r in eligible if r["airport"]}),
                "runways": sorted({f"{r['airport'] or UNKNOWN} / {r['runway']}" for r in eligible if r["runway"]}),
                "capture_outcome": "unreadable" if row.get("index_status") == "error" else row.get("capture_outcome", "results_unavailable"),
                "known_contact_count": sum(bool(r["airport"]) for r in eligible),
                "unresolved_contact_count": sum(not r["airport"] for r in eligible),
                "invalid_contact_count": len(landings) - len(eligible),
                "landing_count": len(landings), "journal_entries": row.get("journal_entries"), "journal_available": row.get("journal_available"),
                "duration_s": telemetry.get("duration_s"), "sample_rate_hz": telemetry.get("sample_rate_hz"), "max_gap_s": telemetry.get("max_gap_s"), "warnings": row.get("warnings", [])}

    def list_captures(self, days=0, q="", airport="", aircraft="", version="", kind="", outcome="", page=1, page_size=50):
        rows, state = self._snapshot(days)
        page, page_size = max(1, page), min(200, max(1, page_size))
        items = []
        for row in rows:
            item = self._item(row)
            airport_matches = not airport or (_airport(airport) in item["airports"] if _airport(airport) else item["unresolved_contact_count"] > 0)
            if not airport_matches or aircraft and aircraft != item["aircraft"] or version and version != item["app_version"] or kind and kind != item["capture_kind"]:
                continue
            if outcome and item["capture_outcome"] != outcome:
                continue
            if q and q.casefold() not in " ".join(str(item.get(key) or "") for key in ("capture_id", "aircraft", "airports", "runways", "app_version", "capture_kind")).casefold():
                continue
            items.append(item)
        return {"items": items[(page - 1) * page_size:page * page_size], "total": len(items), "page": page, "page_size": page_size, "pages": math.ceil(len(items) / page_size), "index": state}

    def capture(self, capture_id):
        rows, _ = self._snapshot()
        row = next((row for row in rows if row["capture_id"] == capture_id), None)
        if row is None:
            return None
        result = {**self._item(row), "sha256": row["sha256"]}
        with self._connect_cache() as connection:
            cached = connection.execute("SELECT payload FROM archive_index WHERE sha256=?", (row["sha256"],)).fetchone()
        if cached and cached["payload"]:
            result.update({key: value for key, value in json.loads(cached["payload"]).items() if key not in ("components", "codes", "replay_codes", "axis_codes", "roles")})
        return _json(result)

    def summary(self, days=0):
        rows, state = self._snapshot(days)
        # Selected-period landing deduplication uses saved record IDs, not airport/time guesses.
        unique, unique_day, anonymous, total_records = {}, {}, [], 0
        daily = defaultdict(lambda: {"captures": 0, "compressed_bytes": 0, "samples": 0, "landings": 0})
        for row in rows:
            day = _iso(row["received_at"])[:10]
            daily[day]["captures"] += 1
            daily[day]["compressed_bytes"] += row["compressed_bytes"]
            daily[day]["samples"] += row["sample_count"]
            for landing in row.get("landings", []):
                total_records += 1
                if landing["id"]:
                    previous = unique.get(landing["id"])
                    if previous is None or (not previous.get("statistics_eligible", True) and landing.get("statistics_eligible", True)):
                        unique[landing["id"]] = landing
                        unique_day[landing["id"]] = day
                else:
                    anonymous.append(landing)
        # Retain and count rejected historical records, but do not let their
        # zero metrics masquerade as measured landings. Missing motion channels
        # and a missing G metric alone never reject a record.
        landings = [r for r in unique.values() if r.get("statistics_eligible", True)]
        for landing in landings:
            daily[unique_day[landing["id"]]]["landings"] += 1
        telemetry = [row["telemetry"] for row in rows if "telemetry" in row]
        ready = [row for row in rows if row["index_status"] == "ready"]
        journal_rows = [row for row in ready if row.get("journal_available")]
        metrics = {key: _distribution(landing["metrics"].get(key) for landing in landings) for key in ("aircraft_fpm", "surface_fpm", "peak_g", "legacy_peak_2s_g", "impact_window_s", "pitch_deg", "bank_deg", "ground_speed_kt", "airspeed_kt", "reconstruction_residual_fpm", "reconstruction_uncertainty_fpm")}
        metrics.update({key: _distribution(t.get(key) for t in telemetry) for key in ("sample_rate_hz", "duration_s", "max_gap_s")})
        metrics["axis_correlation"] = _distribution(abs(binding["correlation"]) for row in ready for binding in row.get("axis_bindings", []) if _number(binding.get("correlation")) is not None)
        metrics["axis_lag_s"] = _distribution(binding.get("lag_seconds") for row in ready for binding in row.get("axis_bindings", []))
        metrics["compression_ratio"] = _distribution(row["uncompressed_bytes"] / row["compressed_bytes"] for row in rows if row["compressed_bytes"] > 0)
        pose = sum(landing["pose_frames"] > 0 for landing in landings)
        replay_tracks = sum(landing.get("replay_track_available") is True for landing in landings)
        runway = sum(landing["runway_available"] for landing in landings)
        totals = {"captures": len(rows), "unique_landings": len(landings), "unique_saved_records": len(unique), "landing_records": total_records,
                  "invalid_historical_contacts": len(unique) - len(landings),
                  "invalid_historical_records": sum(not r.get("statistics_eligible", True) for row in rows for r in row.get("landings", [])),
                  "unresolved_airport_contacts": sum(not r["airport"] for r in landings),
                  "duplicate_landing_records": total_records - len(unique) - len(anonymous), "landings_without_id": len(anonymous),
                  "installations": len({row["install_id"] for row in rows}), "airports": len({r["airport"] for r in landings if r["airport"]}),
                  "aircraft": len({r["aircraft"] for r in landings if r["aircraft"]}),
                  "compressed_bytes": sum(r["compressed_bytes"] for r in rows), "uncompressed_bytes": sum(r["uncompressed_bytes"] for r in rows),
                  "samples": sum(r["sample_count"] for r in rows), "journal_captures": len(journal_rows), "pose_landings": pose, "runway_landings": runway}
        quality = {key: sum(t[key] for t in telemetry) for key in ("duplicate_timestamps", "backward_timestamps", "gaps_over_1s", "nonfinite_values", "malformed_values", "sequence_errors", "delivery_intervals_under_1ms")}
        quality.update(archives_with_gaps=sum(t["gaps_over_1s"] > 0 for t in telemetry), sample_count_mismatches=sum(row["sample_count"] != row["telemetry"]["rows"] for row in ready), index_errors=sum(row["index_status"] == "error" for row in rows), indexed_captures=len(ready))
        result = {"generated_at": _iso(time.time()), "days": days, "index": state, "totals": totals,
                  "storage": {**self._storage, "used_bytes": sum(r["compressed_bytes"] for r in self._rows)},
                  "registry": self._registry.copy(), "signed_attempt_byte_budgets": self._signed_budgets.copy(),
                  "daily": [{"date": day, **values} for day, values in sorted(daily.items())],
                  "breakdowns": {"airports": _counts(r["airport"] for r in landings), "runways": _counts(f"{r['airport'] or UNKNOWN} / {r['runway']}" if r["runway"] else None for r in landings),
                                 "aircraft": _counts(r["aircraft"] for r in landings), "versions": _counts(r["app_version"] for r in rows),
                                 "schemas": _counts(str(r["schema_version"]) for r in rows), "kinds": _counts(r["capture_kind"] for r in rows),
                                 "outcomes": _counts(self._item(r)["capture_outcome"] for r in rows),
                                 "controllers": _counts(device for row in ready for device in row.get("controllers", [])),
                                 "simulators": _counts(r.get("simulator") for r in ready), "impact_status": _counts(r["impact_status"] for r in landings),
                                 "geometry_sources": _counts(r["geometry_source"] for r in landings)},
                  "metrics": metrics, "quality": quality,
                  "journals": {"available": len(journal_rows), "missing": len(ready) - len(journal_rows), "truncated": sum(bool(r.get("journal_truncated")) for r in journal_rows),
                               "entries": sum(r.get("journal_entries", 0) for r in journal_rows), "components": _merge_counts(rows, "components"), "codes": _merge_counts(rows, "codes")},
                  "axis": {"journal_captures": sum(bool(r.get("axis_codes")) for r in rows), "selected_roles": _merge_counts(rows, "roles"), "selection_codes": _merge_counts(rows, "axis_codes")},
                  "replay": {"pose_landings": pose, "confirmed_track_landings": replay_tracks,
                             "track_availability_unknown": sum(r.get("replay_track_available") is None for r in landings),
                             "runway_landings": runway, "closure_available": sum(r["closure_available"] is True for r in landings),
                             "closure_unavailable": sum(r["closure_available"] is False for r in landings), "closure_unknown": sum(r["closure_available"] is None for r in landings), "codes": _merge_counts(rows, "replay_codes")},
                  "recent": [self._item(row) for row in rows[:10]],
                  "notes": ["Counts cover retained receiver archives, not lifetime uploads. Period filters use upload time in UTC.",
                            "Airport, runway, aircraft and landing-metric aggregates deduplicate saved landing IDs; each contact is a separate landing record.",
                            "Historical records with explicitly inactive physics in every raw frame are retained but excluded from landing statistics. Missing motion channels are not treated as inactive.",
                            "Coordinate channels and a confirmed usable replay track are different metrics; replay availability requires a client decision for that contact.",
                            "Installations are anonymous client installations, not identified people. Upload/telemetry/journal totals include repeat uploads of the same landing.",
                            "Index errors are dashboard read/parse failures, not rejected uploads. Historical ingress rejection counts are unavailable.",
                            "Missing metadata is unknown, not zero. G impact and historical two-second peaks are kept separate. FPM metrics use the stored positive-down convention.",
                            "Sampling rate and gaps use simulator time; delivery timing is reported separately and is never used to derive G."]}
        return _json(result)
