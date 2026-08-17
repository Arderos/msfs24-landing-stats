import base64
import os
import secrets
import tempfile
import unittest
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
from app.security import canonical_enrollment  # noqa: E402
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


if __name__ == "__main__":
    unittest.main()
