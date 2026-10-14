import asyncio
import unittest
from unittest import mock

from fastapi import HTTPException
from app.ingress_control import UploadPacer, retry_after


class IngressControlTests(unittest.IsolatedAsyncioTestCase):
    async def test_admission_has_no_wait_below_limit(self):
        store = mock.Mock()
        store.rate_limit.return_value = True
        pacer = UploadPacer()
        with mock.patch("app.ingress_control.sleep") as sleep:
            self.assertEqual(0, await pacer.admit(store, "a"))
        sleep.assert_not_called()

    async def test_only_one_boundary_is_waited_if_competing_request_wins(self):
        store = mock.Mock()
        store.rate_limit.return_value = False
        pacer = UploadPacer()
        with mock.patch("app.ingress_control.sleep") as sleep:
            with self.assertRaises(HTTPException) as failure:
                await pacer.admit(store, "a")
            self.assertEqual(429, failure.exception.status_code)
            sleep.assert_awaited_once()
        self.assertEqual(set(), pacer._waiting)

    async def test_cancelled_wait_releases_capacity(self):
        store = mock.Mock()
        store.rate_limit.return_value = False
        pacer = UploadPacer(capacity=1)
        entered = asyncio.Event()
        async def block(_):
            entered.set()
            await asyncio.Future()
        with mock.patch("app.ingress_control.sleep", side_effect=block):
            task = asyncio.create_task(pacer.admit(store, "a"))
            await entered.wait()
            task.cancel()
            with self.assertRaises(asyncio.CancelledError):
                await task
        self.assertEqual(set(), pacer._waiting)

    async def test_waiter_bound_is_per_identity_and_global(self):
        pacer = UploadPacer(capacity=2)
        with pacer.waiting("a"):
            with self.assertRaises(HTTPException):
                with pacer.waiting("a"):
                    self.fail("duplicate waiter entered")
            with pacer.waiting("b"):
                with self.assertRaises(HTTPException):
                    with pacer.waiting("c"):
                        self.fail("capacity exceeded")
        self.assertEqual(set(), pacer._waiting)

    async def test_retry_after_is_positive_and_points_to_fixed_boundary(self):
        for now, expected in ((120.0, "60"), (179.99, "1"), (150.5, "30")):
            with mock.patch("app.ingress_control.time.time", return_value=now):
                self.assertEqual(expected, retry_after(60))
