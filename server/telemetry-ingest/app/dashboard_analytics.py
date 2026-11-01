"""Bounded, read-only flight analytics derived from retained recordings.

The receiver never imports this module. Missing channels stay missing; profiles
give each physical approach one vote at each coordinate, regardless of frame rate.
"""

import base64
import bisect
import csv
import io
import math
import re
import statistics
import struct

from .validation import _validate_controller_catalog, _validate_source_timeline

MAX_GAP = .5  # LandingReplayTrack.MaximumInterpolationGapSeconds.
MAX_AXIS_AGE = .25  # ReplayControlInputs.MaximumAgeSeconds.
MAX_SOURCE_AGE = 1.5  # ReplaySourceAssessor.MaximumObservationAgeSeconds.
MAX_POINTS = 700
MAX_COHORT = 250
SENTINEL = -1.7976931348623157e308

# Display polarity is explicit: nose up and descent are positive.
CHANNELS = {
    'agl': ('Height above ground', 'ft'), 'ias': ('Indicated airspeed', 'kt'),
    'gs': ('Ground speed', 'kt'), 'sink': ('Descent rate (+ down)', 'ft/min'),
    'pitch': ('Pitch (+ nose up)', '°'), 'bank': ('Bank', '°'),
    'aoa': ('Angle of attack', '°'), 'beta': ('Sideslip', '°'),
    'g': ('Vertical load', 'G'), 'latG': ('Lateral load', 'G'),
    'cmdP': ('Pitch command', '%'), 'cmdR': ('Roll command', '%'), 'cmdY': ('Yaw command', '%'),
    'stickP': ('Physical pitch input', '%'), 'stickR': ('Physical roll input', '%'), 'stickY': ('Physical yaw input', '%'),
    'elev': ('Elevator deflection', '%'), 'ail': ('Left aileron deflection', '%'), 'rud': ('Rudder deflection', '%'),
    'throttle': ('Mean throttle command', '%'), 'n1': ('Mean engine N1', '%'),
    'flap': ('Flaps', '%'), 'brake': ('Mean brake command', '%'),
    'crosswind': ('Crosswind (+ from right)', 'kt'), 'crab': ('Heading relative to runway', '°'),
    'along': ('Distance past landing threshold', 'm'), 'cross': ('Right of runway centreline', 'm'),
}
RAW = {
    'agl': 'agl_ft', 'ias': 'airspeed_indicated_kt', 'gs': 'ground_speed_kt',
    'sink': 'velocity_world_y_fps', 'pitch': 'pitch_deg', 'bank': 'bank_deg',
    'aoa': 'angle_of_attack_deg', 'beta': 'sideslip_deg', 'g': 'g_force',
    'cmdP': 'pilot_pitch_input_percent', 'cmdR': 'pilot_roll_input_percent', 'cmdY': 'rudder_pedal_input_percent',
    'elev': 'elevator_deflection_percent_over_100', 'ail': 'aileron_left_deflection_percent_over_100',
    'rud': 'rudder_deflection_percent_over_100', 'flap': 'flaps_left_percent',
    'lat': 'latitude_deg', 'lon': 'longitude_deg', 'heading': 'heading_true_deg',
    'wind': 'ambient_wind_velocity_kt', 'wind_dir': 'ambient_wind_direction_deg',
}
SERIES = {key: key for key in ('agl', 'gs', 'pitch', 'bank', 'aoa', 'beta', 'g', 'latG', 'cmdP', 'cmdR', 'cmdY', 'flap')}
SERIES.update(sink='iv', elev='defElev', ail='defAilL', rud='defRud', wind='windV', wind_dir='windD')


def number(value):
    if isinstance(value, bool) or value is None:
        return None
    try:
        result = float(value)
        return result if math.isfinite(result) and result != SENTINEL else None
    except (ValueError, TypeError, OverflowError):
        return None


def at(columns, key, index):
    values = columns.get(key, [])
    return values[index] if isinstance(values, list) and index < len(values) else None


def mean(values):
    values = [v for v in values if v is not None]
    return statistics.fmean(values) if values else None


def angle(value):
    return (value + 180) % 360 - 180


