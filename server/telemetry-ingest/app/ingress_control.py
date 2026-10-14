"""Bounded server-side pacing for clients which cannot interpret Retry-After yet."""

from asyncio import sleep
import math
import threading
import time
from contextlib import contextmanager

from fastapi import HTTPException
from starlette.concurrency import run_in_threadpool


def retry_after(seconds: int) -> str:
    return str(max(1, math.ceil(seconds - time.time() % seconds)))


def rate_limited(reason: str, window_seconds: int = 60) -> HTTPException:
    return HTTPException(429, detail=reason, headers={"Retry-After": retry_after(window_seconds)})


class UploadPacer:
    # Only signed, fresh, size/hash-checked uploads may wait. The file has already
    # been spooled to quota-bounded disk, avoiding the proxy's write timeout.
    # One waiter per identity and eight in total bound connections and disk use.
    def __init__(self, capacity: int = 8):
        self.capacity = capacity
        self._lock = threading.Lock()
        self._waiting: set[str] = set()

    @contextmanager
    def waiting(self, install_id: str):
        with self._lock:
            if install_id in self._waiting or len(self._waiting) >= self.capacity:
                raise rate_limited("upload pacing queue is full")
            self._waiting.add(install_id)
        try:
            yield
        finally:
            with self._lock:
                self._waiting.remove(install_id)

    async def admit(self, store, install_id: str) -> float:
        if await run_in_threadpool(store.rate_limit, "capture-install-minute", install_id, 60, 3):
            return 0.0
        started = time.monotonic()
        with self.waiting(install_id):
            # At most one fixed-window boundary, comfortably below the shipped
            # client's 180s request timeout and Cloudflare's origin read timeout.
            await sleep(float(retry_after(60)) + 0.05)
            if not await run_in_threadpool(store.rate_limit, "capture-install-minute", install_id, 60, 3):
                raise rate_limited("installation rate limit exceeded")
        return time.monotonic() - started
