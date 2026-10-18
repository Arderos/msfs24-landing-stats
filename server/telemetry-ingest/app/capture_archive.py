"""Bounded logical members of immutable legacy ZIP and capture format 2.

Format 2 stores one dictionary-free Zstd frame per non-session member, plus
an LSAR0002 binary length/SHA-256 table. Original upload bytes are never rewritten.
"""

import hashlib
import io
import struct
import time
import zipfile
from dataclasses import dataclass

import zstandard

MANIFEST = "archive-format.bin"
NAMES = {"telemetry.csv", "session.txt", "landing-results.json", "decisions.json", "controller-catalogs.json", "source-state.bin"}
MAXIMUM_BYTES = 256 * 1024 * 1024
LIMITS = {"session.txt": 16384, "decisions.json": 4 * 1024 * 1024, "controller-catalogs.json": 8 * 1024 * 1024, "source-state.bin": 16 * 1024 * 1024}


def _exact(stream, size):
    value = stream.read(size)
    if len(value) != size:
        raise ValueError("truncated capture archive framing")
    return value


def _directory_bound(stream):
    stream.seek(0, 2)
    length = stream.tell()
    if length < 22:
        raise zipfile.BadZipFile("malformed capture ZIP")
    if length > MAXIMUM_BYTES:
        raise ValueError("capture input exceeds its bound")
    stream.seek(max(0, length - 65557))
    tail = stream.read(65557)
    for offset in range(len(tail) - 22, -1, -1):
        if tail[offset:offset + 4] != b"PK\x05\x06":
            continue
        disk, central_disk, count_disk, count, size, start, comment = struct.unpack_from("<HHHHIIH", tail, offset + 4)
        if offset + 22 + comment != len(tail):
            continue
        if b"PK\x05\x06" in tail[offset + 1:]:
            raise ValueError("ambiguous capture ZIP directory")
        if offset >= 20 and tail[offset - 20:offset - 16] == b"PK\x06\x07":
            raise ValueError("ZIP64 captures are unsupported")
        end = length - len(tail) + offset
        if disk or central_disk or count_disk != count or count > 7 or size > 32768 or start + size != end:
            raise ValueError("capture ZIP directory exceeds its bound")
        stream.seek(start)
        entries = {}
        for _ in range(count):
            header = _exact(stream, 46)
            flags, method = struct.unpack_from("<HH", header, 8)
            compressed, decoded, name_length, extra, comment, disk = struct.unpack_from("<IIHHHH", header, 20)
            if (header[:4] != b"PK\x01\x02" or flags & 0x41 or method not in {0, 8} or disk
                    or max(compressed, decoded) > MAXIMUM_BYTES or name_length > 256 or extra > 4096 or comment > 1024):
                raise ValueError("unsupported capture ZIP directory entry")
            name = _exact(stream, name_length).decode("utf-8")
            if name in entries:
                raise ValueError("duplicate capture member")
            entries[name] = (method, flags, compressed, decoded, struct.unpack_from("<I", header, 42)[0])
            _check_extra(_exact(stream, extra))
            _exact(stream, comment)
            if stream.tell() > end:
                raise ValueError("truncated capture ZIP directory")
        if stream.tell() != end:
            raise ValueError("capture ZIP directory count differs")
        stream.seek(0)
        return start, entries
    raise ValueError("capture ZIP directory is missing")


def _check_extra(extra):
    offset = 0
    while offset < len(extra):
        if len(extra) - offset < 4:
            raise ValueError("truncated capture ZIP extra field")
        tag, size = struct.unpack_from("<HH", extra, offset)
        if tag == 1:
            raise ValueError("ZIP64 captures are unsupported")
        offset += 4 + size
        if offset > len(extra):
            raise ValueError("truncated capture ZIP extra field")


