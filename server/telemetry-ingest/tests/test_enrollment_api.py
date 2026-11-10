import asyncio
import base64
import concurrent.futures
import hashlib
import io
import json
import os
import secrets
import tempfile
import threading
import time
import unittest
import zipfile
from datetime import datetime, timezone
from pathlib import Path
from types import SimpleNamespace
from unittest import mock

from cryptography.hazmat.primitives import hashes
from cryptography.hazmat.primitives.asymmetric import padding, rsa
from fastapi.testclient import TestClient


MODULE_ROOT = tempfile.TemporaryDirectory()
DATA_ROOT = Path(MODULE_ROOT.name) / "data"
PEPPER_PATH = Path(MODULE_ROOT.name) / "server-pepper"
# Binary secrets may legitimately begin or end with bytes classified as
# whitespace. The service must consume all 32 bytes without text trimming.
PEPPER_PATH.write_bytes(b" " + (b"x" * 30) + b"\n")
os.environ["DATA_ROOT"] = str(DATA_ROOT)
os.environ["SERVER_PEPPER_FILE"] = str(PEPPER_PATH)

from app import main as service  # noqa: E402
from app.security import canonical_capture, canonical_enrollment  # noqa: E402
from app.store import Store  # noqa: E402
from app.dashboard import create_app as create_dashboard_app  # noqa: E402
from app.dashboard_index import DashboardIndex  # noqa: E402