def runway_frame(summary):
    geometry = summary.get('RunwayGeometry') or {}
    runway = str(summary.get('Runway') or '').strip().upper()
    primary, secondary = str(geometry.get('PrimaryName') or '').upper(), str(geometry.get('SecondaryName') or '').upper()
    fields = {key: number(geometry.get(key)) for key in ('LatitudeDegrees', 'LongitudeDegrees', 'HeadingTrueDegrees', 'LengthMeters', 'WidthMeters')}
    if any(v is None for v in fields.values()) or runway not in (primary, secondary) or not runway:
        return None
    if not (-90 <= fields['LatitudeDegrees'] <= 90 and -180 <= fields['LongitudeDegrees'] <= 180 and
            0 < fields['LengthMeters'] <= 20000 and 0 < fields['WidthMeters'] <= 500):
        return None
    reverse = runway == secondary
    offset = number(geometry.get('SecondaryThresholdOffsetMeters' if reverse else 'PrimaryThresholdOffsetMeters'))
    # Unknown displaced thresholds cannot be silently treated as zero.
    if offset is None or not 0 <= offset < fields['LengthMeters']:
        return None
    return {'lat': fields['LatitudeDegrees'], 'lon': fields['LongitudeDegrees'],
            'heading': (fields['HeadingTrueDegrees'] + (180 if reverse else 0)) % 360,
            'length': fields['LengthMeters'], 'width': fields['WidthMeters'], 'offset': offset}


def project(lat, lon, frame):
    if lat is None or lon is None or frame is None or not -90 <= lat <= 90 or not -180 <= lon <= 180:
        return None, None
    east = math.radians(angle(lon - frame['lon'])) * 6371008.8 * math.cos(math.radians(frame['lat']))
    north = math.radians(lat - frame['lat']) * 6371008.8
    heading = math.radians(frame['heading'])
    return (north * math.cos(heading) + east * math.sin(heading) + frame['length'] / 2 - frame['offset'],
            east * math.cos(heading) - north * math.sin(heading))


def catalog_bindings(encoded):
    """Read only assignments/device labels; never expose profile XML or paths."""
    if not encoded:
        return {}, {}
    payload = base64.b64decode(encoded, validate=True)
    _validate_controller_catalog(payload)
    magic = struct.unpack_from('<I', payload)[0]
    offset = 72 if magic in (0x3143434C, 0x3243434C) else 146
    count = struct.unpack_from('<i', payload, offset - 4)[0]
    extended = count < 0
    count = -count - 1 if extended else count

    def text():
        nonlocal offset
        size = struct.unpack_from('<i', payload, offset)[0]
        offset += 4
        value = payload[offset:offset + size].decode('utf-8')
        offset += size
        return value

    devices, labels = {}, {}
    for _ in range(count):
        slot, _ = struct.unpack_from('<ii', payload, offset)
        offset += 8
        name = text(); text()
        mask = payload[offset]; offset += 1
        axes, identity = 6, None
        if extended:
            axes = struct.unpack_from('<i', payload, offset)[0]; offset += 4
            identity = payload[offset:offset + 16]; offset += 32
        offset += axes * 8
        if identity is not None:
            devices[identity] = (slot, axes, mask)
        labels[slot] = name
    bindings = {}
    if extended:
        text()
        count = struct.unpack_from('<i', payload, offset)[0]; offset += 4
        for _ in range(count):
            role, engine, identity, axis, inverted = struct.unpack_from('<Bi16siB', payload, offset)
            offset += 26; text()
            if role > 2:
                continue
            # Keep an assigned-but-absent role authoritative too.
            bindings.setdefault(role, set())
            device = devices.get(identity)
            if device and axis < device[1] and device[2] & (1 << axis):
                bindings[role].add((device[0] * device[1] + axis, -1 if inverted else 1))
    return {role: next(iter(values)) if len(values) == 1 else None for role, values in bindings.items()}, labels