def _zstd_boundary(stream, decoded_length):
    # Framing from the Zstandard v1.5.7 format specification; entropy decoding
    # and content verification remain the responsibility of zstandard + SHA.
    def number(count):
        return int.from_bytes(_exact(stream, count), "little")
    if number(4) != 0xFD2FB528:
        raise ValueError("capture requires a standard Zstd frame")
    descriptor = number(1)
    single = bool(descriptor & 32)
    if descriptor & 24:
        raise ValueError("unsupported Zstd frame flags")
    window = 0
    if not single:
        wd = number(1)
        basis = 1 << (10 + (wd >> 3))
        window = basis + (basis >> 3) * (wd & 7)
    if number((0, 1, 2, 4)[descriptor & 3]):
        raise ValueError("capture dictionaries are unsupported")
    size_bytes = (1 if single else 0, 2, 4, 8)[descriptor >> 6]
    size = number(size_bytes) + (256 if size_bytes == 2 else 0)
    if size_bytes and size != decoded_length:
        raise ValueError("Zstd frame length differs from capture descriptor")
    if (size if single else window) > 128 * 1024 * 1024:
        raise ValueError("Zstd window exceeds its bound")
    for _ in range(65536):
        block = number(3)
        kind, remaining = (block >> 1) & 3, block >> 3
        if kind == 3 or remaining > 131072:
            raise ValueError("invalid Zstd block")
        if kind == 1:
            remaining = 1
        while remaining:
            amount = min(65536, remaining)
            _exact(stream, amount)
            remaining -= amount
        if block & 1:
            break
    else:
        raise ValueError("excessive Zstd blocks")
    if descriptor & 4:
        number(4)
    if stream.read(1):
        raise ValueError("trailing data after Zstd capture frame")


@dataclass(frozen=True)
class Member:
    filename: str
    file_size: int
    physical: zipfile.ZipInfo
    digest: bytes | None = None
    zstd: bool = False

    @property
    def compress_size(self):
        return self.physical.compress_size

    @property
    def flag_bits(self):
        return self.physical.flag_bits

    def is_dir(self):
        return self.physical.is_dir()


class _Verified(io.RawIOBase):
    def __init__(self, source, member):
        self.source, self.member = source, member
        self.position, self.read_seconds, self.finished = 0, 0.0, False
        self.digest = hashlib.sha256()

    def readable(self):
        return True

    def readinto(self, buffer):
        if self.finished or not len(buffer):
            return 0
        start = time.monotonic()
        try:
            data = self.source.read(min(65536, len(buffer)))
        except zstandard.ZstdError as exc:
            raise ValueError("invalid Zstd capture member") from exc
        self.read_seconds += time.monotonic() - start
        if self.read_seconds > 10:
            raise TimeoutError("capture decoding exceeded its time budget")
        self.position += len(data)
        if self.position > self.member.file_size:
            raise ValueError("capture member exceeds its declared length")
        if data:
            self.digest.update(data)
            buffer[:len(data)] = data
            return len(data)
        if self.position != self.member.file_size:
            raise ValueError("capture member is truncated")
        if self.member.digest is not None and self.digest.digest() != self.member.digest:
            raise ValueError("capture member checksum differs")
        self.finished = True
        return 0

    def close(self):
        self.source.close()
        super().close()


