#!/usr/bin/env python3
"""Landing Stats API v1 client for Windows; Python 3.9+, standard library only.

Enable Settings > Local API in Landing Stats before connecting. This program
only reads the API. `watch` writes to the explicitly selected local SQLite file.
See ../integration-api.md for the contract and recovery guarantees.
"""

import argparse
import ctypes
from ctypes import wintypes
import json
import os
from pathlib import Path
import sqlite3
import struct
import sys
import time
import uuid

PROTOCOL_VERSION = 1
MAX_REQUEST_BYTES = 64 * 1024
MAX_MESSAGE_BYTES = 512 * 1024
PIPE_PREFIX = "LandingStats.Api.v1."


class ApiError(Exception):
    def __init__(self, error):
        self.code = error.get("code", "UnknownError")
        self.message = error.get("message", "")
        super().__init__(f"{self.code}: {self.message}")


def win32():
    if os.name != "nt":
        raise OSError("This example connects to Windows Named Pipes; run it on Windows.")
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel.CloseHandle.argtypes = [wintypes.HANDLE]
    kernel.CloseHandle.restype = wintypes.BOOL
    return kernel


def current_user_sid():
    """Discover the current Windows user without a shell or third-party module."""
    kernel = win32()
    kernel.GetCurrentProcess.restype = wintypes.HANDLE
    kernel.LocalFree.argtypes = [ctypes.c_void_p]
    kernel.LocalFree.restype = ctypes.c_void_p
    security = ctypes.WinDLL("advapi32", use_last_error=True)
    security.OpenProcessToken.argtypes = [wintypes.HANDLE, wintypes.DWORD, ctypes.POINTER(wintypes.HANDLE)]
    security.OpenProcessToken.restype = wintypes.BOOL
    security.GetTokenInformation.argtypes = [wintypes.HANDLE, ctypes.c_int, ctypes.c_void_p, wintypes.DWORD, ctypes.POINTER(wintypes.DWORD)]
    security.GetTokenInformation.restype = wintypes.BOOL
    security.ConvertSidToStringSidW.argtypes = [ctypes.c_void_p, ctypes.POINTER(wintypes.LPWSTR)]
    security.ConvertSidToStringSidW.restype = wintypes.BOOL
    token = wintypes.HANDLE()
    if not security.OpenProcessToken(kernel.GetCurrentProcess(), 0x0008, ctypes.byref(token)):
        raise ctypes.WinError(ctypes.get_last_error())
    try:
        size = wintypes.DWORD()
        security.GetTokenInformation(token, 1, None, 0, ctypes.byref(size))  # TokenUser
        if size.value == 0:
            raise ctypes.WinError(ctypes.get_last_error())
        data = ctypes.create_string_buffer(size.value)
        if not security.GetTokenInformation(token, 1, data, size, ctypes.byref(size)):
            raise ctypes.WinError(ctypes.get_last_error())
        # TOKEN_USER starts with SID_AND_ATTRIBUTES; its first field is PSID.
        sid = ctypes.cast(data, ctypes.POINTER(ctypes.c_void_p)).contents.value
        text = wintypes.LPWSTR()
        if not security.ConvertSidToStringSidW(sid, ctypes.byref(text)):
            raise ctypes.WinError(ctypes.get_last_error())
        try:
            return text.value
        finally:
            kernel.LocalFree(ctypes.cast(text, ctypes.c_void_p))
    finally:
        kernel.CloseHandle(token)


class Overlapped(ctypes.Structure):
    _fields_ = [("Internal", ctypes.c_size_t), ("InternalHigh", ctypes.c_size_t),
                ("Offset", wintypes.DWORD), ("OffsetHigh", wintypes.DWORD), ("hEvent", wintypes.HANDLE)]


