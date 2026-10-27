import csv
import base64
import binascii
import io
import json
import math
import struct
import zipfile
from .capture_archive import CaptureArchive, MAXIMUM_SESSION_METADATA_BYTES
from .bounded_json import JsonScanner, check_json_graph
from dataclasses import dataclass, replace
from pathlib import Path


EXPECTED_RAW_ENTRIES = {"telemetry.csv", "session.txt"}
EXPECTED_BUG_REPORT_ENTRIES = {"telemetry.csv", "session.txt", "landing-results.json"}
CAPTURE_KINDS = ("raw_debug", "bug_report", "automatic_landing")
DECISION_JOURNAL_FORMAT_VERSION = 1
MAXIMUM_DECISION_JOURNAL_BYTES = 4 * 1024 * 1024
MAXIMUM_DECISION_ENTRIES = 8192
CONTROLLER_CATALOG_FORMAT_VERSION = 1
MAXIMUM_CONTROLLER_CATALOG_BYTES = 8 * 1024 * 1024
# Match the client's runtime/telemetry payload byte budget. Device count is
# derived from the payload, rather than limited to an arbitrary slot count.
MAXIMUM_CONTROLLER_BINARY_BYTES = 1024 * 1024
MAXIMUM_CONTROLLER_BASE64_CHARS = ((MAXIMUM_CONTROLLER_BINARY_BYTES + 2) // 3) * 4
csv.field_size_limit(max(csv.field_size_limit(), MAXIMUM_CONTROLLER_BASE64_CHARS))
MAXIMUM_RESULT_BYTES = 64 * 1024 * 1024
MAXIMUM_RESULT_RECORDS = 65536
BOOLEAN_COLUMNS = {
    "on_ground",
    "motion_simulation",
    "axis_elevator_set_valid",
    *(f"contact_{index}_on_ground" for index in range(20)),
    *(f"controller_{index}_raw_{axis}_valid" for index in range(8) for axis in "xyzruv"),
}
STATE_INTEGER_LIMITS = {
    "simulator_state": (0, 3),
    "pause_flags": (-1, 2**31 - 1),
    "flight_segment": (0, 2**63 - 1),
}


@dataclass(frozen=True)
class CaptureFacts:
    sample_count: int
    uncompressed_bytes: int
    capture_kind: str


@dataclass(frozen=True)
class _ReplaySample:
    time: float
    on_ground: bool
    latitude: float
    longitude: float
    altitude: float
    ground_speed: float
    airspeed: float
    world_y: float
    motion: bool
    flight_segment: int


def _median(values: list[float]) -> float:
    ordered = sorted(values)
    middle = len(ordered) // 2
    return (
        (ordered[middle - 1] + ordered[middle]) / 2.0
        if len(ordered) % 2 == 0
        else ordered[middle]
    )


def _great_circle_nautical_miles(first: _ReplaySample, second: _ReplaySample) -> float:
    latitude_one = math.radians(first.latitude)
    latitude_two = math.radians(second.latitude)
    latitude_delta = latitude_two - latitude_one
    longitude_delta = math.radians(second.longitude - first.longitude)
    haversine = (
        math.sin(latitude_delta / 2.0) ** 2
        + math.cos(latitude_one)
        * math.cos(latitude_two)
        * math.sin(longitude_delta / 2.0) ** 2
    )
    clamped = max(0.0, min(1.0, haversine))
    angle = 2.0 * math.atan2(math.sqrt(clamped), math.sqrt(1.0 - clamped))
    return 3440.065 * angle


def _is_replay_like(samples: list[_ReplaySample]) -> bool:
    # Keep physics/lifecycle boundaries at repeated simulator times. A pause has
    # already been removed using SDK state; an unexplained stop remains a barrier.
    unique: list[_ReplaySample] = []
    for sample in samples:
        if (unique and abs(sample.time - unique[-1].time) <= 1e-9
                and sample.flight_segment == unique[-1].flight_segment
                and sample.motion == unique[-1].motion):
            unique[-1] = sample
        else:
            unique.append(sample)
    if len(unique) < 9:
        return False

    has_been_airborne = False
    contact_index = -1
    segment_start = 0
    for position, sample in enumerate(unique):
        if position and sample.flight_segment != unique[position - 1].flight_segment:
            has_been_airborne = False
            segment_start = position
        if not sample.motion:
            has_been_airborne = False
            segment_start = position + 1
            continue
        if has_been_airborne and sample.on_ground:
            contact_index = position
            break
        if not sample.on_ground:
            has_been_airborne = True
    if contact_index <= 0:
        return False

    contact_time = unique[contact_index].time
    window_start = contact_time - 15.0
    window_end = contact_time - 0.5
    derived_ground_speeds: list[float] = []
    reported_ground_speeds: list[float] = []
    reported_airspeeds: list[float] = []
    altitude_rates: list[float] = []
    vertical_rate_errors: list[float] = []
    next_index = 0
    for position in range(segment_start, contact_index):
        first = unique[position]
        if first.on_ground or first.time < window_start or first.time > window_end:
            continue
        next_index = max(next_index, position + 1)
        while next_index < contact_index and unique[next_index].time - first.time < 0.5:
            next_index += 1
        if next_index >= contact_index:
            break

        second = unique[next_index]
        duration = second.time - first.time
        if second.on_ground or second.time > window_end or duration <= 0.0 or duration > 1.5:
            continue
        if not (
            -90.0 <= first.latitude <= 90.0
            and -90.0 <= second.latitude <= 90.0
            and -180.0 <= first.longitude <= 180.0
            and -180.0 <= second.longitude <= 180.0
        ):
            continue

        altitude_rate = (second.altitude - first.altitude) / duration
        reported_vertical_rate = (first.world_y + second.world_y) / 2.0
        derived_ground_speeds.append(
            _great_circle_nautical_miles(first, second) * 3600.0 / duration
        )
        reported_ground_speeds.append((abs(first.ground_speed) + abs(second.ground_speed)) / 2.0)
        reported_airspeeds.append((abs(first.airspeed) + abs(second.airspeed)) / 2.0)
        altitude_rates.append(abs(altitude_rate))
        vertical_rate_errors.append(abs(altitude_rate - reported_vertical_rate))

    if len(derived_ground_speeds) < 8:
        return False

    derived_ground_speed = _median(derived_ground_speeds)
    reported_ground_speed = _median(reported_ground_speeds)
    return (
        derived_ground_speed >= 30.0
        and reported_ground_speed <= 5.0
        and _median(reported_airspeeds) <= 30.0
        and derived_ground_speed >= reported_ground_speed * 4.0
        and _median(altitude_rates) >= 3.0
        and _median(vertical_rate_errors) >= 5.0
    )


def _session_values(content: bytes) -> dict[str, str]:
    if len(content) > MAXIMUM_SESSION_METADATA_BYTES:
        raise ValueError("session metadata is too large")
    text = content.decode("utf-8")
    result: dict[str, str] = {}
    for line in text.splitlines():
        if not line or line.startswith("Generated by "):
            continue
        if "=" not in line:
            raise ValueError("session metadata contains an invalid line")
        key, value = line.split("=", 1)
        if not key or key in result or len(key) > 64 or len(value) > 1024:
            raise ValueError("session metadata is malformed")
        result[key] = value
    return result


def _unique_json_object(pairs: list[tuple[str, object]]) -> dict[str, object]:
    result: dict[str, object] = {}
    for key, value in pairs:
        if key in result:
            raise ValueError("decision journal contains duplicate fields")
        result[key] = value
    return result


def _finite_number(value: object) -> bool:
    try:
        return type(value) in (int, float) and math.isfinite(value)
    except OverflowError:
        return False


def _bounded_text(value: object, maximum: int) -> bool:
    return isinstance(value, str) and 0 < len(value) <= maximum


def _validate_decision_journal(content: bytes, episode_id: int) -> None:
    if len(content) > MAXIMUM_DECISION_JOURNAL_BYTES:
        raise ValueError("decision journal is too large")
    try:
        check_json_graph(content)
        journal = json.loads(content.decode("utf-8"), object_pairs_hook=_unique_json_object)
    except (ValueError, UnicodeDecodeError, RecursionError) as exc:
        raise ValueError("decision journal JSON is invalid") from exc
    if (
        not isinstance(journal, dict)
        or set(journal) != {"format_version", "episode_id", "truncated", "entries"}
        or type(journal["format_version"]) is not int
        or journal["format_version"] != DECISION_JOURNAL_FORMAT_VERSION
        or type(journal["episode_id"]) is not int
        or journal["episode_id"] != episode_id
        or type(journal["truncated"]) is not bool
        or not isinstance(journal["entries"], list)
        or len(journal["entries"]) > MAXIMUM_DECISION_ENTRIES
    ):
        raise ValueError("decision journal envelope is malformed")
    for entry in journal["entries"]:
        if (
            not isinstance(entry, dict)
            or set(entry) != {"time_seconds", "component", "code", "reason", "details"}
            or not _finite_number(entry["time_seconds"])
            or not _bounded_text(entry["component"], 64)
            or not _bounded_text(entry["code"], 96)
            or not _bounded_text(entry["reason"], 1024)
            or not isinstance(entry["details"], dict)
            or len(entry["details"]) > 32
            or any(
                not _bounded_text(key, 64) or not _finite_number(value)
                for key, value in entry["details"].items()
            )
        ):
            raise ValueError("decision journal entry is malformed")


def _validate_controller_catalog(payload: bytes) -> None:
    if not payload:  # Explicitly unavailable, distinct from unknown (-1).
        return
    if len(payload) < 72 or len(payload) > MAXIMUM_CONTROLLER_BINARY_BYTES:
        raise ValueError("controller catalog length is invalid")
    magic = struct.unpack_from("<I", payload)[0]
    if magic in {0x3143434C, 0x3243434C}:  # LCC1/LCC2 durable metadata.
        generation, sequence, capture, revision, encoded_count = struct.unpack_from("<qqqqi", payload, 36)
        if not any(payload[4:20]) or not any(payload[20:36]) or generation <= 0 or sequence <= 0 or capture < 0:
            raise ValueError("controller catalog identity is invalid")
        if (magic == 0x3243434C) != (encoded_count < 0):
            raise ValueError("controller catalog version/layout is invalid")
        offset = 72
    else:
        if len(payload) < 146:
            raise ValueError("controller catalog length is invalid")
        magic, version, kind, length = struct.unpack_from("<IHHI", payload)
        generation = struct.unpack_from("<q", payload, 44)[0]
        connection, sequence, capture = struct.unpack_from("<qqq", payload, 53)
        if (magic != 0x5452534C or version != 1 or kind != 12 or length != len(payload) - 134
                or not any(payload[12:28]) or not any(payload[28:44]) or generation <= 0
                or payload[52] != 2 or connection != 0 or sequence <= 0 or capture < 0
                or struct.unpack_from("<q", payload, 93)[0] < 0
                or payload[133] not in {0, 1, 2}
                or (payload[133] == 0) != (not any(payload[117:133]))
                or (any(payload[117:133]) and not any(payload[101:117]))):
            raise ValueError("controller catalog identity is invalid")
        revision, encoded_count = struct.unpack_from("<qi", payload, 134)
        offset = 146
    extended = encoded_count < 0
    count = -encoded_count - 1 if extended else encoded_count
    if revision <= 0 or count > (len(payload) - offset) // 65 or (not extended and count > 8):
        raise ValueError("controller catalog revision/count is invalid")
    slots, devices = set(), set()

    def read_text(maximum):
        nonlocal offset
        size = struct.unpack_from("<i", payload, offset)[0]
        offset += 4
        if not 0 <= size <= maximum or offset + size > len(payload):
            raise ValueError("controller catalog text is invalid")
        if "\0" in payload[offset:offset + size].decode("utf-8", errors="strict"):
            raise ValueError("controller catalog text contains NUL")
        offset += size

    try:
        for _ in range(count):
            slot, device = struct.unpack_from("<ii", payload, offset)
            offset += 8
            if (slot < 0 or device < 0 or slot in slots or device in devices
                    or not extended and (slot >= 8 or device >= 16)):
                raise ValueError("controller catalog slot/device is invalid")
            slots.add(slot)
            devices.add(device)
            read_text(512)
            read_text(512)
            axes = payload[offset]
            offset += 1
            axis_count = 6
            instance = None
            if extended:
                axis_count = struct.unpack_from("<i", payload, offset)[0]
                offset += 4
                if offset + 32 > len(payload):
                    raise ValueError("controller catalog GUID is truncated")
                instance = payload[offset:offset + 16]
                offset += 32  # Instance GUID and product GUID.
            if axis_count not in {6, 8} or axes >= (1 << axis_count):
                raise ValueError("controller catalog axis mask/layout is invalid")
            if slot >= MAXIMUM_CONTROLLER_BINARY_BYTES // (8 + axis_count * 9):
                raise ValueError("controller catalog slot exceeds the payload byte budget")
            for axis in range(axis_count):
                minimum, maximum = struct.unpack_from("<II", payload, offset)
                offset += 8
                if axes & (1 << axis) and minimum >= maximum:
                    raise ValueError("controller catalog axis range is invalid")
        if extended:
            read_text(4096)
            binding_count = struct.unpack_from("<i", payload, offset)[0]
            offset += 4
            if not 0 <= binding_count <= (len(payload) - offset) // 30:
                raise ValueError("controller binding count exceeds its payload")
            for _ in range(binding_count):
                control, engine, instance, axis, inverted = struct.unpack_from("<Bi16siB", payload, offset)
                offset += 26
                if (control not in {0, 1, 2, 3} or not -1 <= engine <= 3
                        or (control != 3 and engine != -1) or not 0 <= axis <= 7
                        or inverted not in {0, 1} or not any(instance)):
                    raise ValueError("controller binding has an invalid control, identity or axis")
                read_text(4096)
            if offset < len(payload):
                marker, size = struct.unpack_from("<Ii", payload, offset)
                offset += 8
                if marker != 0x31464550 or size <= 0 or size != len(payload) - offset:
                    raise ValueError("controller profile evidence header is invalid")
                try:
                    evidence = json.loads(payload[offset:].decode("utf-8", errors="strict"))
                except (ValueError, RecursionError) as exc:
                    raise ValueError("controller profile evidence is not valid JSON") from exc
                if not isinstance(evidence, dict):
                    raise ValueError("controller profile evidence must be an object")
                offset += size
    except (struct.error, IndexError, UnicodeDecodeError) as exc:
        raise ValueError("controller catalog is truncated or malformed") from exc
    if offset != len(payload):
        raise ValueError("controller catalog contains trailing bytes")


def _validate_controller_samples(encoded: str, valid_axes: set[tuple[int, int]] | None = None) -> int | None:
    if not encoded:  # Legacy eight-by-six projection remains authoritative.
        return
    if len(encoded) > MAXIMUM_CONTROLLER_BASE64_CHARS:
        raise ValueError("controller observations exceed their byte budget")
    try:
        payload = base64.b64decode(encoded, validate=True)
    except (ValueError, binascii.Error) as exc:
        raise ValueError("controller observations are not valid base64") from exc
    if not 10 <= len(payload) <= MAXIMUM_CONTROLLER_BINARY_BYTES:
        raise ValueError("controller observation length is invalid")
    version, count, axes = struct.unpack_from("<Hii", payload)
    if version != 2 or count < 0 or axes not in {6, 8} or 10 + count * (21 + 17 * axes) != len(payload):
        raise ValueError("controller observation dimensions do not match the payload")
    offset = 10
    for slot in range(count):
        device = struct.unpack_from("<i", payload, offset)[0]
        offset += 4
        if device < -1:
            raise ValueError("controller observation device is invalid")
        for channel in range(axes + 1):  # Legacy Y projection followed by every axis.
            value, valid, age = struct.unpack_from("<dBd", payload, offset)
            offset += 17
            if (valid not in {0, 1} or not math.isfinite(value) or not math.isfinite(age)
                    or age < 0 or valid and (device < 0 or abs(value) > 100)):
                raise ValueError("controller observation value, validity or age is invalid")
            if valid_axes is not None and channel > 0 and valid:
                valid_axes.add((slot, channel - 1))
    return axes


def _validate_controller_timeline(content: bytes, samples: int) -> None:
    if len(content) > MAXIMUM_CONTROLLER_CATALOG_BYTES:
        raise ValueError("controller metadata is too large")
    try:
        check_json_graph(content)
        timeline = json.loads(content.decode("utf-8"), object_pairs_hook=_unique_json_object)
    except (ValueError, UnicodeDecodeError, RecursionError) as exc:
        raise ValueError("controller metadata JSON is invalid") from exc
    if (not isinstance(timeline, dict) or set(timeline) != {"version", "samples", "catalogs", "transitions"}
            or type(timeline["version"]) is not int or timeline["version"] != CONTROLLER_CATALOG_FORMAT_VERSION
            or type(timeline["samples"]) is not int or timeline["samples"] != samples
            or not isinstance(timeline["catalogs"], list) or len(timeline["catalogs"]) > 4096
            or not isinstance(timeline["transitions"], list) or not 1 <= len(timeline["transitions"]) <= samples):
        raise ValueError("controller metadata envelope is malformed")
    total = 0
    for encoded in timeline["catalogs"]:
        if not isinstance(encoded, str) or len(encoded) > MAXIMUM_CONTROLLER_BASE64_CHARS:
            raise ValueError("controller catalog encoding is too large")
        try:
            payload = base64.b64decode(encoded, validate=True)
        except (ValueError, binascii.Error) as exc:
            raise ValueError("controller catalog base64 is invalid") from exc
        total += len(payload)
        if total > 4 * 1024 * 1024:
            raise ValueError("controller catalog table is too large")
        _validate_controller_catalog(payload)
    previous = -1
    for transition in timeline["transitions"]:
        if (not isinstance(transition, list) or len(transition) != 2
                or any(type(value) is not int for value in transition)
                or not previous < transition[0] < samples
                or (previous == -1 and transition[0] != 0)
                or not -1 <= transition[1] < len(timeline["catalogs"])):
            raise ValueError("controller catalog transition is invalid")
        previous = transition[0]


def _validate_source_timeline(content: bytes, samples: int) -> None:
    # LSR1 is optional provenance, independent of the numeric CSV schema. Keep
    # raw non-finite physics values; availability/identity must still be valid.
    if not 1 <= samples <= 65536 or not 8 <= len(content) <= 8 + 65536 * 129:
        raise ValueError("source metadata exceeds its bound")
    if content[:4] != b"LSR1" or struct.unpack_from("<i", content, 4)[0] != samples:
        raise ValueError("source metadata does not match telemetry rows")
    position = 8
    for _ in range(samples):
        if position >= len(content) or content[position] not in (0, 1):
            raise ValueError("source metadata presence tag is invalid")
        present = content[position]
        position += 1
        if not present:
            continue
        if position + 128 > len(content):
            raise ValueError("source metadata is truncated")
        (version, sdk, reserved, worker, generation, connection, observed, frame, frequency,
         availability, mask, slew, reason, request, exception, *pose) = struct.unpack_from("<BbH16sqqqqqBbbBII7d", content, position)
        if (version != 1 or sdk not in (-1, 0, 1) or reserved != 0 or worker == bytes(16)
                or generation <= 0 or connection <= 0 or observed < 0 or frame < 0 or frequency <= 0
                or availability not in range(5) or reason not in range(8)):
            raise ValueError("source metadata identity or state is invalid")
        if availability == 2:
            if mask not in range(8) or slew not in (0, 1) or reason != 0 or request == 0:
                raise ValueError("available source observation is invalid")
        elif mask != -1 or slew != -1 or reason == 0:
            raise ValueError("unavailable source observation has fabricated values")
        position += 128
    if position != len(content):
        raise ValueError("source metadata contains trailing bytes")


def validate_capture(path: Path, expected_header: str, maximum_uncompressed: int, expected_schema: int = 5) -> CaptureFacts:
    try:
        return _validate_capture_archive(path, expected_header, maximum_uncompressed, expected_schema)
    except (zipfile.BadZipFile, csv.Error) as exc:
        raise ValueError("capture archive or telemetry CSV is malformed") from exc


def _validate_capture_archive(path: Path, expected_header: str, maximum_uncompressed: int, expected_schema: int) -> CaptureFacts:
    with CaptureArchive(path, maximum_uncompressed) as archive:
        infos = archive.infolist()
        names = [info.filename for info in infos]
        allowed_entries = EXPECTED_BUG_REPORT_ENTRIES | {"decisions.json", "controller-catalogs.json", "source-state.bin"}
        if len(infos) not in {2, 3, 4, 5, 6} or len(set(names)) != len(names) or not set(names) <= allowed_entries:
            raise ValueError("archive contains unexpected or duplicate entries")
        if "session.txt" not in names:
            raise ValueError("archive is missing session metadata")

        total_uncompressed = 0
        for info in infos:
            if info.is_dir() or info.flag_bits & 0x1:
                raise ValueError("directories and encrypted entries are not accepted")
            if "\\" in info.filename or "/" in info.filename or info.filename.startswith("."):
                raise ValueError("archive entry path is unsafe")
            total_uncompressed += info.file_size
            if total_uncompressed > maximum_uncompressed:
                raise ValueError("archive expands beyond the configured limit")
            if info.filename == "decisions.json" and info.file_size > MAXIMUM_DECISION_JOURNAL_BYTES:
                raise ValueError("decision journal is too large")
            if info.filename == "controller-catalogs.json" and info.file_size > MAXIMUM_CONTROLLER_CATALOG_BYTES:
                raise ValueError("controller metadata is too large")
            if info.filename == "landing-results.json" and info.file_size > MAXIMUM_RESULT_BYTES:
                raise ValueError("bug report results are too large")
            maximum_ratio = 100 if info.filename == "landing-results.json" else 25
            # Repeated decisions legitimately compress very well. Their strict
            # 4 MiB expanded bound above limits allocation independently of ratio.
            if archive.format == 1 and (info.compress_size == 0 or (
                info.filename not in {"decisions.json", "controller-catalogs.json", "source-state.bin"} and info.file_size / info.compress_size > maximum_ratio
            )):
                raise ValueError("archive compression ratio exceeds policy")

        session = _session_values(archive.read("session.txt"))
        capture_kind = session.get("capture_kind", "raw_debug")
        is_landing_report = capture_kind in {"bug_report", "automatic_landing"}
        expected_entries = (
            EXPECTED_BUG_REPORT_ENTRIES
            if is_landing_report
            else EXPECTED_RAW_ENTRIES
        )
        if is_landing_report and "decisions.json" in names:
            expected_entries = expected_entries | {"decisions.json"}
        if "controller-catalogs.json" in names:
            expected_entries = expected_entries | {"controller-catalogs.json"}
        if "source-state.bin" in names:
            expected_entries = expected_entries | {"source-state.bin"}
        if set(names) != expected_entries or len(names) != len(expected_entries):
            raise ValueError("archive entries do not match capture kind")
        if capture_kind not in CAPTURE_KINDS:
            raise ValueError("unsupported capture kind")
        if expected_schema not in {5, 6, 7, 8} or session.get("telemetry_schema") != str(expected_schema):
            raise ValueError("unsupported telemetry schema")
        if session.get("source_period") != "SIM_FRAME":
            raise ValueError("telemetry is not a SIM_FRAME capture")

        if is_landing_report:
            try:
                declared_landing_count = int(session["landing_count"])
                declared_episode_id = int(session["episode_id"])
                report_format = int(session["bug_report_format"])
                if not 0 <= declared_landing_count <= MAXIMUM_RESULT_RECORDS:
                    raise ValueError("bug report landing count exceeds its bound")
                with archive.open("landing-results.json") as result_stream:
                    results = JsonScanner(result_stream).result_envelope(declared_landing_count)
            except (KeyError, ValueError, UnicodeDecodeError, RecursionError) as exc:
                raise ValueError("bug report result metadata is invalid") from exc
            result_format = results.get("format_version")
            records = results.get("records")
            if (
                report_format != 1
                or result_format != 1
                or results.get("episode_id") != declared_episode_id
                or type(result_format) is not int
                or type(results.get("episode_id")) is not int
                or type(records) is not int
                or (not records and capture_kind == "bug_report")
                or declared_landing_count != records
            ):
                raise ValueError("bug report results are malformed")
            if "decisions.json" in names:
                _validate_decision_journal(archive.read("decisions.json"), declared_episode_id)

        expected_columns = expected_header.split(",")
        index = {name: position for position, name in enumerate(expected_columns)}
        sample_count = 0
        previous_sequence: int | None = None
        replay_samples: list[_ReplaySample] = []
        paused = False
        with archive.open("telemetry.csv", "r") as raw:
            text = io.TextIOWrapper(raw, encoding="utf-8", errors="strict", newline="")
            reader = csv.reader(text)
            try:
                header = next(reader)
            except StopIteration as exc:
                raise ValueError("telemetry.csv is empty") from exc
            if header != expected_columns:
                raise ValueError(f"telemetry.csv header is not schema v{expected_schema}")

            for row in reader:
                sample_count += 1
                maximum_samples = 65536 if is_landing_report else 30000
                if sample_count > maximum_samples:
                    raise ValueError(f"capture contains more than {maximum_samples} samples")
                if len(row) != len(expected_columns):
                    raise ValueError("telemetry row width does not match schema")
                numeric_width = sum(len(value) for column, value in zip(expected_columns, row) if column != "controller_data_v2")
                if numeric_width > 8192:
                    raise ValueError("telemetry row is too large")
                try:
                    sequence = int(row[index["sequence"]])
                except ValueError as exc:
                    raise ValueError("sequence is not an integer") from exc
                if previous_sequence is not None and sequence <= previous_sequence:
                    raise ValueError("sequence is not strictly increasing")
                previous_sequence = sequence

                for column, value in zip(expected_columns, row):
                    if column == "controller_data_v2":
                        _validate_controller_samples(value)
                        continue
                    if column in BOOLEAN_COLUMNS:
                        if value not in {"0", "1"}:
                            raise ValueError(f"{column} is not boolean")
                        continue
                    if column in STATE_INTEGER_LIMITS:
                        minimum, maximum = STATE_INTEGER_LIMITS[column]
                        try:
                            integer = int(value)
                        except ValueError as exc:
                            raise ValueError(f"{column} is not an integer") from exc
                        if not minimum <= integer <= maximum:
                            raise ValueError(f"{column} is outside the supported range")
                        continue
                    try:
                        numeric = float(value)
                    except ValueError as exc:
                        raise ValueError(f"{column} is not numeric") from exc
                    if not math.isfinite(numeric) and not (column == "analysis_time_s" and math.isnan(numeric)):
                        raise ValueError(f"{column} is not finite")

                simulator_state = int(row[index["simulator_state"]]) if expected_schema >= 7 else 0
                if simulator_state == 2:
                    paused = True
                    continue
                analysis_time = float(row[index["analysis_time_s"]]) if expected_schema >= 7 else math.nan
                replay_sample = _ReplaySample(
                        time=analysis_time if math.isfinite(analysis_time) else float(row[index["simulation_time_s"]]),
                        on_ground=row[index["on_ground"]] == "1",
                        latitude=float(row[index["latitude_deg"]]),
                        longitude=float(row[index["longitude_deg"]]),
                        altitude=float(row[index["plane_altitude_ft"]]),
                        ground_speed=float(row[index["ground_speed_kt"]]),
                        airspeed=float(row[index["airspeed_indicated_kt"]]),
                        world_y=float(row[index["velocity_world_y_fps"]]),
                        motion=row[index["motion_simulation"]] == "1" and simulator_state != 3,
                        flight_segment=int(row[index["flight_segment"]]) if expected_schema >= 7 else 0,
                )
                if paused and simulator_state == 1 and replay_samples:
                    previous = replay_samples[-1]
                    if (previous.motion and replay_sample.motion
                            and previous.flight_segment == replay_sample.flight_segment
                            and abs(previous.time - replay_sample.time) <= 1e-9):
                        if previous.on_ground == replay_sample.on_ground:
                            continue
                        replay_samples.append(replace(replay_sample, motion=False))
                replay_samples.append(replay_sample)
                paused = False

        if sample_count == 0:
            raise ValueError("capture contains no samples")
        try:
            declared_count = int(session["sample_count"])
        except (KeyError, ValueError) as exc:
            raise ValueError("session sample_count is missing or invalid") from exc
        if declared_count != sample_count:
            raise ValueError("session sample_count does not match telemetry.csv")
        if "controller-catalogs.json" in names:
            _validate_controller_timeline(archive.read("controller-catalogs.json"), sample_count)
        if "source-state.bin" in names:
            _validate_source_timeline(archive.read("source-state.bin"), sample_count)
        # Rejected automatic episodes are diagnostics, not claimed landing results.
        # Keep their raw evidence/journal available without admitting replay-like
        # telemetry as a calculated landing or a legacy/manual capture.
        diagnostic_only = capture_kind == "automatic_landing" and not records
        if _is_replay_like(replay_samples) and not diagnostic_only:
            raise ValueError("replay-like telemetry is not accepted")

        return CaptureFacts(
            sample_count=sample_count,
            uncompressed_bytes=total_uncompressed,
            capture_kind=capture_kind,
        )