class EnrollmentApiTests(unittest.TestCase):
    def setUp(self):
        self.root = tempfile.TemporaryDirectory()
        self.store = Store(Path(self.root.name))
        self.store.initialize()
        self.store_patch = mock.patch.object(service, "STORE", self.store)
        self.data_root_patch = mock.patch.object(service, "DATA_ROOT", Path(self.root.name))
        self.store_patch.start()
        self.data_root_patch.start()

    def tearDown(self):
        self.data_root_patch.stop()
        self.store_patch.stop()
        self.root.cleanup()

    @staticmethod
    def payload(install_id: str) -> dict[str, str]:
        private = rsa.generate_private_key(public_exponent=65537, key_size=2048)
        numbers = private.public_key().public_numbers()
        modulus = base64.b64encode(numbers.n.to_bytes(256, "big")).decode("ascii")
        exponent = base64.b64encode(numbers.e.to_bytes(3, "big")).decode("ascii")
        sent_at = datetime.now(timezone.utc).isoformat(timespec="milliseconds").replace("+00:00", "Z")
        nonce = base64.urlsafe_b64encode(secrets.token_bytes(16)).decode("ascii").rstrip("=")
        canonical = canonical_enrollment(install_id, sent_at, nonce, modulus, exponent)
        signature = base64.b64encode(
            private.sign(canonical, padding.PKCS1v15(), hashes.SHA256())
        ).decode("ascii")
        return {
            "install_id": install_id,
            "sent_at_utc": sent_at,
            "nonce": nonce,
            "public_modulus": modulus,
            "public_exponent": exponent,
            "signature": signature,
        }

    def test_global_enrollment_budget_is_independent_of_source_address(self):
        with mock.patch.object(service, "GLOBAL_ENROLLMENTS_PER_HOUR", 1), mock.patch.object(
            service, "MAX_INSTALLATIONS", 100
        ), TestClient(service.app) as client:
            first = client.post(
                "/v1/enroll",
                json=self.payload("a" * 32),
                headers={"cf-connecting-ip": "192.0.2.1"},
            )
            second = client.post(
                "/v1/enroll",
                json=self.payload("b" * 32),
                headers={"cf-connecting-ip": "198.51.100.2"},
            )

        self.assertEqual(201, first.status_code)
        self.assertEqual(429, second.status_code)
        self.assertIsNone(self.store.installation("b" * 32))

    def test_registry_cap_rejects_new_identity_but_accepts_idempotent_refresh(self):
        with mock.patch.object(service, "GLOBAL_ENROLLMENTS_PER_HOUR", 100), mock.patch.object(
            service, "MAX_INSTALLATIONS", 1
        ), TestClient(service.app) as client:
            payload = self.payload("a" * 32)
            first = client.post("/v1/enroll", json=payload)
            refresh = client.post("/v1/enroll", json=payload)
            blocked = client.post("/v1/enroll", json=self.payload("b" * 32))

        self.assertEqual(201, first.status_code)
        self.assertEqual(200, refresh.status_code)
        self.assertEqual(503, blocked.status_code)
        self.assertIsNone(self.store.installation("b" * 32))

    def test_new_identity_is_rejected_below_free_space_reserve(self):
        free = service.MINIMUM_FREE_BYTES - 1
        disk_usage = SimpleNamespace(total=free + 1024, used=1024, free=free)
        with mock.patch.object(service, "GLOBAL_ENROLLMENTS_PER_HOUR", 100), mock.patch.object(
            service.shutil, "disk_usage", return_value=disk_usage
        ), TestClient(service.app) as client:
            response = client.post("/v1/enroll", json=self.payload("a" * 32))

        self.assertEqual(507, response.status_code)
        self.assertIsNone(self.store.installation("a" * 32))

    def _signed_capture(self, schema: int, session_schema: int | None = None,
                        automatic: bool = False, invalid_journal: bool = False,
                        private=None, install_id=None, sample_sequence=1):
        private = private or rsa.generate_private_key(public_exponent=65537, key_size=2048)
        numbers = private.public_key().public_numbers()
        install_id = install_id or secrets.token_hex(16)
        self.store.enroll(install_id, base64.b64encode(numbers.n.to_bytes(256, "big")).decode("ascii"),
                          base64.b64encode(numbers.e.to_bytes(3, "big")).decode("ascii"))
        header = service.EXPECTED_HEADERS[schema]
        values = ["0"] * len(header.split(","))
        values[0] = str(sample_sequence)
        if schema >= 6:
            values[header.split(",").index("controller_7_raw_v_percent")] = "-63.5"
            values[header.split(",").index("controller_7_raw_v_valid")] = "1"
        if schema >= 7:
            values[header.split(",").index("analysis_time_s")] = "NaN"
            values[header.split(",").index("pause_flags")] = "-1"
        if schema >= 8:
            values[header.split(",").index("controller_data_v2")] = ""
        content = io.BytesIO()
        with zipfile.ZipFile(content, "w", compression=zipfile.ZIP_DEFLATED) as archive:
            archive.writestr("telemetry.csv", header + "\n" + ",".join(values) + "\n")
            session = f"telemetry_schema={schema if session_schema is None else session_schema}\n" \
                      "source_period=SIM_FRAME\nsample_count=1\n"
            if automatic:
                session += "capture_kind=automatic_landing\nbug_report_format=1\nepisode_id=42\nlanding_count=0\n"
                archive.writestr("landing-results.json", json.dumps({
                    "format_version": 1, "episode_id": 42, "records": [],
                }))
                archive.writestr("decisions.json", json.dumps({
                    "format_version": 1, "episode_id": 42, "truncated": False,
                    "entries": [{
                        "time_seconds": float("nan") if invalid_journal else 1.5,
                        "component": "recorder", "code": "rejected_contact",
                        "reason": "No contact qualified", "details": {},
                    }],
                }))
            archive.writestr("session.txt", session)
        body = content.getvalue()
        digest = hashlib.sha256(body).hexdigest()
        sent = datetime.now(timezone.utc).isoformat(timespec="milliseconds").replace("+00:00", "Z")
        nonce = secrets.token_urlsafe(16)
        version = "0.8.8"
        signature = private.sign(canonical_capture(install_id, sent, nonce, digest, len(body), schema, version),
                                 padding.PKCS1v15(), hashes.SHA256())
        return body, {
            "X-Install-Id": install_id, "X-Sent-At-Utc": sent, "X-Capture-Nonce": nonce,
            "X-Capture-SHA256": digest, "X-Capture-Size": str(len(body)), "X-Telemetry-Schema": str(schema),
            "X-App-Version": version, "X-Signature": base64.b64encode(signature).decode("ascii"),
            "Content-Type": "application/zip",
        }

    def test_signed_upload_accepts_old_and_new_schema(self):
        with TestClient(service.app) as client:
            config = client.get("/v1/config").json()
            self.assertEqual(5, config["telemetry_schema"])
            self.assertEqual([5, 6, 7, 8], config["telemetry_schemas"])
            self.assertEqual(["raw_debug", "bug_report", "automatic_landing"], config["capture_kinds"])
            self.assertEqual([1], config["decision_journal_formats"])
            self.assertEqual([1], config["controller_catalog_formats"])
            for schema in (5, 6, 7, 8):
                body, headers = self._signed_capture(schema)
                response = client.post("/v1/captures", content=body, headers=headers)
                self.assertEqual(201, response.status_code, response.text)
                self.assertEqual("accepted", response.json()["status"])
                with self.store.connect() as connection:
                    capture = connection.execute("SELECT schema_version FROM captures WHERE sha256=?",
                                                 (headers["X-Capture-SHA256"],)).fetchone()
                self.assertEqual(schema, capture["schema_version"])

    def test_signed_malformed_result_metadata_cannot_poison_shared_dashboard(self):
        private = rsa.generate_private_key(public_exponent=65537, key_size=2048)
        numbers = private.public_key().public_numbers()
        install_id = secrets.token_hex(16)
        modulus = base64.b64encode(numbers.n.to_bytes(256, 'big')).decode()
        exponent = base64.b64encode(numbers.e.to_bytes(3, 'big')).decode()

        def stamp():
            return datetime.now(timezone.utc).isoformat(timespec='milliseconds').replace('+00:00', 'Z')

        def sign(value):
            return base64.b64encode(private.sign(value, padding.PKCS1v15(), hashes.SHA256())).decode()

        def record(identifier, **changes):
            return {'summary': {'Id': identifier, 'AircraftTitle': 'A320', 'Airport': 'LMML', 'Runway': '27',
                                'ContactNumber': 1, 'ContactCount': 1, 'EstimatedContactTimeSeconds': 100, **changes},
                    'series': {'t': [-.2, 0, .2], 'agl': [20, 10, 10], 'ground': [False, True, True]}}

        def upload(client, records):
            header = service.EXPECTED_HEADERS[7]
            columns, rows = header.split(','), []
            for i, current in enumerate((99.8, 100, 100.2)):
                row = dict.fromkeys(columns, '0')
                row.update(sequence=str(i + 1), simulation_time_s=str(current), analysis_time_s=str(current),
                           simulator_state='1', motion_simulation='1', flight_segment='1',
                           on_ground=str(int(i > 0)), agl_ft=str(20 if i == 0 else 10),
                           latitude_deg='35', longitude_deg='14', plane_altitude_ft='100',
                           ground_speed_kt='100', airspeed_indicated_kt='100', velocity_world_y_fps='-1')
                rows.append(','.join(row[key] for key in columns))
            content = io.BytesIO()
            with zipfile.ZipFile(content, 'w', zipfile.ZIP_DEFLATED) as archive:
                archive.writestr('telemetry.csv', header + '\n' + '\n'.join(rows) + '\n')
                archive.writestr('session.txt', 'Generated by MSFS Landing Stats 0.9.9\ntelemetry_schema=7\n'
                                 'source_period=SIM_FRAME\nsample_count=3\ncapture_kind=automatic_landing\n'
                                 f'bug_report_format=1\nlanding_count={len(records)}\nepisode_id=1\naircraft_title=A320\n')
                archive.writestr('landing-results.json', json.dumps({'format_version': 1, 'episode_id': 1, 'records': records}))
            body, sent, nonce = content.getvalue(), stamp(), secrets.token_urlsafe(16)
            digest = hashlib.sha256(body).hexdigest()
            headers = {'X-Install-Id': install_id, 'X-Sent-At-Utc': sent, 'X-Capture-Nonce': nonce,
                       'X-Capture-SHA256': digest, 'X-Capture-Size': str(len(body)), 'X-Telemetry-Schema': '7',
                       'X-App-Version': '0.9.9', 'X-Signature': sign(canonical_capture(install_id, sent, nonce, digest, len(body), 7, '0.9.9'))}
            response = client.post('/v1/captures', content=body, headers=headers)
            self.assertEqual(201, response.status_code, response.text)
            self.assertEqual('accepted', response.json()['status'])
            return response.json()['capture_id'], body

        malformed = [('AircraftTitle', ['x']), ('Airport', True), ('Runway', 27), ('Id', ['x']),
                     ('RecordingEpisodeId', {'id': 'x'}), ('Simulator', ['x']), ('TimestampUtc', {}),
                     ('ContactNumber', True), ('ContactCount', [])]
        old_records = [record('old-good')] + [record('bad-' + field, **{field: value}) for field, value in malformed]
        with mock.patch.object(service, 'REGISTRATION_MODE', 'open'), TestClient(service.app) as client:
            sent, nonce = stamp(), secrets.token_urlsafe(16)
            enrolled = client.post('/v1/enroll', json={'install_id': install_id, 'sent_at_utc': sent, 'nonce': nonce,
                'public_modulus': modulus, 'public_exponent': exponent,
                'signature': sign(canonical_enrollment(install_id, sent, nonce, modulus, exponent))})
            self.assertEqual(201, enrolled.status_code, enrolled.text)
            recent_id, recent_bytes = upload(client, [record('recent-good')])
            old_id, old_bytes = upload(client, old_records)
            with self.store.connect() as connection:
                connection.execute('UPDATE captures SET received_at=? WHERE capture_id=?', (int(time.time()) - 10 * 86400, old_id))
            with tempfile.TemporaryDirectory() as cache:
                index = DashboardIndex(self.store.root, Path(cache))
                index._initialize_cache()
                index._refresh()
                dashboard = create_dashboard_app(index=index, allowed_networks=['127.0.0.0/8'], allowed_hosts=['localhost'])
                # This test owns deterministic index publications.
                with mock.patch.object(index, 'start'), TestClient(dashboard, base_url='http://localhost',
                        client=('127.0.0.1', 12345), raise_server_exceptions=False) as reader:
                    for days, total, included in ((0, 10, 9), (7, 1, 1), (30, 10, 9)):
                        with self.subTest(days=days):
                            response = reader.get(f'/api/analytics/catalog?days={days}&page_size=1')
                            self.assertEqual(200, response.status_code, response.text)
                            result = response.json()
                            self.assertEqual(total, result['total'])
                            self.assertEqual(0, result['analytics_errors'])
                            self.assertEqual(['LMML'], result['filters']['airports'])
                            self.assertTrue(all(isinstance(pair[1], str) for pair in result['filters']['runways']))
                            self.assertEqual(200, reader.get(f'/api/analytics/catalog?days={days}&page=2&page_size=1').status_code)
                            cohort = reader.get(f'/api/analytics/cohort?days={days}')
                            self.assertEqual(200, cohort.status_code, cohort.text)
                            self.assertEqual(included, cohort.json()['included'])
                            self.assertIn('recent-good', [member['id'] for member in cohort.json()['members']])
                    all_members = index.analytics_cohort()['members']
                    self.assertIn('old-good', [member['id'] for member in all_members])
                    self.assertEqual('bad-RecordingEpisodeId', next(member for member in all_members
                                     if member['id'] == 'bad-RecordingEpisodeId')['episode'])
                    for path in ('/api/summary', '/api/captures', '/health'):
                        self.assertEqual(200, reader.get(path).status_code, path)
                    for path in ('/health', '/v1/config'):
                        self.assertEqual(200, client.get(path).status_code, path)
                    upload(client, [record('later-good')])
                    index._refresh()
                    self.assertEqual(11, reader.get('/api/analytics/catalog').json()['total'])
                    self.assertEqual(2, reader.get('/api/analytics/cohort?days=7').json()['included'])
            for capture_id, original in ((recent_id, recent_bytes), (old_id, old_bytes)):
                with self.store.connect() as connection:
                    path = connection.execute('SELECT relative_path FROM captures WHERE capture_id=?', (capture_id,)).fetchone()[0]
                self.assertEqual(original, (self.store.root / path).read_bytes())

    def test_missing_receipted_archive_is_received_again_on_signed_retry(self):
        key = rsa.generate_private_key(public_exponent=65537, key_size=2048)
        with TestClient(service.app) as client:
            body, headers = self._signed_capture(7, private=key)
            response = client.post("/v1/captures", content=body, headers=headers)
            self.assertEqual(201, response.status_code, response.text)
            first_id = response.json()["capture_id"]
            with self.store.connect() as connection:
                relative = connection.execute("SELECT relative_path FROM captures WHERE capture_id=?", (first_id,)).fetchone()[0]
            (self.store.root / relative).unlink()
            headers["X-Capture-Nonce"] = secrets.token_urlsafe(16)
            headers["X-Signature"] = base64.b64encode(key.sign(canonical_capture(
                headers["X-Install-Id"], headers["X-Sent-At-Utc"], headers["X-Capture-Nonce"],
                headers["X-Capture-SHA256"], len(body), 7, headers["X-App-Version"]),
                padding.PKCS1v15(), hashes.SHA256())).decode("ascii")
            retried = client.post("/v1/captures", content=body, headers=headers)
            self.assertEqual(201, retried.status_code, retried.text)
            self.assertEqual("accepted", retried.json()["status"])
            self.assertNotEqual(first_id, retried.json()["capture_id"])
            with self.store.connect() as connection:
                relative = connection.execute("SELECT relative_path FROM captures WHERE sha256=?", (headers["X-Capture-SHA256"],)).fetchone()[0]
            self.assertEqual(body, (self.store.root / relative).read_bytes())

    def test_signed_device_labels_remain_typed_across_catalog_pages_periods_and_filters(self):
        private = rsa.generate_private_key(public_exponent=65537, key_size=2048)
        numbers = private.public_key().public_numbers()
        install_id = secrets.token_hex(16)
        modulus = base64.b64encode(numbers.n.to_bytes(256, 'big')).decode()
        exponent = base64.b64encode(numbers.e.to_bytes(3, 'big')).decode()
        valid_label = '  Pedals Δ\t'

        def stamp():
            return datetime.now(timezone.utc).isoformat(timespec='milliseconds').replace('+00:00', 'Z')

        def sign(value):
            return base64.b64encode(private.sign(value, padding.PKCS1v15(), hashes.SHA256())).decode()

        def record(identifier, names):
            return {'summary': {'Id': identifier, 'ContactNumber': 1, 'ControlInputSources': names,
                                'RawControllerAxesSourceIndices': [1, 7], 'EstimatedContactTimeSeconds': 100},
                    'series': {'t': [-.25, 0, .25], 'agl': [20, 10, 10], 'ground': [False, True, True]},
                    'inputs': {'value': [[10, 11], [20, 21], [30, 31]],
                               'valid': [[True, True]] * 3, 'age': [[.01, .01]] * 3}}

        def upload(client, records):
            header = service.EXPECTED_HEADERS[7]
            columns, rows = header.split(','), []
            for i, current in enumerate((99.75, 100, 100.25)):
                row = dict.fromkeys(columns, '0')
                row.update(sequence=str(i + 1), simulation_time_s=str(current), analysis_time_s=str(current),
                           simulator_state='1', motion_simulation='1', flight_segment='1',
                           on_ground=str(int(i > 0)), agl_ft=str(20 if i == 0 else 10))
                rows.append(','.join(row[key] for key in columns))
            entries = [{'time_seconds': 100, 'component': 'replay.inputs', 'code': 'axis_selected',
                        'reason': 'recorded binding', 'details': {'role': role, 'source_index': source,
                        'correlation': 1, 'contact_number': 1, 'catalog_index': -1}}
                       for role, source in ((0, 1), (1, 7))]
            content = io.BytesIO()
            with zipfile.ZipFile(content, 'w', zipfile.ZIP_DEFLATED) as archive:
                archive.writestr('telemetry.csv', header + '\n' + '\n'.join(rows) + '\n')
                archive.writestr('session.txt', 'telemetry_schema=7\nsource_period=SIM_FRAME\nsample_count=3\n'
                                 f'capture_kind=automatic_landing\nbug_report_format=1\nlanding_count={len(records)}\nepisode_id=1\n')
                archive.writestr('landing-results.json', json.dumps({'format_version': 1, 'episode_id': 1, 'records': records}))
                archive.writestr('decisions.json', json.dumps({'format_version': 1, 'episode_id': 1,
                                 'truncated': False, 'entries': entries}))
            body, sent, nonce = content.getvalue(), stamp(), secrets.token_urlsafe(16)
            digest = hashlib.sha256(body).hexdigest()
            headers = {'X-Install-Id': install_id, 'X-Sent-At-Utc': sent, 'X-Capture-Nonce': nonce,
                       'X-Capture-SHA256': digest, 'X-Capture-Size': str(len(body)), 'X-Telemetry-Schema': '7',
                       'X-App-Version': '0.9.9', 'X-Signature': sign(canonical_capture(install_id, sent, nonce, digest, len(body), 7, '0.9.9'))}
            response = client.post('/v1/captures', content=body, headers=headers)
            self.assertEqual(201, response.status_code, response.text)
            self.assertEqual('accepted', response.json()['status'])
            return response.json()['capture_id'], body

        with mock.patch.object(service, 'REGISTRATION_MODE', 'open'), TestClient(service.app) as client:
            sent, nonce = stamp(), secrets.token_urlsafe(16)
            enrolled = client.post('/v1/enroll', json={'install_id': install_id, 'sent_at_utc': sent, 'nonce': nonce,
                'public_modulus': modulus, 'public_exponent': exponent,
                'signature': sign(canonical_enrollment(install_id, sent, nonce, modulus, exponent))})
            self.assertEqual(201, enrolled.status_code, enrolled.text)
            originals = [upload(client, [record('recent', [valid_label])])]
            # Numeric labels alone survive the old archive builder, then break
            # the shared catalog when sorted beside another archive's strings.
            originals.append(upload(client, [record('number', [7]), record('old-valid', [valid_label])]))
            with self.store.connect() as connection:
                connection.execute('UPDATE captures SET received_at=? WHERE capture_id=?',
                                   (int(time.time()) - 10 * 86400, originals[-1][0]))
            with tempfile.TemporaryDirectory() as cache:
                index = DashboardIndex(self.store.root, Path(cache))
                index._initialize_cache()
                index._refresh()
                dashboard = create_dashboard_app(index=index, allowed_networks=['127.0.0.0/8'], allowed_hosts=['localhost'])
                with mock.patch.object(index, 'start'), TestClient(dashboard, base_url='http://localhost',
                        client=('127.0.0.1', 12345), raise_server_exceptions=False) as reader:
                    for days, total in ((0, 3), (7, 1), (30, 3)):
                        for page in (1, 2, 3):
                            with self.subTest(days=days, page=page):
                                response = reader.get('/api/analytics/catalog', params={'days': days, 'page': page, 'page_size': 1})
                                self.assertEqual(200, response.status_code, response.text)
                                self.assertEqual(total, response.json()['total'])
                                if page == 1:
                                    self.assertEqual([valid_label], response.json()['filters']['devices'])
                    malformed = [record('label-' + str(i), [value, valid_label])
                                 for i, value in enumerate((None, True, False, 7, 0, .5, [], [1], {}, {'label': 'x'}))]
                    malformed += [record('container-' + str(i), value)
                                  for i, value in enumerate((None, True, 7, .5, 'Stick', {}, {'0': 'Stick'}))]
                    originals.append(upload(client, [record('sibling-valid', ['Stick Δ', valid_label]), *malformed]))
                    index._refresh()
                    for days, total in ((0, 21), (7, 19), (30, 21)):
                        response = reader.get('/api/analytics/catalog', params={'days': days, 'page_size': 1})
                        self.assertEqual(200, response.status_code, response.text)
                        self.assertEqual(total, response.json()['total'])
                        self.assertEqual(0, response.json()['analytics_errors'])
                        self.assertEqual(sorted([valid_label, 'Stick Δ']), response.json()['filters']['devices'])
                        cohort = reader.get('/api/analytics/cohort', params={'days': days})
                        self.assertEqual(200, cohort.status_code, cohort.text)
                        self.assertEqual(total, cohort.json()['included'])
                        filtered = reader.get('/api/analytics/cohort', params={'days': days, 'device': valid_label})
                        self.assertEqual(200, filtered.status_code, filtered.text)
                        self.assertEqual(12 if days == 7 else 13, filtered.json()['included'])
                    self.assertEqual(0, reader.get('/api/analytics/cohort', params={'device': '7'}).json()['included'])
                    for identifier, expected in (('number', []), ('label-7', [valid_label]),
                                                  ('container-4', []), ('sibling-valid', sorted([valid_label, 'Stick Δ']))):
                        response = reader.get('/api/analytics/landings/' + identifier)
                        self.assertEqual(200, response.status_code, response.text)
                        self.assertEqual(expected, response.json()['meta']['devices'])
                        self.assertEqual([10, 20, 30], response.json()['series']['stickP'])
                    for path in ('/api/summary', '/api/captures', '/health'):
                        self.assertEqual(200, reader.get(path).status_code, path)
            for capture_id, original in originals:
                with self.store.connect() as connection:
                    path = connection.execute('SELECT relative_path FROM captures WHERE capture_id=?', (capture_id,)).fetchone()[0]
                self.assertEqual(original, (self.store.root / path).read_bytes())

    @staticmethod
    def _retry_capture_headers(body, headers, private):
        headers = dict(headers, **{"X-Capture-Nonce": secrets.token_urlsafe(16)})
        message = canonical_capture(
            headers["X-Install-Id"], headers["X-Sent-At-Utc"], headers["X-Capture-Nonce"],
            headers["X-Capture-SHA256"], len(body), int(headers["X-Telemetry-Schema"]), headers["X-App-Version"])
        headers["X-Signature"] = base64.b64encode(private.sign(message, padding.PKCS1v15(), hashes.SHA256())).decode("ascii")
        return headers

    def _move_uploads_to_previous_utc_day(self):
        now = int(time.time())
        with self.store.connect() as connection:
            connection.execute("UPDATE captures SET received_at=?", (now - now % 86400 - 1,))
            connection.execute("UPDATE byte_budgets SET window_start=?", (now - now % 86400 - 86400,))

    def _concurrent_uploads_at_rolling_quota(self, duplicate):
        private = rsa.generate_private_key(public_exponent=65537, key_size=2048)
        install_id = secrets.token_hex(16)
        prior = self._signed_capture(5, private=private, install_id=install_id, sample_sequence=0)
        first = self._signed_capture(5, private=private, install_id=install_id, sample_sequence=1)
        second = ((first[0], self._retry_capture_headers(*first, private)) if duplicate else
                  self._signed_capture(5, private=private, install_id=install_id, sample_sequence=2))
        limit = 2 * max(len(body) for body, _ in (prior, first, second))
        barrier = threading.Barrier(2)
        real_add = self.store.add_capture
        def both_finish_before_acceptance(*args, **kwargs):
            barrier.wait(timeout=10)
            return real_add(*args, **kwargs)
        with mock.patch("app.store.time.time", return_value=time.time()), \
                mock.patch.object(service, "DAILY_INSTALL_QUOTA_BYTES", limit), \
                mock.patch.object(service, "VALIDATION_GATE", asyncio.Semaphore(1)), TestClient(service.app) as client:
            self.assertEqual(201, client.post("/v1/captures", content=prior[0], headers=prior[1]).status_code)
            self._move_uploads_to_previous_utc_day()
            with mock.patch.object(self.store, "add_capture", both_finish_before_acceptance), \
                    concurrent.futures.ThreadPoolExecutor(max_workers=2) as workers:
                jobs = [workers.submit(client.post, "/v1/captures", content=body, headers=headers)
                        for body, headers in (first, second)]
                responses = [job.result(timeout=20) for job in jobs]
        self.assertEqual([201, 201] if duplicate else [201, 429], sorted(r.status_code for r in responses))
        if duplicate:
            self.assertEqual({"accepted", "already_received"}, {r.json()["status"] for r in responses})
            self.assertEqual(1, len({r.json()["capture_id"] for r in responses}))
        else:
            rejected = next(r for r in responses if r.status_code == 429)
            self.assertEqual("installation daily byte quota exceeded", rejected.json()["detail"])
            self.assertEqual("86400", rejected.headers["retry-after"])
        self.assertLessEqual(self.store.accepted_bytes_since(install_id, int(time.time()) - 86400), limit)
        with self.store.connect() as connection:
            rows = connection.execute("SELECT relative_path FROM captures").fetchall()
        self.assertEqual(2, len(rows))
        retained = {self.store.root / row["relative_path"] for row in rows}
        self.assertEqual(retained, set((self.store.root / "accepted").glob("*/*/*/*.zip")))
        self.assertEqual([], list((self.store.root / "incoming").iterdir()))

    def test_concurrent_uploads_recheck_rolling_quota_and_clean_rejected_file(self):
        self._concurrent_uploads_at_rolling_quota(duplicate=False)

    def test_concurrent_duplicate_uploads_share_receipt_at_rolling_quota(self):
        self._concurrent_uploads_at_rolling_quota(duplicate=True)

    def test_retained_duplicate_receipt_is_available_at_rolling_quota(self):
        private = rsa.generate_private_key(public_exponent=65537, key_size=2048)
        body, headers = self._signed_capture(5, private=private)
        with mock.patch("app.store.time.time", return_value=time.time()), \
                mock.patch.object(service, "DAILY_INSTALL_QUOTA_BYTES", len(body)), TestClient(service.app) as client:
            first = client.post("/v1/captures", content=body, headers=headers)
            self.assertEqual(201, first.status_code, first.text)
            self._move_uploads_to_previous_utc_day()
            retried = client.post("/v1/captures", content=body,
                                  headers=self._retry_capture_headers(body, headers, private))
        self.assertEqual(201, retried.status_code, retried.text)
        self.assertEqual("already_received", retried.json()["status"])
        self.assertEqual(first.json()["capture_id"], retried.json()["capture_id"])
        self.assertEqual(len(body), self.store.accepted_bytes_since(headers["X-Install-Id"], 0))

    def test_rolling_quota_rejects_before_body_after_fixed_window_resets(self):
        private = rsa.generate_private_key(public_exponent=65537, key_size=2048)
        install_id = secrets.token_hex(16)
        prior = self._signed_capture(5, private=private, install_id=install_id, sample_sequence=0)
        extra = self._signed_capture(5, private=private, install_id=install_id, sample_sequence=1)
        with mock.patch("app.store.time.time", return_value=time.time()), \
                mock.patch.object(service, "DAILY_INSTALL_QUOTA_BYTES", max(len(prior[0]), len(extra[0]))), \
                TestClient(service.app) as client:
            self.assertEqual(201, client.post('/v1/captures', content=prior[0], headers=prior[1]).status_code)
            self._move_uploads_to_previous_utc_day()
            async def unread_body(request):
                # Middleware may construct the iterator; reading its first
                # chunk is the operation this request must avoid.
                self.fail('body must not be read')
                yield b''
            with mock.patch.object(service.Request, 'stream', unread_body):
                response = client.post('/v1/captures', content=extra[0], headers=extra[1])
        self.assertEqual(429, response.status_code, response.text)
        self.assertEqual('installation daily byte quota exceeded', response.json()['detail'])
        self.assertEqual('86400', response.headers['retry-after'])
        self.assertEqual([], list((self.store.root / 'incoming').iterdir()))
        self.assertIsNone(self.store.capture_by_hash(extra[1]['X-Capture-SHA256']))

    def test_signed_automatic_report_persists_kind_and_journal(self):
        with TestClient(service.app) as client:
            body, headers = self._signed_capture(7, automatic=True)
            response = client.post("/v1/captures", content=body, headers=headers)
            self.assertEqual(201, response.status_code, response.text)
            with self.store.connect() as connection:
                capture = connection.execute("SELECT capture_kind, relative_path FROM captures WHERE sha256=?",
                                             (headers["X-Capture-SHA256"],)).fetchone()
            self.assertEqual("automatic_landing", capture["capture_kind"])
            with zipfile.ZipFile(Path(self.root.name) / capture["relative_path"]) as archive:
                journal = json.loads(archive.read("decisions.json"))
            self.assertEqual("rejected_contact", journal["entries"][0]["code"])

    def test_invalid_signed_journal_is_rejected_and_temporary_upload_removed(self):
        with TestClient(service.app) as client:
            body, headers = self._signed_capture(6, automatic=True, invalid_journal=True)
            response = client.post("/v1/captures", content=body, headers=headers)
            self.assertEqual(422, response.status_code, response.text)
            self.assertIn("decision journal entry is malformed", response.text)
            self.assertIsNone(self.store.capture_by_hash(headers["X-Capture-SHA256"]))
            self.assertEqual([], list((Path(self.root.name) / "incoming").iterdir()))
            retained = Path(self.root.name) / "rejected" / (headers["X-Capture-SHA256"] + ".zip")
            self.assertEqual(body, retained.read_bytes())
            self.assertEqual("decision journal entry is malformed",
                             json.loads(retained.with_suffix(".json").read_text())["reason"])

    def test_signed_new_schema_cannot_relabel_old_session(self):
        with TestClient(service.app) as client:
            body, headers = self._signed_capture(6, session_schema=5)
            response = client.post("/v1/captures", content=body, headers=headers)
            self.assertEqual(422, response.status_code, response.text)
            self.assertIn("unsupported telemetry schema", response.text)
            self.assertIsNone(self.store.capture_by_hash(headers["X-Capture-SHA256"]))
            self.assertEqual([], list((Path(self.root.name) / "incoming").iterdir()))

    def test_signed_refresh_does_not_spend_new_registration_budgets(self):
        payload = self.payload("a" * 32)
        now = int(time.time()) // 3600 * 3600 + 60
        with mock.patch("app.store.time.time", return_value=now) as clock, \
                mock.patch.object(service, "GLOBAL_ENROLLMENTS_PER_HOUR", 1), \
                TestClient(service.app) as client:
            self.assertEqual(201, client.post("/v1/enroll", json=payload).status_code)
            for index in range(15):
                clock.return_value = now + (index // 3) * 60
                response = client.post("/v1/enroll", json=payload)
                self.assertEqual(200, response.status_code, response.text)
            # Refresh also succeeds after the new-identity global quota is full.
            response = client.post("/v1/enroll", json=self.payload("b" * 32))
            self.assertEqual(429, response.status_code)
            self.assertGreater(int(response.headers["retry-after"]), 0)
        with self.store.connect() as db:
            self.assertEqual(1, db.execute("SELECT SUM(count) FROM rate_limits WHERE scope='enroll-global-hour'").fetchone()[0])

    def test_refresh_still_requires_signature_and_matching_active_identity(self):
        payload = self.payload("a" * 32)
        with TestClient(service.app) as client:
            self.assertEqual(201, client.post("/v1/enroll", json=payload).status_code)
            invalid = dict(payload, signature=base64.b64encode(bytes(256)).decode())
            self.assertEqual(400, client.post("/v1/enroll", json=invalid).status_code)
            self.assertEqual(400, client.post("/v1/enroll", json=self.payload("a" * 32)).status_code)
            with self.store.connect() as db:
                db.execute("UPDATE installations SET status='revoked'")
            self.assertEqual(403, client.post("/v1/enroll", json=payload).status_code)

    def test_closed_registration_refreshes_active_identity_but_not_new_or_pruned(self):
        payload = self.payload("a" * 32)
        with TestClient(service.app) as client:
            self.assertEqual(201, client.post("/v1/enroll", json=payload).status_code)
            with mock.patch.object(service, "REGISTRATION_MODE", "closed"):
                self.assertEqual(200, client.post("/v1/enroll", json=payload).status_code)
                self.assertEqual(403, client.post("/v1/enroll", json=self.payload("b" * 32)).status_code)
                with self.store.connect() as connection:
                    connection.execute("DELETE FROM installations WHERE install_id=?", ("a" * 32,))
                self.assertEqual(403, client.post("/v1/enroll", json=payload).status_code)

    def test_refresh_has_independent_pre_authentication_limit(self):
        payload = self.payload("a" * 32)
        with mock.patch("app.store.time.time", return_value=int(time.time())), TestClient(service.app) as client:
            self.assertEqual(201, client.post("/v1/enroll", json=payload).status_code)
            for _ in range(6):
                self.assertEqual(200, client.post("/v1/enroll", json=payload).status_code)
            response = client.post("/v1/enroll", json=payload)
            self.assertEqual(429, response.status_code)
            self.assertIn("refresh", response.json()["detail"])
            self.assertGreater(int(response.headers["retry-after"]), 0)

    def test_fourth_signed_upload_is_received_then_paced_not_rejected(self):
        key = rsa.generate_private_key(public_exponent=65537, key_size=2048)
        install_id = secrets.token_hex(16)
        from app.ingress_control import UploadPacer
        now = int(time.time()) // 60 * 60 + 5
        with mock.patch("app.store.time.time", return_value=now) as clock, \
                mock.patch.object(service, "UPLOAD_PACER", UploadPacer()), \
                TestClient(service.app) as client:
            async def advance(delay):
                parts = list((Path(self.root.name) / "incoming").glob("*.part"))
                self.assertEqual(1, len(parts))
                # The complete file was spooled before the wait (proxy write timeout).
                with zipfile.ZipFile(parts[0]) as archive:
                    self.assertIn("telemetry.csv", archive.namelist())
                clock.return_value += delay
            with mock.patch("app.ingress_control.sleep", side_effect=advance) as sleep:
                for sequence in range(1, 5):
                    body, headers = self._signed_capture(6, automatic=True, private=key,
                                                         install_id=install_id, sample_sequence=sequence)
                    response = client.post("/v1/captures", content=body, headers=headers)
                    self.assertEqual(201, response.status_code, response.text)
                    self.assertEqual("accepted", response.json()["status"])
                sleep.assert_awaited_once()
                self.assertLessEqual(sleep.call_args.args[0], 60.1)
            self.assertEqual([], list((Path(self.root.name) / "incoming").iterdir()))
            with self.store.connect() as db:
                self.assertEqual(4, db.execute("SELECT COUNT(*) FROM captures").fetchone()[0])

    def test_rejection_log_has_reason_and_hash_but_no_secret_or_source_address(self):
        with TestClient(service.app) as client:
            body, headers = self._signed_capture(6, automatic=True, invalid_journal=True)
            headers["cf-connecting-ip"] = "192.0.2.123"
            with self.assertLogs("uvicorn.error", level="INFO") as logs:
                response = client.post("/v1/captures", content=body, headers=headers)
            self.assertEqual(422, response.status_code)
            events = [json.loads(line.split(":", 2)[2]) for line in logs.output]
            event = next(e for e in events if e["event"] == "telemetry_ingress")
            self.assertEqual(422, event["status"])
            self.assertEqual("decision journal entry is malformed", event["reason"])
            self.assertEqual(headers["X-Capture-SHA256"], event["sha256"])
            self.assertEqual(headers["X-Install-Id"], event["install_id"])
            self.assertEqual("retained", event["quarantine"])
            for secret in (headers["X-Signature"], headers["X-Capture-Nonce"], "192.0.2.123"):
                self.assertNotIn(secret, "\n".join(logs.output))

    def test_enrollment_size_guard_and_schema_errors_are_logged_safely(self):
        with TestClient(service.app) as client, self.assertLogs("uvicorn.error", level="INFO") as logs:
            response = client.post("/v1/enroll", content=b"x" * 32769)
            self.assertEqual(413, response.status_code)
            response = client.post("/v1/enroll", json={"signature": "PRIVATE_MARKER"})
            self.assertEqual(422, response.status_code)
        self.assertIn("enrollment request exceeds policy", "\n".join(logs.output))
        self.assertIn("request fields do not match the API schema", "\n".join(logs.output))
        self.assertNotIn("PRIVATE_MARKER", "\n".join(logs.output))

    def test_disk_read_failure_is_retryable_not_permanent_quarantine(self):
        with TestClient(service.app) as client, mock.patch.object(service, "validate_capture", side_effect=OSError("private path")):
            body, headers = self._signed_capture(6)
            response = client.post("/v1/captures", content=body, headers=headers)
            self.assertEqual(503, response.status_code)
            self.assertNotIn("private path", response.text)
            self.assertEqual([], list((Path(self.root.name) / "incoming").iterdir()))

    def test_invalid_signature_and_hash_never_enter_quarantine_or_wait(self):
        with TestClient(service.app) as client, mock.patch.object(service.UPLOAD_PACER, "admit") as pacing:
            body, headers = self._signed_capture(6, automatic=True, invalid_journal=True)
            bad_signature = dict(headers, **{"X-Signature": base64.b64encode(bytes(256)).decode()})
            self.assertEqual(403, client.post("/v1/captures", content=body, headers=bad_signature).status_code)
            self.assertEqual(400, client.post("/v1/captures", content=bytes(len(body)), headers=headers).status_code)
            pacing.assert_not_awaited()
            self.assertFalse((Path(self.root.name) / "rejected").exists())
            self.assertEqual([], list((Path(self.root.name) / "incoming").iterdir()))

    def test_failed_quarantine_is_explicit_and_does_not_claim_acceptance(self):
        with TestClient(service.app) as client, \
                mock.patch.object(self.store, "quarantine_capture", side_effect=OSError("private path")), \
                self.assertLogs("uvicorn.error", level="INFO") as logs:
            body, headers = self._signed_capture(6, automatic=True, invalid_journal=True)
            response = client.post("/v1/captures", content=body, headers=headers)
            self.assertEqual(422, response.status_code)
        self.assertIn('"quarantine":"retention_failed"', "\n".join(logs.output))
        self.assertNotIn("private path", "\n".join(logs.output))
        self.assertIsNone(self.store.capture_by_hash(headers["X-Capture-SHA256"]))


if __name__ == "__main__":
    unittest.main()
