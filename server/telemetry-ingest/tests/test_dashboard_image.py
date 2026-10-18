"""Exercise app entrypoints from exactly the Dockerfile's copied Python files."""
import os
from pathlib import Path
import shlex
import shutil
import subprocess
import sys
import tempfile
import unittest


class DashboardImageTests(unittest.TestCase):
    def test_clean_copy_layout_imports_and_serves_health(self):
        source = Path(__file__).resolve().parents[1]
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            for line in (source / "Dockerfile.dashboard").read_text().splitlines():
                if not line.startswith("COPY "):
                    continue
                arguments = shlex.split(line)[1:]
                target = root / arguments[-1]
                for name in arguments[:-1]:
                    item = source / name
                    if item.is_dir():
                        shutil.copytree(item, target, dirs_exist_ok=True, ignore=shutil.ignore_patterns("__pycache__"))
                    elif len(arguments) > 2 or arguments[-1].endswith("/"):
                        target.mkdir(parents=True, exist_ok=True)
                        shutil.copyfile(item, target / item.name)
                    else:
                        target.parent.mkdir(parents=True, exist_ok=True)
                        shutil.copyfile(item, target)
            environment = dict(os.environ, DATA_ROOT=str(root / "data"), CACHE_ROOT=str(root / "cache"),
                               DASHBOARD_ALLOWED_NETWORKS="127.0.0.0/8,::1/128", DASHBOARD_ALLOWED_HOSTS="testserver")
            script = """
from fastapi.testclient import TestClient
import app.dashboard
import app.dashboard_snapshot
with TestClient(app.dashboard.app, client=("127.0.0.1", 123)) as client:
    response = client.get("/health")
    assert response.status_code == 200, response.text
"""
            result = subprocess.run([sys.executable, "-B", "-c", script], cwd=root, env=environment,
                                    text=True, capture_output=True, timeout=30)
            self.assertEqual(0, result.returncode, result.stdout + result.stderr)


if __name__ == "__main__":
    unittest.main()
