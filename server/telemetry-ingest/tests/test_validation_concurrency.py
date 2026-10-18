import asyncio
from pathlib import Path
import threading
import time
import unittest
from unittest import mock

# This module provides the isolated service configuration before importing main.
from test_enrollment_api import service


class ValidationConcurrencyTests(unittest.TestCase):
    def test_validation_waiters_do_not_decode_concurrently(self):
        active = maximum = completed = 0
        lock = threading.Lock()
        def validate(*args):
            nonlocal active, maximum, completed
            with lock:
                active += 1
                maximum = max(maximum, active)
            try:
                time.sleep(0.01)
            finally:
                with lock:
                    active -= 1
                    completed += 1
        async def run():
            with mock.patch.object(service, "VALIDATION_GATE", asyncio.Semaphore(1)), \
                    mock.patch.object(service, "validate_capture", validate):
                await asyncio.gather(*(service.validate_incoming(Path("fixture.zip"), 7) for _ in range(12)))
        asyncio.run(run())
        self.assertEqual(12, completed)
        self.assertEqual(1, maximum)


if __name__ == "__main__":
    unittest.main()