class NamedPipe:
    """Bounded overlapped I/O: connect, frame reads and writes all have deadlines."""
    def __init__(self, name, timeout=20):
        self.kernel = win32()
        self.handle = None
        self.timeout = timeout
        kernel = self.kernel
        kernel.CreateFileW.argtypes = [wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD, ctypes.c_void_p,
                                       wintypes.DWORD, wintypes.DWORD, wintypes.HANDLE]
        kernel.CreateFileW.restype = wintypes.HANDLE
        kernel.WaitNamedPipeW.argtypes = [wintypes.LPCWSTR, wintypes.DWORD]
        kernel.WaitNamedPipeW.restype = wintypes.BOOL
        kernel.CreateEventW.argtypes = [ctypes.c_void_p, wintypes.BOOL, wintypes.BOOL, wintypes.LPCWSTR]
        kernel.CreateEventW.restype = wintypes.HANDLE
        io_args = [wintypes.HANDLE, ctypes.c_void_p, wintypes.DWORD, ctypes.POINTER(wintypes.DWORD), ctypes.POINTER(Overlapped)]
        kernel.ReadFile.argtypes = io_args
        kernel.WriteFile.argtypes = io_args
        kernel.ReadFile.restype = kernel.WriteFile.restype = wintypes.BOOL
        kernel.GetOverlappedResult.argtypes = [wintypes.HANDLE, ctypes.POINTER(Overlapped), ctypes.POINTER(wintypes.DWORD), wintypes.BOOL]
        kernel.GetOverlappedResult.restype = wintypes.BOOL
        kernel.WaitForSingleObject.argtypes = [wintypes.HANDLE, wintypes.DWORD]
        kernel.WaitForSingleObject.restype = wintypes.DWORD
        kernel.CancelIoEx.argtypes = [wintypes.HANDLE, ctypes.POINTER(Overlapped)]
        kernel.CancelIoEx.restype = wintypes.BOOL
        path = name if name.startswith("\\\\.\\pipe\\") else "\\\\.\\pipe\\" + name
        deadline = time.monotonic() + timeout
        while True:
            handle = kernel.CreateFileW(path, 0xC0000000, 0, None, 3, 0x40000000, None)
            if handle != ctypes.c_void_p(-1).value:
                self.handle = handle
                break
            error = ctypes.get_last_error()
            if error not in (2, 231):  # API disabled/not started, or all slots busy.
                raise ctypes.WinError(error)
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                raise TimeoutError("Landing Stats API unavailable; start the app and enable Settings > Local API.")
            kernel.WaitNamedPipeW(path, max(1, min(200, int(remaining * 1000))))
            if error == 2:
                time.sleep(min(0.05, remaining))

    def close(self):
        if self.handle is not None:
            self.kernel.CloseHandle(self.handle)
            self.handle = None

    def __enter__(self):
        return self

    def __exit__(self, *_):
        self.close()

    def _io(self, buffer, length, writing, deadline):
        event = self.kernel.CreateEventW(None, True, False, None)
        if not event:
            raise ctypes.WinError(ctypes.get_last_error())
        operation = Overlapped()
        operation.hEvent = event
        count = wintypes.DWORD()
        pending = False
        try:
            call = self.kernel.WriteFile if writing else self.kernel.ReadFile
            if not call(self.handle, buffer, length, ctypes.byref(count), ctypes.byref(operation)):
                error = ctypes.get_last_error()
                if error != 997:  # ERROR_IO_PENDING
                    raise ctypes.WinError(error)
                pending = True
                while True:
                    remaining = deadline - time.monotonic()
                    if remaining <= 0:
                        raise TimeoutError("Named-pipe I/O deadline expired.")
                    status = self.kernel.WaitForSingleObject(event, max(1, min(200, int(remaining * 1000))))
                    if status == 0:
                        break
                    if status != 258:  # WAIT_TIMEOUT
                        raise ctypes.WinError(ctypes.get_last_error())
                if not self.kernel.GetOverlappedResult(self.handle, ctypes.byref(operation), ctypes.byref(count), False):
                    raise ctypes.WinError(ctypes.get_last_error())
                pending = False
            if not count.value:
                raise EOFError("Landing Stats closed the API connection.")
            return count.value
        finally:
            if pending:
                # Keep the native buffer and OVERLAPPED alive until cancellation completes.
                self.kernel.CancelIoEx(self.handle, ctypes.byref(operation))
                self.kernel.GetOverlappedResult(self.handle, ctypes.byref(operation), ctypes.byref(count), True)
            self.kernel.CloseHandle(event)

    def read_exactly(self, length, deadline):
        chunks = bytearray()
        while len(chunks) < length:
            buffer = ctypes.create_string_buffer(length - len(chunks))
            count = self._io(buffer, len(buffer), False, deadline)
            chunks.extend(buffer.raw[:count])
        return bytes(chunks)

    def receive(self):
        deadline = time.monotonic() + self.timeout
        length, = struct.unpack("<I", self.read_exactly(4, deadline))
        if not 0 < length <= MAX_MESSAGE_BYTES:
            raise ValueError("Invalid server frame length.")
        message = json.loads(self.read_exactly(length, deadline).decode("utf-8"))
        if not isinstance(message, dict):
            raise ValueError("Expected a JSON object.")
        if message.get("error"):
            raise ApiError(message["error"])
        return message

    def send(self, message):
        payload = json.dumps(message, ensure_ascii=False, separators=(",", ":"), allow_nan=False).encode("utf-8")
        if not 0 < len(payload) <= MAX_REQUEST_BYTES:
            raise ValueError("Request exceeds the 64 KiB frame limit.")
        frame = struct.pack("<I", len(payload)) + payload
        deadline = time.monotonic() + self.timeout
        offset = 0
        while offset < len(frame):
            remaining = frame[offset:]
            buffer = ctypes.create_string_buffer(remaining)
            offset += self._io(buffer, len(remaining), True, deadline)