class CaptureArchive:
    def __init__(self, path, maximum_decoded=MAXIMUM_BYTES):
        if not 1 <= maximum_decoded <= MAXIMUM_BYTES:
            raise ValueError("invalid capture expansion bound")
        self.stream = open(path, "rb")
        self.zip = None
        try:
            directory_start, directory = _directory_bound(self.stream)
            self.zip = zipfile.ZipFile(self.stream)
            physical = self.zip.infolist()
            if (self.zip.start_dir != directory_start or len(physical) != len(directory)
                    or len({entry.filename for entry in physical}) != len(physical)):
                raise ValueError("duplicate or excessive capture members")
            # Enforce the policy on the objects actually passed to open(), before
            # even the format descriptor can be decompressed.
            for entry in physical:
                if (entry.compress_type not in {zipfile.ZIP_STORED, zipfile.ZIP_DEFLATED}
                        or directory.get(entry.filename) != (entry.compress_type, entry.flag_bits,
                            entry.compress_size, entry.file_size, entry.header_offset)):
                    raise ValueError("capture ZIP parsers disagree on a directory entry")
                _check_extra(entry.extra)
                if entry.header_offset < 0 or entry.header_offset + 30 > directory_start:
                    raise ValueError("capture ZIP local header is outside the data region")
                self.stream.seek(entry.header_offset)
                header = _exact(self.stream, 30)
                if (header[:4] != b"PK\x03\x04" or struct.unpack_from("<HH", header, 6) != (entry.flag_bits, entry.compress_type)):
                    raise ValueError("capture ZIP local header differs")
                name_length, extra_length = struct.unpack_from("<HH", header, 26)
                if (name_length > 256 or extra_length > 4096 or
                        entry.header_offset + 30 + name_length + extra_length + entry.compress_size > directory_start):
                    raise ValueError("capture ZIP local header exceeds its bound")
                if _exact(self.stream, name_length).decode("utf-8") != entry.filename:
                    raise ValueError("capture ZIP local member name differs")
                _check_extra(_exact(self.stream, extra_length))
            self.format = 2 if MANIFEST in self.zip.namelist() else 1
            self.members = self._manifest() if self.format == 2 else [Member(entry.filename, entry.file_size, entry) for entry in physical]
            total = 0
            for entry in self.members:
                total += entry.file_size
                if entry.file_size > LIMITS.get(entry.filename, MAXIMUM_BYTES):
                    message = {"decisions.json": "decision journal is too large", "controller-catalogs.json": "controller metadata is too large"}
                    raise ValueError(message.get(entry.filename, "capture member is too large"))
                if entry.file_size < 0 or total > maximum_decoded:
                    raise ValueError("capture expands beyond its byte limit")
                if "/" in entry.filename or "\\" in entry.filename or entry.filename.startswith("."):
                    raise ValueError("capture member path is unsafe")
        except Exception:
            self.close()
            raise

    def _manifest(self):
        manifest = self.zip.getinfo(MANIFEST)
        if not 9 <= manifest.file_size <= 4096:
            raise ValueError("invalid capture descriptor size")
        with self.zip.open(manifest) as source:
            descriptor_bytes = source.read(4097)
        if len(descriptor_bytes) != manifest.file_size:
            raise ValueError("capture descriptor length differs")
        descriptor = io.BytesIO(descriptor_bytes)
        if _exact(descriptor, 8) != b"LSAR0002":
            raise ValueError("unsupported capture format")
        count = _exact(descriptor, 1)[0]
        if not 2 <= count <= 6 or count + 1 != len(self.zip.infolist()):
            raise ValueError("capture descriptor member count differs")
        entries, physical_names = [], {MANIFEST}
        for _ in range(count):
            name_length = _exact(descriptor, 1)[0]
            if not 1 <= name_length <= 32:
                raise ValueError("invalid capture member name")
            name = _exact(descriptor, name_length).decode("ascii")
            codec, length = struct.unpack("<Bq", _exact(descriptor, 9))
            digest = _exact(descriptor, 32)
            if name not in NAMES or name in {entry.filename for entry in entries} or codec != (0 if name == "session.txt" else 1):
                raise ValueError("invalid capture member descriptor")
            physical_name = name if codec == 0 else name + ".zst"
            if physical_name not in self.zip.namelist():
                raise ValueError("encoded capture member is missing")
            physical = self.zip.getinfo(physical_name)
            if (codec == 0 and physical.file_size != length or codec == 1 and
                    (length < 0 or physical.file_size > length + length // 100 + 1024)):
                raise ValueError("encoded capture size or method differs")
            physical_names.add(physical_name)
            entries.append(Member(name, length, physical, digest, codec == 1))
        if (descriptor.read(1) or not {"session.txt", "telemetry.csv"} <= {entry.filename for entry in entries}
                or set(self.zip.namelist()) != physical_names):
            raise ValueError("invalid capture descriptor tail or members")
        return entries

    def infolist(self):
        return self.members[:]

    def getinfo(self, name):
        return next((entry for entry in self.members if entry.filename == name), None) or self._missing(name)

    @staticmethod
    def _missing(name):
        raise KeyError(name)

    def open(self, name, mode="r"):
        if mode != "r":
            raise ValueError("capture archives are immutable")
        member = self.getinfo(name)
        if member.zstd:
            with self.zip.open(member.physical) as framed:
                _zstd_boundary(framed, member.file_size)
        source = self.zip.open(member.physical)
        if member.zstd:
            # Backend forwards this value to ZSTD_DCtx_setMaxWindowSize (bytes).
            # Framing was also checked independently above before allocation.
            source = zstandard.ZstdDecompressor(max_window_size=128 * 1024 * 1024).stream_reader(source, read_across_frames=True, closefd=True)
        return io.BufferedReader(_Verified(source, member), 65536)

    def read(self, name):
        with self.open(name) as source:
            return source.read()

    def close(self):
        if self.zip is not None:
            self.zip.close()
        self.stream.close()

    def __enter__(self):
        return self

    def __exit__(self, *_):
        self.close()
