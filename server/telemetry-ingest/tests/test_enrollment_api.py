import base64
import hashlib
import io
import json
import os
import secrets
import tempfile
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
            self.assertEqual([5, 6, 7], config["telemetry_schemas"])
            self.assertEqual(["raw_debug", "bug_report", "automatic_landing"], config["capture_kinds"])
            self.assertEqual([1], config["decision_journal_formats"])
            for schema in (5, 6, 7):
                body, headers = self._signed_capture(schema)
                response = client.post("/v1/captures", content=body, headers=headers)
                self.assertEqual(201, response.status_code, response.text)
                self.assertEqual("accepted", response.json()["status"])
                with self.store.connect() as connection:
                    capture = connection.execute("SELECT schema_version FROM captures WHERE sha256=?",
                                                 (headers["X-Capture-SHA256"],)).fetchone()
                self.assertEqual(schema, capture["schema_version"])

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