class Client:
    def __init__(self, pipe_name, timeout=20):
        self.pipe = NamedPipe(pipe_name, timeout)
        try:
            self.hello = self.query("hello")
        except BaseException:
            self.pipe.close()
            raise

    def __enter__(self):
        return self

    def __exit__(self, *_):
        self.pipe.close()

    def query(self, method, params=None):
        request_id = uuid.uuid4().hex
        self.pipe.send({"id": request_id, "protocolVersion": PROTOCOL_VERSION,
                        "method": method, "params": params or {}})
        message = self.pipe.receive()
        if message.get("id") != request_id or "result" not in message:
            raise ValueError("Response does not match the outstanding request.")
        return message["result"]

    def subscribe(self, cursor):
        params = {"streamId": cursor[0], "afterSequence": cursor[1]}
        request_id = uuid.uuid4().hex
        self.pipe.send({"id": request_id, "protocolVersion": PROTOCOL_VERSION,
                        "method": "events.subscribe", "params": params})
        acknowledgement = self.pipe.receive()
        if acknowledgement.get("id") != request_id or not acknowledgement.get("result", {}).get("subscribed"):
            raise ValueError("Expected a subscription acknowledgement.")
        while True:
            message = self.pipe.receive()
            if message.get("subscriptionId") != request_id:
                raise ValueError("Frame belongs to another subscription.")
            if message.get("type") == "heartbeat":
                continue  # A heartbeat is not a durable delivery acknowledgement.
            if message.get("type") != "event":
                raise ValueError("Unknown subscription frame type.")
            yield message["streamId"], message["event"]


def write_json(value):
    print(json.dumps(value, ensure_ascii=False, indent=2, allow_nan=False), flush=True)


def compact(value):
    return json.dumps(value, ensure_ascii=False, separators=(",", ":"), allow_nan=False)


