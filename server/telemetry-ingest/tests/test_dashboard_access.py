"""Independent checks for the LAN dashboard's read-only HTTP boundary."""

import unittest

from fastapi.testclient import TestClient

from app.dashboard import create_app


class FakeIndex:
    def __init__(self):
        self.started = False
        self.stopped = False
        self.calls = []

    def start(self):
        self.started = True

    def stop(self):
        self.stopped = True

    def summary(self, days=0):
        self.calls.append(("summary", days))
        return {"test_archive_count": 3}

    def list_captures(self, days=0, q="", airport="", aircraft="", version="", kind="",
                      outcome="", page=1, page_size=50):
        self.calls.append(("list", days, q, airport, aircraft, version, kind, page, page_size, outcome))
        return {"items": [], "total": 0, "page": page, "page_size": page_size}

    def capture(self, capture_id):
        self.calls.append(("capture", capture_id))
        if capture_id == "a" * 32:
            return {"capture_id": capture_id}
        return None


class PeerAddress:
    """Set the real ASGI peer independently of attacker-controlled headers."""

    def __init__(self, app, address):
        self.app = app
        self.address = address

    async def __call__(self, scope, receive, send):
        if scope["type"] == "http":
            scope = dict(scope, client=(self.address, 41234))
        await self.app(scope, receive, send)


class DashboardAccessTests(unittest.TestCase):
    def setUp(self):
        self.index = FakeIndex()

    def client(self, peer="172.16.3.12", *, hosts=None, networks=None):
        application = create_app(
            index=self.index,
            allowed_networks=networks or ["172.16.0.0/16", "127.0.0.0/8", "::1/128"],
            allowed_hosts=hosts or ["astr-scad-01", "172.16.3.10", "localhost"],
        )
        return TestClient(PeerAddress(application, peer), base_url="http://astr-scad-01:8081")

    def test_lan_can_read_and_index_lifecycle_is_owned_by_application(self):
        with self.client() as client:
            self.assertTrue(self.index.started)
            response = client.get("/api/summary")
            self.assertEqual(200, response.status_code, response.text)
            self.assertEqual(3, response.json()["test_archive_count"])
        self.assertTrue(self.index.stopped)

    def test_only_configured_lan_and_loopback_sources_are_accepted(self):
        for peer in ("127.0.0.1", "::1", "172.16.255.254"):
            with self.subTest(peer=peer), self.client(peer) as client:
                self.assertEqual(200, client.get("/api/summary").status_code)
        for peer in ("8.8.8.8", "172.17.0.1", "192.168.1.2", "2001:4860:4860::8888",
                     "fd00::42", "unknown"):
            with self.subTest(peer=peer), self.client(peer) as client:
                self.assertEqual(403, client.get("/api/summary").status_code)

    def test_forwarded_headers_cannot_make_a_public_peer_local(self):
        headers = {
            "X-Forwarded-For": "172.16.3.12",
            "X-Real-IP": "127.0.0.1",
            "CF-Connecting-IP": "127.0.0.1",
            "Forwarded": 'for="172.16.3.12";host="astr-scad-01"',
        }
        with self.client("8.8.8.8") as client:
            response = client.get("/api/summary", headers=headers)
        self.assertEqual(403, response.status_code)
        self.assertEqual([], self.index.calls)

    def test_host_guard_rejects_rebinding_even_from_a_lan_peer(self):
        for host in ("attacker.example", "astr-scad-01.attacker.example", "172.16.3.10.attacker.example"):
            with self.subTest(host=host), self.client() as client:
                response = client.get("/api/summary", headers={"Host": host})
                self.assertEqual(400, response.status_code, response.text)
        self.assertEqual([], self.index.calls)

    def test_cross_site_fetch_is_blocked_without_cors_permission(self):
        with self.client() as client:
            response = client.get("/api/summary", headers={
                "Sec-Fetch-Site": "cross-site", "Origin": "https://attacker.example",
            })
        self.assertEqual(403, response.status_code)
        self.assertNotIn("access-control-allow-origin", response.headers)
        self.assertEqual([], self.index.calls)

    def test_write_methods_never_reach_index(self):
        with self.client() as client:
            for method in ("POST", "PUT", "PATCH", "DELETE", "TRACE"):
                with self.subTest(method=method):
                    response = client.request(method, "/api/captures", json={"delete": True})
                    self.assertEqual(405, response.status_code)
        self.assertEqual([], self.index.calls)

    def test_summary_is_not_cacheable_and_cannot_be_framed_or_use_inline_scripts(self):
        with self.client() as client:
            response = client.get("/api/summary")
        self.assertEqual(200, response.status_code)
        self.assertIn("no-store", response.headers.get("cache-control", ""))
        self.assertEqual("nosniff", response.headers.get("x-content-type-options"))
        csp = response.headers.get("content-security-policy", "")
        self.assertIn("frame-ancestors 'none'", csp)
        self.assertNotIn("'unsafe-inline'", csp)
        self.assertNotIn("'unsafe-eval'", csp)

    def test_invalid_query_is_rejected_before_reading_index(self):
        with self.client() as client:
            for path in ("/api/summary?days=-1", "/api/summary?days=not-a-number",
                         "/api/captures?page=0", "/api/captures?page_size=0",
                         "/api/captures?page_size=1000000"):
                with self.subTest(path=path):
                    self.assertIn(client.get(path).status_code, (400, 422))
        self.assertEqual([], self.index.calls)

    def test_outcome_filter_reaches_the_read_only_index(self):
        with self.client() as client:
            response = client.get("/api/captures?outcome=invalid_contacts&airport=Unknown")
        self.assertEqual(200, response.status_code)
        self.assertEqual("invalid_contacts", self.index.calls[-1][-1])
        self.assertEqual("Unknown", self.index.calls[-1][3])

    def test_detail_identifier_is_validated_and_unknown_ids_are_not_found(self):
        with self.client() as client:
            self.assertEqual(200, client.get("/api/captures/" + "a" * 32).status_code)
            self.assertEqual(404, client.get("/api/captures/" + "b" * 32).status_code)
            for value in ("not-a-capture", "A" * 32, "a" * 33):
                with self.subTest(value=value):
                    self.assertIn(client.get("/api/captures/" + value).status_code, (400, 404, 422))
        self.assertEqual([("capture", "a" * 32), ("capture", "b" * 32)], self.index.calls)

    def test_no_public_schema_or_receiver_upload_endpoints(self):
        with self.client() as client:
            for path in ("/docs", "/redoc", "/openapi.json", "/v1/config", "/v1/captures"):
                with self.subTest(path=path):
                    self.assertEqual(404, client.get(path).status_code)

    def test_dashboard_static_interface_is_english(self):
        with self.client() as client:
            for path in ("/", "/static/app.js", "/static/app.css"):
                with self.subTest(path=path):
                    response = client.get(path)
                    self.assertEqual(200, response.status_code)
                    self.assertNotRegex(response.text, r"[\u0400-\u04ff]")
                    self.assertNotIn("ru-RU", response.text)
            self.assertIn('<html lang="en">', client.get("/").text)
            self.assertIn('"en-GB"', client.get("/static/app.js").text)


if __name__ == "__main__":
    unittest.main()
