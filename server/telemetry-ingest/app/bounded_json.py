"""Validate JSON with bounded state, without constructing its object graph.

Result details are retained as immutable archive bytes; ingress needs only the
envelope and record count. Large numeric columns and provenance strings are
therefore scanned and discarded, not decoded into millions of Python objects.
"""

import io
import json
import re


_STRING_SPECIAL = re.compile(r'["\\\x00-\x1f]')
_NUMBER = re.compile(r'-?(?:0|[1-9][0-9]*)(?:\.[0-9]+)?(?:[eE][+-]?[0-9]+)?\Z')


class JsonScanner:
    def __init__(self, stream, *, maximum_values=32_000_000, maximum_containers=8_000_000):
        self.stream = io.TextIOWrapper(stream, encoding="utf-8", errors="strict")
        self.chunk = ""
        self.offset = 0
        self.values = 0
        self.containers = 0
        self.maximum_values = maximum_values
        self.maximum_containers = maximum_containers

    def _peek(self):
        if self.offset == len(self.chunk):
            self.chunk = self.stream.read(65536)
            self.offset = 0
        return self.chunk[self.offset:self.offset + 1]

    def _take(self):
        value = self._peek()
        if value:
            self.offset += 1
        return value

    def _space(self):
        while self._peek() in (" ", "\t", "\r", "\n"):
            self.offset += 1

    def _expect(self, value):
        self._space()
        if self._take() != value:
            raise ValueError("JSON punctuation is invalid")

    def _string(self, keep=False):
        self._expect('"')
        pieces = []
        size = 0
        while self._peek():
            match = _STRING_SPECIAL.search(self.chunk, self.offset)
            end = match.start() if match else len(self.chunk)
            if keep:
                size += end - self.offset
                if size > 1024:
                    raise ValueError("JSON envelope key is too large")
                pieces.append(self.chunk[self.offset:end])
            self.offset = end
            if not match:
                continue
            character = self._take()
            if character == '"':
                return json.loads('"' + "".join(pieces) + '"') if keep else None
            if character != "\\":
                raise ValueError("JSON string contains a control character")
            escape = self._take()
            if escape not in ('"', "\\", "/", "b", "f", "n", "r", "t", "u"):
                raise ValueError("JSON string escape is invalid")
            encoded = "\\" + escape
            if escape == "u":
                for _ in range(4):
                    digit = self._take()
                    if not digit or digit not in "0123456789abcdefABCDEF":
                        raise ValueError("JSON Unicode escape is invalid")
                    encoded += digit
            if keep:
                pieces.append(encoded)
                size += len(encoded)
                if size > 1024:
                    raise ValueError("JSON envelope key is too large")
        raise ValueError("JSON string is unterminated")

    def _budget(self, depth, container=False):
        self.values += 1
        self.containers += int(container)
        if depth > 64 or self.values > self.maximum_values or self.containers > self.maximum_containers:
            raise ValueError("JSON structure exceeds its bound")

    def value(self, depth=0, keep=False):
        self._space()
        character = self._peek()
        self._budget(depth, character in ("{", "["))
        if character == '"':
            return self._string(keep)
        if character in ("{", "["):
            self.offset += 1
            closing = "}" if character == "{" else "]"
            self._space()
            if self._peek() != closing:
                while True:
                    if character == "{":
                        self._string()
                        self._expect(":")
                    self.value(depth + 1)
                    self._space()
                    if self._peek() != ",":
                        break
                    self.offset += 1
            self._expect(closing)
            return None
        token = ""
        while self._peek() and self._peek() not in (" ", "\t", "\r", "\n", ",", "]", "}"):
            token += self._take()
            if len(token) > 4300:
                raise ValueError("JSON scalar exceeds its bound")
        # Keep the legacy Python JSON reader's accepted special numbers in
        # opaque result fields. Envelope integers are checked separately.
        if token not in ("true", "false", "null", "NaN", "Infinity", "-Infinity") and not _NUMBER.fullmatch(token):
            raise ValueError("JSON scalar is invalid")
        return json.loads(token) if keep else None

    def finish(self):
        self._space()
        if self._peek():
            raise ValueError("JSON contains trailing data")

    def result_envelope(self, declared_records):
        self._expect("{")
        self._budget(0, True)
        envelope = {}
        self._space()
        if self._peek() != "}":
            while True:
                key = self._string(True)
                self._expect(":")
                if key in ("format_version", "episode_id", "records"):
                    if key in envelope:
                        raise ValueError("JSON result envelope contains duplicate fields")
                    if key == "records":
                        self._expect("[")
                        self._budget(1, True)
                        count = 0
                        self._space()
                        if self._peek() != "]":
                            while True:
                                self._space()
                                if self._peek() != "{" or count >= declared_records:
                                    raise ValueError("bug report result records exceed their declared bound")
                                self.value(2)
                                count += 1
                                self._space()
                                if self._peek() != ",":
                                    break
                                self.offset += 1
                        self._expect("]")
                        envelope[key] = count
                    else:
                        envelope[key] = self.value(1, True)
                else:
                    self.value(1)
                self._space()
                if self._peek() != ",":
                    break
                self.offset += 1
        self._expect("}")
        self.finish()
        return envelope


def check_json_graph(content):
    # Auxiliary JSON is materialized by its schema validator. Cap graph growth
    # BEFORE json.loads; legitimate 8192 journal entries with 32 detail fields
    # and 65536 two-integer controller transitions fit these independent bounds.
    scanner = JsonScanner(io.BytesIO(content), maximum_values=600000, maximum_containers=100000)
    scanner.value()
    scanner.finish()