class Mirror:
    """Example durable consumer; its database is owned entirely by this client."""
    def __init__(self, path):
        self.db = sqlite3.connect(path)
        self.db.execute("PRAGMA journal_mode=WAL")
        self.db.execute("PRAGMA synchronous=FULL")
        self.db.executescript("""
            CREATE TABLE IF NOT EXISTS state (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS landings
                (id TEXT PRIMARY KEY, episode_id TEXT NOT NULL, revision INTEGER NOT NULL, payload TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS events
                (stream_id TEXT NOT NULL, sequence INTEGER NOT NULL, type TEXT NOT NULL, payload TEXT NOT NULL,
                 PRIMARY KEY (stream_id, sequence));
            CREATE TABLE IF NOT EXISTS episodes
                (id TEXT PRIMARY KEY, revision INTEGER NOT NULL, payload TEXT NOT NULL);
        """)

    def __enter__(self):
        return self

    def __exit__(self, *_):
        self.db.close()

    def cursor(self):
        state = dict(self.db.execute("SELECT key,value FROM state"))
        if "streamId" not in state or "sequence" not in state:
            return None
        return state["streamId"], int(state["sequence"])

    def _cursor(self, stream, sequence):
        self.db.executemany("INSERT OR REPLACE INTO state(key,value) VALUES (?,?)",
                            [("streamId", stream), ("sequence", str(sequence))])

    def _contact(self, contact, revision, staging=False):
        table = "staging_landings" if staging else "landings"
        self.db.execute(f"""INSERT INTO {table}(id,episode_id,revision,payload) VALUES (?,?,?,?)
            ON CONFLICT(id) DO UPDATE SET episode_id=excluded.episode_id,
                revision=excluded.revision,payload=excluded.payload
            WHERE excluded.revision >= {table}.revision""",
                        (contact["id"], contact["episodeId"], revision, compact(contact)))

    def _result(self, event, staging=False):
        if event["type"] not in ("LandingResultAvailable", "LandingResultUpdated"):
            return  # Preserve the raw event/cursor even for an unknown additive event type.
        table = "staging_episodes" if staging else "episodes"
        contacts_table = "staging_landings" if staging else "landings"
        payload = event["payload"]
        episode_id = event["episodeId"]
        revision = event["revision"]
        previous = self.db.execute(f"SELECT revision FROM {table} WHERE id=?", (episode_id,)).fetchone()
        if previous and previous[0] >= revision:
            return  # At-least-once delivery must not regress a newer episode revision.
        self.db.execute(f"INSERT OR REPLACE INTO {table}(id,revision,payload) VALUES (?,?,?)",
                        (episode_id, revision, compact(payload)))
        self.db.execute(f"DELETE FROM {contacts_table} WHERE episode_id=?", (episode_id,))
        for contact in payload["contacts"]:
            self._contact(contact, revision, staging)

    def consume(self, stream, event):
        with self.db:
            current = self.cursor()
            if current is None or current[0] != stream:
                raise ApiError({"code": "ResyncRequired", "message": "The local mirror belongs to another stream."})
            sequence = event["sequence"]
            if sequence <= current[1]:
                return False
            inserted = self.db.execute("INSERT OR IGNORE INTO events(stream_id,sequence,type,payload) VALUES (?,?,?,?)",
                                       (stream, sequence, event["type"], compact(event))).rowcount
            if inserted:
                self._result(event)
            self._cursor(stream, sequence)
            # The result tables are the mirror; retaining unbounded duplicate raw
            # notifications is unnecessary for this demonstration client.
            self.db.execute("DELETE FROM events WHERE stream_id<>? OR sequence<?", (stream, max(0, sequence - 4096)))
        return bool(inserted)

    def rebuild(self, client):
        """Stage a keyset history walk, overlay retained results, then swap atomically."""
        baseline = client.query("hello")["events"]
        stream = baseline["streamId"]
        with self.db:
            self.db.executescript("""
                DROP TABLE IF EXISTS staging_landings;
                DROP TABLE IF EXISTS staging_episodes;
                CREATE TABLE staging_landings
                    (id TEXT PRIMARY KEY, episode_id TEXT NOT NULL, revision INTEGER NOT NULL, payload TEXT NOT NULL);
                CREATE TABLE staging_episodes
                    (id TEXT PRIMARY KEY, revision INTEGER NOT NULL, payload TEXT NOT NULL);
            """)
        parameters = {"limit": 50}
        while True:
            page = client.query("landings.list", parameters)
            if page["streamId"] != stream:
                raise ApiError({"code": "ResyncRequired", "message": "History changed stream during the rebuild."})
            with self.db:
                for contact in page["items"]:
                    self._contact(contact, 0, staging=True)
            if not page["hasMore"]:
                break
            cursor = page.get("nextCursor")
            if not cursor or cursor == parameters.get("cursor"):
                raise ValueError("History pagination did not advance.")
            parameters["cursor"] = cursor

        # Overlay all retained result events, including analysis committed BEFORE
        # the first history page but whose compressed history file is not ready.
        # Starting after hello.latestSequence would silently lose that result.
        retained = client.query("events.read", {"streamId": stream,
            "afterSequence": baseline["latestSequence"], "limit": 1})
        event_params = {"streamId": stream, "afterSequence": retained["oldestSequence"] - 1, "limit": 16}
        while True:
            page = client.query("events.read", event_params)
            if page["streamId"] != stream:
                raise ApiError({"code": "ResyncRequired", "message": "Event stream changed during the rebuild."})
            with self.db:
                for event in page["events"]:
                    self._result(event, staging=True)
            sequence = page["nextSequence"]
            event_params["afterSequence"] = sequence
            if not page["hasMore"]:
                break
        # Validate once more; a deletion or expired cursor must invalidate staging
        # rather than restore deleted results from an older stream.
        client.query("events.read", {"streamId": stream, "afterSequence": sequence, "limit": 1})
        with self.db:
            self.db.execute("DELETE FROM landings")
            self.db.execute("INSERT INTO landings SELECT * FROM staging_landings")
            self.db.execute("DELETE FROM episodes")
            self.db.execute("INSERT INTO episodes SELECT * FROM staging_episodes")
            self.db.execute("DELETE FROM events")
            self._cursor(stream, sequence)
            count = self.db.execute("SELECT COUNT(*) FROM landings").fetchone()[0]
        print(f"Rebuilt {count} landings; stream={stream}, sequence={sequence}", file=sys.stderr, flush=True)