def physical_series(record, entries):
    summary = record.get('summary', {})
    series, inputs = record.get('series', {}), record.get('inputs') or {}
    mapping = summary.get('RawControllerAxesSourceIndices') or []
    timeline = record.get('controller_catalogs') or {}
    catalogs = timeline.get('catalogs', [])
    transitions = timeline.get('transitions', [[0, -1]])
    if timeline and (timeline.get('version') != 1 or timeline.get('samples') != len(series.get('t', []))):
        return {}, []
    decoded = [catalog_bindings(value) for value in catalogs]
    selected = {}
    contact = summary.get('ContactNumber', 1)
    for entry in entries:
        d = entry.get('details') or {}
        if entry.get('component') != 'replay.inputs' or entry.get('code') != 'axis_selected' or d.get('contact_number', contact) != contact:
            continue
        role, source, correlation = d.get('role'), d.get('source_index'), number(d.get('correlation'))
        if role in (0, 1, 2) and source is not None and source >= 0 and correlation:
            selected[(int(d.get('catalog_index', -1)), int(role))] = (int(source), 1 if correlation > 0 else -1)
    result = {key: [] for key in ('stickP', 'stickR', 'stickY')}
    labels, transition = set(), 0
    legacy_mapping = summary.get('RawControllerSourceIndices') or list(range(8))
    for i in range(len(series.get('t', []))):
        while transition + 1 < len(transitions) and transitions[transition + 1][0] <= i:
            transition += 1
        context = transitions[transition][1] if transitions else -1
        direct, devices = decoded[context] if 0 <= context < len(decoded) else ({}, {})
        for role, key in enumerate(result):
            binding = direct.get(role) if role in direct else selected.get((context, role))
            value = None
            if binding:
                source, polarity = binding
                column = mapping.index(source) if source in mapping else -1
                arrays = inputs
                if not mapping and source % 6 == 1 and source // 6 in legacy_mapping:
                    column = legacy_mapping.index(source // 6)
                    arrays = {'value': series.get('rawY', []), 'valid': series.get('rawYOk', []), 'age': series.get('rawYAge', [])}
                values, valid, ages = (at(arrays, name, i) or [] for name in ('value', 'valid', 'age'))
                if 0 <= column < min(len(values), len(valid), len(ages)):
                    raw, age = number(values[column]), number(ages[column])
                    if valid[column] is True and raw is not None and abs(raw) <= 100.001 and age is not None and 0 <= age <= MAX_AXIS_AGE:
                        value = raw * polarity
                        slot = source // (summary.get('ControllerAxisCount') or 6)
                        label = devices.get(slot)
                        if not label:
                            names = summary.get('ControlInputSources') or []
                            label = names[slot] if slot < len(names) else None
                        if label:
                            labels.add(label)
            result[key].append(value)
    return result, sorted(labels)


def source_states(archive):
    """Tri-state provenance, aligned to original CSV rows (including duplicates)."""
    if 'source-state.bin' not in {entry.filename for entry in archive.infolist()}:
        return None
    content = archive.read('source-state.bin')
    if len(content) < 8:
        raise ValueError('source metadata is truncated')
    count = struct.unpack_from('<i', content, 4)[0]
    _validate_source_timeline(content, count)
    states, offset = [], 8
    for _ in range(count):
        present = content[offset]; offset += 1
        state = None
        if present:
            (_, sdk, _, _, _, _, observed, frame, frequency, availability, mask, slew,
             _, _, _, *pose) = struct.unpack_from('<BbH16sqqqqqBbbBII7d', content, offset)
            offset += 128
            if sdk == 1 and availability != 4:  # Gap has no authoritative observation.
                state = False
            elif availability == 2 and 0 <= (frame - observed) / frequency <= MAX_SOURCE_AGE:
                if mask or slew:
                    state = False
                elif all(math.isfinite(value) for value in pose[:6]):
                    state = True
        states.append(state)
    return states


def contact_intervals(contact, points):
    """Observed gear state intervals; do not extend through physics/data gaps."""
    times = [p['t'] for p in points]
    intervals = []
    for i in range(len(contact.get('t', [])) - 1):
        start, end = number(at(contact, 't', i)), number(at(contact, 't', i + 1))
        ground = at(contact, 'g', i)
        if start is None or end is None or not 0 < end - start <= MAX_GAP or not isinstance(ground, bool):
            continue
        left, right = bisect.bisect_right(times, start) - 1, bisect.bisect_left(times, end)
        if left < 0 or right >= len(points) or points[left]['run'] != points[right]['run']:
            continue
        if intervals and intervals[-1]['end'] == start and intervals[-1]['ground'] == ground:
            intervals[-1]['end'] = end
        else:
            intervals.append({'start': start, 'end': end, 'ground': ground})
    return {'gear': contact.get('n'), 'intervals': intervals}


def read_raw(archive):
    """Only keep the numeric channels needed by analytics, not raw device blobs."""
    points, states = [], source_states(archive)
    row_count = 0
    with archive.open('telemetry.csv') as stream:
        reader = csv.DictReader(io.TextIOWrapper(stream, encoding='utf-8-sig', newline=''))
        for row_index, row in enumerate(reader):
            row_count += 1
            source = states[row_index] if states is not None and row_index < len(states) else None
            t = number(row.get('analysis_time_s'))
            if t is None:
                t = number(row.get('simulation_time_s'))
            if t is None:
                continue
            p = {key: number(row.get(column)) for key, column in RAW.items()}
            p.update(t=t, ground=number(row.get('on_ground')), segment=number(row.get('flight_segment')),
                     source_checked=source is not None,
                     valid=source is not False and number(row.get('motion_simulation')) != 0 and number(row.get('simulator_state')) not in (2, 3)
                     and (number(row.get('pause_flags')) or 0) <= 0)
            for key, factor in (('sink', -60), ('pitch', -1), ('elev', 100), ('ail', 100), ('rud', 100)):
                if p[key] is not None:
                    p[key] *= factor
            engines = int(number(row.get('number_of_engines')) or 0)
            for key, suffix in (('throttle', 'throttle_percent'), ('n1', 'n1_percent')):
                p[key] = mean(number(row.get(f'engine_{i}_{suffix}')) for i in range(1, min(4, engines) + 1))
            p['brake'] = mean(number(row.get(f'brake_{side}_position')) for side in ('left', 'right'))
            lateral = number(row.get('acceleration_body_x_fps2'))
            p['latG'] = lateral / 32.17404856 if lateral is not None else None
            if points and t == points[-1]['t']:
                # Never turn a confirmed paused duplicate into valid physics.
                p['valid'] = p['valid'] and points[-1]['valid']
                points[-1] = p
            else:
                points.append(p)
    if states is not None and row_count != len(states):
        raise ValueError('source metadata does not match telemetry rows')
    return points


def interpolate(points, coordinate, grid, channels, descending=False):
    """Adjacent, continuous observations only; one last crossing per approach."""
    output = {key: [None] * len(grid) for key in channels}
    for a, b in zip(points, points[1:]):
        x, y = a.get(coordinate), b.get(coordinate)
        if x is None or y is None or x == y or b['t'] <= a['t'] or b['t'] - a['t'] > MAX_GAP or a.get('run') != b.get('run'):
            continue
        if descending and (y >= x or a['t'] > 0 or b['t'] > 0 or a.get('ground') == 1 or b.get('ground') == 1):
            continue
        low, high = min(x, y), max(x, y)
        for index in range(bisect.bisect_left(grid, low), bisect.bisect_right(grid, high)):
            fraction = (grid[index] - x) / (y - x)
            for key in channels:
                if key.startswith('stick') and a.get('input_run') != b.get('input_run'):
                    continue
                first, last = a.get(key), b.get(key)
                if first is not None and last is not None:
                    value = first + (last - first) * fraction
                    output[key][index] = round(value, 5) if math.isfinite(value) else None
    return output


def sample_indices(points):
    if len(points) <= MAX_POINTS:
        return list(range(len(points)))
    # Keep endpoints and vertical-load extrema within every display bucket.
    step = math.ceil(len(points) / (MAX_POINTS // 4))
    selected = set()
    for start in range(0, len(points), step):
        indices = list(range(start, min(len(points), start + step)))
        selected.update((indices[0], indices[-1]))
        valid = [i for i in indices if points[i].get('g') is not None]
        if valid:
            selected.update((min(valid, key=lambda i: points[i]['g']), max(valid, key=lambda i: points[i]['g'])))
    return sorted(selected)


def build_analytics(archive, results, entries):
    records = results.get('records', [])
    if not records:
        return []
    raw = read_raw(archive)
    output = []
    for record in records:
        summary, series = record.get('summary', record), record.get('series') or {}
        if not series.get('t'):
            continue
        contact_time = number(summary.get('EstimatedContactTimeSeconds'))
        frame = runway_frame(summary)
        physical, devices = physical_series(record, entries)
        stored = []
        transitions = (record.get('controller_catalogs') or {}).get('transitions', [[0, -1]])
        input_run = 0
        for i, timestamp in enumerate(series['t']):
            t = number(timestamp)
            if t is None:
                continue
            p = {key: number(at(series, column, i)) for key, column in SERIES.items()}
            p.update({key: number(at(record.get('pose') or {}, column, i)) for key, column in
                      (('lat', 'lat'), ('lon', 'lon'), ('heading', 'headingTrueDeg'))})
            if p['pitch'] is not None:
                p['pitch'] *= -1
            for key in ('elev', 'ail', 'rud'):
                if p[key] is not None:
                    p[key] *= 100
            while input_run + 1 < len(transitions) and transitions[input_run + 1][0] <= i:
                input_run += 1
            p.update(t=t, input_run=input_run, ground=1 if at(series, 'ground', i) is True else 0,
                     **{key: values[i] for key, values in physical.items()})
            stored.append(p)
        if len(stored) < 2 or any(b['t'] < a['t'] for a, b in zip(stored, stored[1:])):
            continue
        # Saved frames supply physical bindings and contact detail. Raw frames
        # extend the approach and carry authoritative pause/segment evidence.
        quality = 'legacy-state-unavailable'
        points = stored
        if contact_time is not None and raw:
            candidates = [i for i, p in enumerate(raw) if abs(p['t'] - contact_time) <= MAX_GAP]
            if not candidates:
                continue
            nearest = min(candidates, key=lambda i: abs(raw[i]['t'] - contact_time))
            if not raw[nearest]['valid']:
                continue
            start = nearest
            while start > 0 and raw[start - 1]['valid'] and raw[start - 1]['segment'] == raw[nearest]['segment'] and 0 < raw[start]['t'] - raw[start - 1]['t'] <= MAX_GAP:
                if raw[start - 1].get('ground') == 1 and raw[start]['t'] < contact_time - MAX_GAP:
                    break
                start -= 1
            points, run = [], 0
            for p in raw[start:]:
                t = p['t'] - contact_time
                if p['t'] > contact_time + stored[-1]['t'] or p['segment'] != raw[nearest]['segment']:
                    break
                if not p['valid']:
                    run += 1
                    continue
                if points and (t <= points[-1]['t'] or t - points[-1]['t'] > MAX_GAP):
                    run += 1
                points.append({**p, 't': t, 'run': run})
            quality = 'recorded-physics'
            # Never infer hardware roles anew on the server.
            mapped = interpolate(stored, 't', [p['t'] for p in points], physical.keys())
            stored_times = [p['t'] for p in stored]
            for i, p in enumerate(points):
                p.update({key: values[i] for key, values in mapped.items()})
                p['input_run'] = stored[max(0, bisect.bisect_right(stored_times, p['t']) - 1)]['input_run']
        if len(points) < 2:
            continue
        run = 0
        for index, p in enumerate(points):
            if index and (p['t'] <= points[index - 1]['t'] or p['t'] - points[index - 1]['t'] > MAX_GAP):
                run += 1
            p.setdefault('run', run)
            p['along'], p['cross'] = project(p.get('lat'), p.get('lon'), frame)
            p['crosswind'] = p['wind'] * math.sin(math.radians(angle(p['wind_dir'] - frame['heading']))) if frame and p.get('wind') is not None and p.get('wind_dir') is not None else None
            p['crab'] = angle(p['heading'] - frame['heading']) if frame and p.get('heading') is not None else None
        # A first-contact approach must contain actual descending airborne data.
        airborne = [p for p in points if p['t'] < 0 and p.get('ground') == 0 and p.get('agl') is not None]
        if not airborne:
            continue
        keys = list(CHANNELS)
        for contact in record.get('contacts', []):
            key = f"gear_{contact.get('n')}"
            source = [{'t': t, key: number(at(contact, 'c', i)), 'run': 0} for i, t in enumerate(contact.get('t', [])) if number(t) is not None]
            values = interpolate(source, 't', [p['t'] for p in points], [key])[key]
            for i, p in enumerate(points):
                p[key] = values[i]
            keys.append(key)
        grids = {'agl': list(range(0, 1001, 10)), 't': [round(i / 10, 1) for i in range(-300, 151)], 'along': list(range(-5000, 2001, 50))}
        profiles = {axis: interpolate(points if axis == 't' else [p for p in points if p['t'] <= 0], axis, grid, keys, axis == 'agl') for axis, grid in grids.items()}
        lat, lon = number(summary.get('TouchdownLatitudeDegrees')), number(summary.get('TouchdownLongitudeDegrees'))
        along, cross = project(lat, lon, frame)
        # Contact metrics must be paired at t=0, not the closest available frame.
        zero = {key: values[300] for key, values in profiles['t'].items()}
        episode = summary.get('RecordingEpisodeId') or summary.get('Id')
        events = []
        for candidate in records:
            other = candidate.get('summary', candidate)
            if (other.get('RecordingEpisodeId') or other.get('Id')) != episode:
                continue
            other_time = number(other.get('EstimatedContactTimeSeconds'))
            if contact_time is not None and other_time is not None:
                events.append({'contact': other.get('ContactNumber', 1), 't': other_time - contact_time,
                               'g': number(other.get('ImpactPeakG')), 'window': number(other.get('ImpactWindowSeconds'))})
        events.sort(key=lambda event: event['t'])
        metadata = {'id': summary.get('Id'), 'episode': summary.get('RecordingEpisodeId') or summary.get('Id'),
                    'contact': summary.get('ContactNumber', 1), 'contacts': summary.get('ContactCount', 1),
                    'aircraft': summary.get('AircraftTitle'), 'airport': summary.get('Airport'), 'runway': summary.get('Runway'),
                    'simulator': summary.get('Simulator'), 'timestamp': summary.get('TimestampUtc'),
                    'lat': lat, 'lon': lon, 'along': along, 'cross': cross, 'frame': frame, 'devices': devices,
                    'fpm': number(summary.get('InertialFpm')), 'g': number(summary.get('ImpactPeakG')),
                    'weight': number(summary.get('WeightPounds')), 'flap': zero.get('flap'), 'ias': zero.get('ias'),
                    'pitch': zero.get('pitch'), 'bank': zero.get('bank'), 'crosswind': zero.get('crosswind'), 'crab': zero.get('crab'),
                    'stickY': zero.get('stickY'), 'cmdY': zero.get('cmdY'), 'latG': zero.get('latG'),
                    'impact_window': number(summary.get('ImpactWindowSeconds')), 'contact_events': events,
                    'source_checked_frames': sum(bool(p.get('source_checked')) for p in points),
                    'quality': quality, 'height_max': max(p['agl'] for p in airborne), 'frames': len(points),
                    'channels': [key for key in keys if any(p.get(key) is not None for p in points)]}
        indices = sample_indices(points)
        columns = {key: [round(points[i][key], 6) if number(points[i].get(key)) is not None else None for i in indices] for key in ['t', 'lat', 'lon', 'run', 'input_run', 'ground', *keys]}
        for key in keys:
            # Decimation may remove measurements, but cannot hide an unavailable
            # interval and thereby draw a continuous line through it.
            for j in range(1, len(indices)):
                if any(points[i].get(key) is None for i in range(indices[j - 1], indices[j])):
                    columns[key][j] = None
        output.append({'meta': metadata, 'profiles': profiles, 'series': columns,
                       'contact_states': [contact_intervals(contact, points) for contact in record.get('contacts', [])]})
    return output


def profile_display_channels(value):
    keys = list(dict.fromkeys(value.split(','))) if value else []
    if len(keys) > 8 or any(key not in CHANNELS and not re.fullmatch(r'gear_\d{1,3}', key) for key in keys):
        raise ValueError('Choose at most eight known display channels')
    return keys


def quantiles(values):
    values = sorted(values)
    if not values:
        return None
    def percentile(q):
        position = (len(values) - 1) * q
        low, high = math.floor(position), math.ceil(position)
        return round(values[low] * (1 - position + low) + values[high] * (position - low), 5)
    return [len(values), values[0], percentile(.1), percentile(.5), percentile(.9), values[-1]]