def watch(name, timeout, database, max_events):
    if not database.parent.exists():
        raise ValueError("The database parent directory must already exist.")
    received = 0
    rebuild = False
    delay = 1
    with Mirror(database) as mirror:
        while True:
            try:
                with Client(name, timeout) as client:
                    cursor = mirror.cursor()
                    if rebuild or cursor is None or cursor[0] != client.hello["events"]["streamId"]:
                        mirror.rebuild(client)
                        rebuild = False
                        cursor = mirror.cursor()
                    for stream, event in client.subscribe(cursor):
                        delay = 1
                        if mirror.consume(stream, event):
                            write_json({"streamId": stream, "event": event})
                            received += 1
                            if max_events and received >= max_events:
                                return
            except ApiError as error:
                if error.code == "ResyncRequired":
                    rebuild = True
                elif error.code not in ("Timeout", "TemporarilyUnavailable"):
                    raise
                print(f"{error}; reconnecting in {delay}s", file=sys.stderr, flush=True)
            except (OSError, EOFError) as error:
                print(f"{error}; reconnecting in {delay}s", file=sys.stderr, flush=True)
            time.sleep(delay)
            delay = min(delay * 2, 10)


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--pipe", help="Override the discovered pipe name (useful for isolated test hosts).")
    parser.add_argument("--timeout", type=float, default=20, help="Connection and I/O deadline in seconds (default: 20).")
    commands = parser.add_subparsers(dest="command", required=True)
    commands.add_parser("hello", help="Print the negotiated protocol and capabilities.")
    query = commands.add_parser("query", help="Call any documented read-only method.")
    query.add_argument("method")
    query.add_argument("--params", default="{}", help="A JSON object containing method parameters.")
    subscription = commands.add_parser("watch", help="Maintain a resumable local SQLite mirror and print new result events.")
    subscription.add_argument("--db", required=True, type=Path, help="Client-owned SQLite file (parent directory must exist).")
    subscription.add_argument("--max-events", type=int, default=0, help="Exit after this many new events; 0 means keep watching.")
    args = parser.parse_args()
    if args.timeout < 16:
        parser.error("--timeout must be at least 16 seconds to allow subscription heartbeats.")
    name = args.pipe or PIPE_PREFIX + current_user_sid()
    if args.command == "watch":
        if args.max_events < 0:
            parser.error("--max-events cannot be negative.")
        watch(name, args.timeout, args.db, args.max_events)
        return
    parameters = json.loads(args.params) if args.command == "query" else None
    if parameters is not None and not isinstance(parameters, dict):
        parser.error("--params must be a JSON object.")
    with Client(name, args.timeout) as client:
        write_json(client.hello if args.command == "hello" else client.query(args.method, parameters))


if __name__ == "__main__":
    try:
        main()
    except KeyboardInterrupt:
        pass
    except (ApiError, OSError, EOFError, ValueError) as error:
        print(str(error), file=sys.stderr)
        sys.exit(1)
