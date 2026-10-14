import asyncio
import hashlib
import json
import logging
import os
import re
import secrets
import shutil
import time
from contextlib import asynccontextmanager
from datetime import datetime, timezone
from pathlib import Path

from fastapi import FastAPI, HTTPException, Request, Response, status
from fastapi.exception_handlers import http_exception_handler, request_validation_exception_handler
from fastapi.exceptions import RequestValidationError
from starlette.exceptions import HTTPException as StarletteHTTPException
from starlette.requests import ClientDisconnect
from starlette.concurrency import run_in_threadpool
from pydantic import BaseModel, Field

from .security import (
    INSTALL_ID_RE,
    NONCE_RE,
    VERSION_RE,
    address_key,
    canonical_capture,
    canonical_enrollment,
    parse_utc,
    public_key,
    verify_signature,
)
from .store import InstallationRegistryFullError, Store
from .ingress_control import UploadPacer, rate_limited
from .validation import CAPTURE_KINDS, DECISION_JOURNAL_FORMAT_VERSION, validate_capture


DATA_ROOT = Path(os.environ.get("DATA_ROOT", "/data"))
REGISTRATION_MODE = "open"
MAX_UPLOAD_BYTES = int(os.environ.get("MAX_UPLOAD_BYTES", str(16 * 1024 * 1024)))
MAX_UNCOMPRESSED_BYTES = int(os.environ.get("MAX_UNCOMPRESSED_BYTES", str(64 * 1024 * 1024)))
STORAGE_QUOTA_BYTES = int(os.environ.get("STORAGE_QUOTA_BYTES", str(20 * 1024 * 1024 * 1024)))
DAILY_INSTALL_QUOTA_BYTES = int(os.environ.get("DAILY_INSTALL_QUOTA_BYTES", str(512 * 1024 * 1024)))
DAILY_SOURCE_ATTEMPT_QUOTA_BYTES = int(
    os.environ.get("DAILY_SOURCE_ATTEMPT_QUOTA_BYTES", str(1024 * 1024 * 1024))
)
DAILY_GLOBAL_ATTEMPT_QUOTA_BYTES = int(
    os.environ.get("DAILY_GLOBAL_ATTEMPT_QUOTA_BYTES", str(4 * 1024 * 1024 * 1024))
)
MINIMUM_FREE_BYTES = int(os.environ.get("MINIMUM_FREE_BYTES", str(2 * 1024 * 1024 * 1024)))
RETENTION_DAYS = int(os.environ.get("RETENTION_DAYS", "30"))
REJECTED_RETENTION_DAYS = 7
REJECTED_QUOTA_BYTES = 256 * 1024 * 1024
GLOBAL_ENROLLMENTS_PER_HOUR = int(os.environ.get("GLOBAL_ENROLLMENTS_PER_HOUR", "1000"))
MAX_INSTALLATIONS = int(os.environ.get("MAX_INSTALLATIONS", "100000"))
UNREFERENCED_INSTALLATION_RETENTION_DAYS = int(
    os.environ.get("UNREFERENCED_INSTALLATION_RETENTION_DAYS", "30")
)
PEPPER_PATH = Path(os.environ.get("SERVER_PEPPER_FILE", "/run/secrets/server_pepper"))
EXPECTED_HEADERS = {
    version: (Path(__file__).parent.parent / f"schema-v{version}.header").read_text(encoding="utf-8").strip()
    for version in (5, 6, 7)
}
STORE = Store(DATA_ROOT)
UPLOAD_PACER = UploadPacer()
INGRESS_LOG = logging.getLogger("uvicorn.error")

if not 1024 * 1024 <= MAX_UPLOAD_BYTES <= 64 * 1024 * 1024:
    raise RuntimeError("MAX_UPLOAD_BYTES is outside policy")
if not 1 <= GLOBAL_ENROLLMENTS_PER_HOUR <= 10000:
    raise RuntimeError("GLOBAL_ENROLLMENTS_PER_HOUR is outside policy")
if not 1000 <= MAX_INSTALLATIONS <= 1_000_000:
    raise RuntimeError("MAX_INSTALLATIONS is outside policy")
if not 1 <= UNREFERENCED_INSTALLATION_RETENTION_DAYS <= 365:
    raise RuntimeError("UNREFERENCED_INSTALLATION_RETENTION_DAYS is outside policy")

pepper_bytes = PEPPER_PATH.read_bytes()
# Preserve exact 32-byte binary secrets. For longer text-form secrets, retain
# the historical newline/whitespace normalization so existing deployments do
# not change their derived address keys after an update.
SERVER_PEPPER = pepper_bytes if len(pepper_bytes) == 32 else pepper_bytes.strip()
if len(SERVER_PEPPER) < 32:
    raise RuntimeError("server pepper is missing or too short")


@asynccontextmanager
async def lifespan(_: FastAPI):
    STORE.initialize()
    STORE.prune(RETENTION_DAYS, STORAGE_QUOTA_BYTES)
    STORE.prune_installations(UNREFERENCED_INSTALLATION_RETENTION_DAYS)
    STORE.prune_rejected(REJECTED_RETENTION_DAYS, REJECTED_QUOTA_BYTES)
    yield


app = FastAPI(
    title="MSFS Landing Stats telemetry ingress",
    docs_url=None,
    redoc_url=None,
    openapi_url=None,
    lifespan=lifespan,
)


class EnrollmentRequest(BaseModel):
    install_id: str = Field(min_length=32, max_length=32)
    sent_at_utc: str = Field(min_length=20, max_length=40)
    nonce: str = Field(min_length=22, max_length=64)
    public_modulus: str = Field(min_length=300, max_length=1024)
    public_exponent: str = Field(min_length=4, max_length=16)
    signature: str = Field(min_length=300, max_length=1024)


@app.exception_handler(StarletteHTTPException)
async def rejection_handler(request: Request, exc: StarletteHTTPException):
    request.state.rejection_reason = str(exc.detail)[:240]
    return await http_exception_handler(request, exc)


@app.exception_handler(RequestValidationError)
async def malformed_request_handler(request: Request, exc: RequestValidationError):
    # Pydantic's error details can contain the submitted key/signature. Never log them.
    request.state.rejection_reason = "request fields do not match the API schema"
    return await request_validation_exception_handler(request, exc)


@app.exception_handler(ClientDisconnect)
async def disconnected_handler(request: Request, exc: ClientDisconnect):
    request.state.rejection_reason = "client disconnected while uploading"
    return Response(status_code=499)


async def ingress_diagnostics(request: Request, call_next):
    started = time.monotonic()
    code = 500
    try:
        response = await call_next(request)
        code = response.status_code
        return response
    except asyncio.CancelledError:
        code = 499
        request.state.rejection_reason = "request cancelled"
        raise
    finally:
        if request.url.path in {"/v1/config", "/v1/enroll", "/v1/captures"}:
            fields = {
                "event": "telemetry_ingress", "path": request.url.path, "status": code,
                "duration_ms": round((time.monotonic() - started) * 1000),
                "reason": getattr(request.state, "rejection_reason", "internal error" if code == 500 else ""),
                "source": address_key(SERVER_PEPPER, _source_address(request))[:16],
            }
            for header, name, pattern in (
                ("x-install-id", "install_id", INSTALL_ID_RE),
                ("x-capture-sha256", "sha256", re.compile(r"^[a-fA-F0-9]{64}$")),
                ("x-app-version", "app_version", VERSION_RE),
                ("cf-ray", "ray", re.compile(r"^[a-fA-F0-9]{16,32}(?:-[A-Z]{3})?$")),
            ):
                value = request.headers.get(header, "")
                if len(value) <= 128 and pattern.fullmatch(value):
                    fields[name] = value
            fields["pacing_ms"] = round(getattr(request.state, "pacing_seconds", 0.0) * 1000)
            fields["quarantine"] = getattr(request.state, "quarantine", "")
            # JSON escapes all control characters; only bounded/validated metadata,
            # no addresses, request bodies, nonces, keys or signatures are included.
            INGRESS_LOG.info(json.dumps(fields, separators=(",", ":")))


@app.middleware("http")
async def request_size_guard(request: Request, call_next):
    if request.method == "POST" and request.url.path == "/v1/enroll":
        content_length = request.headers.get("content-length")
        try:
            size = int(content_length) if content_length is not None else -1
        except ValueError:
            size = -1
        if size < 0:
            request.state.rejection_reason = "Content-Length is required"
            return Response(status_code=411, content="Content-Length is required")
        if size > 32 * 1024:
            request.state.rejection_reason = "enrollment request exceeds policy"
            return Response(status_code=413, content="enrollment request exceeds policy")
    return await call_next(request)


app.middleware("http")(ingress_diagnostics)


def _source_address(request: Request) -> str:
    value = request.headers.get("cf-connecting-ip", "")
    if not value or len(value) > 64 or not re.fullmatch(r"[0-9A-Fa-f:.]+", value):
        return "unknown"
    return value


def _fresh_timestamp(value: str) -> None:
    parsed = parse_utc(value)
    if abs((datetime.now(timezone.utc) - parsed).total_seconds()) > 900:
        raise ValueError("request timestamp is outside the 15 minute window")


async def retain_rejection(request: Request, incoming: Path, sha256: str,
                           install_id: str, version: str, schema: int, reason: str) -> None:
    metadata = {"sha256": sha256, "install_id": install_id, "app_version": version,
                "schema": schema, "received_at": int(time.time()), "reason": reason[:240]}
    try:
        request.state.quarantine = await run_in_threadpool(
            STORE.quarantine_capture, incoming, sha256, metadata,
            REJECTED_RETENTION_DAYS, REJECTED_QUOTA_BYTES)
    except OSError:
        # The response still describes validation, not acceptance. The shipped
        # client also retains its rejected archive locally. Do not hide failures.
        request.state.quarantine = "retention_failed"


@app.get("/health")
def health() -> dict[str, str]:
    return {"status": "ok"}


@app.get("/v1/config")
def config() -> dict[str, object]:
    return {
        "protocol": 1,
        "registration_mode": REGISTRATION_MODE,
        "telemetry_schema": 5,
        "telemetry_schemas": list(EXPECTED_HEADERS),
        "capture_kinds": list(CAPTURE_KINDS),
        "decision_journal_formats": [DECISION_JOURNAL_FORMAT_VERSION],
        "max_upload_bytes": MAX_UPLOAD_BYTES,
    }


@app.post("/v1/enroll", status_code=status.HTTP_201_CREATED)
def enroll(payload: EnrollmentRequest, request: Request, response: Response) -> dict[str, str]:
    source_hash = address_key(SERVER_PEPPER, _source_address(request))
    existing = STORE.installation(payload.install_id)
    refresh = (existing is not None and existing["status"] == "active"
               and existing["modulus"] == payload.public_modulus
               and existing["exponent"] == payload.public_exponent)
    if refresh:
        # This does not create an identity. Keep an independent pre-verification
        # bound for RSA/DB work; still verify possession of the registered key.
        if not STORE.rate_limit("enroll-refresh-ip-minute", source_hash, 60, 6):
            raise rate_limited("registration refresh rate limit exceeded")
    else:
        if not STORE.rate_limit("enroll-ip-hour", source_hash, 3600, 10):
            raise rate_limited("enrollment rate limit exceeded", 3600)
        if not STORE.rate_limit("enroll-global-hour", "global", 3600, GLOBAL_ENROLLMENTS_PER_HOUR):
            raise rate_limited("global enrollment rate limit exceeded", 3600)
    try:
        if not INSTALL_ID_RE.fullmatch(payload.install_id):
            raise ValueError("invalid install_id")
        if not NONCE_RE.fullmatch(payload.nonce):
            raise ValueError("invalid nonce")
        _fresh_timestamp(payload.sent_at_utc)
        key = public_key(payload.public_modulus, payload.public_exponent)
        verify_signature(
            key,
            canonical_enrollment(
                payload.install_id,
                payload.sent_at_utc,
                payload.nonce,
                payload.public_modulus,
                payload.public_exponent,
            ),
            payload.signature,
        )
        if existing is None and shutil.disk_usage(DATA_ROOT).free < MINIMUM_FREE_BYTES:
            raise HTTPException(status_code=507, detail="telemetry storage reserve is active")
        created = STORE.enroll(
            payload.install_id,
            payload.public_modulus,
            payload.public_exponent,
            maximum_installations=MAX_INSTALLATIONS,
            idle_retention_days=UNREFERENCED_INSTALLATION_RETENTION_DAYS,
        )
    except InstallationRegistryFullError as exc:
        raise HTTPException(status_code=503, detail=str(exc)) from exc
    except PermissionError as exc:
        raise HTTPException(status_code=403, detail=str(exc)) from exc
    except ValueError as exc:
        raise HTTPException(status_code=400, detail=str(exc)) from exc
    response.status_code = status.HTTP_201_CREATED if created else status.HTTP_200_OK
    return {"status": "enrolled", "install_id": payload.install_id}


@app.post("/v1/captures", status_code=status.HTTP_201_CREATED)
async def upload_capture(request: Request) -> dict[str, object]:
    headers = request.headers
    install_id = headers.get("x-install-id", "")
    sent_at_utc = headers.get("x-sent-at-utc", "")
    nonce = headers.get("x-capture-nonce", "")
    expected_sha = headers.get("x-capture-sha256", "").lower()
    app_version = headers.get("x-app-version", "")
    signature = headers.get("x-signature", "")
    try:
        expected_size = int(headers.get("x-capture-size", ""))
        schema = int(headers.get("x-telemetry-schema", ""))
    except ValueError as exc:
        raise HTTPException(status_code=400, detail="invalid numeric upload header") from exc

    source_hash = address_key(SERVER_PEPPER, _source_address(request))
    if not await run_in_threadpool(STORE.rate_limit, "capture-ip-minute", source_hash, 60, 6):
        raise rate_limited("source rate limit exceeded")
    if not INSTALL_ID_RE.fullmatch(install_id):
        raise HTTPException(status_code=400, detail="invalid install_id")
    if not NONCE_RE.fullmatch(nonce):
        raise HTTPException(status_code=400, detail="invalid nonce")
    if not re.fullmatch(r"[a-f0-9]{64}", expected_sha):
        raise HTTPException(status_code=400, detail="invalid SHA-256")
    if schema not in EXPECTED_HEADERS or not VERSION_RE.fullmatch(app_version):
        raise HTTPException(status_code=400, detail="unsupported client metadata")
    if expected_size <= 0 or expected_size > MAX_UPLOAD_BYTES:
        raise HTTPException(status_code=413, detail="upload size exceeds policy")
    content_length = request.headers.get("content-length")
    try:
        actual_content_length = int(content_length) if content_length is not None else None
    except ValueError as exc:
        raise HTTPException(status_code=400, detail="invalid Content-Length") from exc
    if actual_content_length is None or actual_content_length != expected_size:
        raise HTTPException(status_code=411, detail="Content-Length must match signed size")

    installation = await run_in_threadpool(STORE.installation, install_id)
    if installation is None or installation["status"] != "active":
        raise HTTPException(status_code=403, detail="installation is not enrolled")
    try:
        _fresh_timestamp(sent_at_utc)
        verify_signature(
            public_key(installation["modulus"], installation["exponent"]),
            canonical_capture(
                install_id, sent_at_utc, nonce, expected_sha, expected_size, schema, app_version
            ),
            signature,
        )
    except ValueError as exc:
        raise HTTPException(status_code=403, detail=str(exc)) from exc
    if not await run_in_threadpool(STORE.use_nonce, install_id, nonce):
        raise HTTPException(status_code=409, detail="replayed nonce")

    accepted_today = await run_in_threadpool(
        STORE.accepted_bytes_since, install_id, int(time.time()) - 86400
    )
    if accepted_today + expected_size > DAILY_INSTALL_QUOTA_BYTES:
        raise HTTPException(status_code=429, detail="installation daily byte quota exceeded",
                            headers={"Retry-After": "86400"})
    free_bytes = (await run_in_threadpool(shutil.disk_usage, DATA_ROOT)).free
    if free_bytes - expected_size < MINIMUM_FREE_BYTES:
        raise HTTPException(status_code=507, detail="telemetry storage reserve is active")
    if not await run_in_threadpool(
        STORE.reserve_byte_budgets,
        [
            ("capture-install-day", install_id, 86400, expected_size, DAILY_INSTALL_QUOTA_BYTES),
            ("capture-source-day", source_hash, 86400, expected_size, DAILY_SOURCE_ATTEMPT_QUOTA_BYTES),
            ("capture-global-day", "global", 86400, expected_size, DAILY_GLOBAL_ATTEMPT_QUOTA_BYTES),
        ],
    ):
        raise rate_limited("signed upload byte budget exceeded", 86400)

    existing = await run_in_threadpool(STORE.capture_by_hash, expected_sha)
    if existing is not None:
        return {"status": "already_received", "capture_id": existing["capture_id"]}

    capture_id = secrets.token_hex(16)
    incoming = DATA_ROOT / "incoming" / f"{capture_id}.part"
    digest = hashlib.sha256()
    received = 0
    try:
        output = await run_in_threadpool(incoming.open, "xb")
        try:
            async for chunk in request.stream():
                received += len(chunk)
                if received > MAX_UPLOAD_BYTES or received > expected_size:
                    raise HTTPException(status_code=413, detail="upload exceeded signed size")
                digest.update(chunk)
                await run_in_threadpool(output.write, chunk)
            await run_in_threadpool(output.flush)
            await run_in_threadpool(os.fsync, output.fileno())
        finally:
            await run_in_threadpool(output.close)
        if received != expected_size or digest.hexdigest() != expected_sha:
            raise HTTPException(status_code=400, detail="upload hash or size mismatch")
        # Finish receiving the bounded file before pacing: waiting while the
        # proxy is still writing a large body can trigger its write timeout.
        request.state.pacing_seconds = await UPLOAD_PACER.admit(STORE, install_id)
        try:
            facts = await run_in_threadpool(
                validate_capture, incoming, EXPECTED_HEADERS[schema], MAX_UNCOMPRESSED_BYTES, schema
            )
        except UnicodeDecodeError as exc:
            await retain_rejection(request, incoming, expected_sha, install_id, app_version, schema,
                                   "archive text is not valid UTF-8")
            raise HTTPException(status_code=422, detail="archive text is not valid UTF-8") from exc
        except OSError as exc:
            # A receiver disk/read error is transient, not a malformed archive.
            # Old clients quarantine 422 permanently; 503 retains their retry.
            raise HTTPException(status_code=503, detail="archive read failed; retry later") from exc
        except ValueError as exc:
            await retain_rejection(request, incoming, expected_sha, install_id, app_version, schema, str(exc))
            raise HTTPException(status_code=422, detail=str(exc)) from exc

        now = datetime.now(timezone.utc)
        relative = Path("accepted") / now.strftime("%Y") / now.strftime("%m") / now.strftime("%d") / f"{capture_id}.zip"
        destination = DATA_ROOT / relative
        await run_in_threadpool(destination.parent.mkdir, parents=True, exist_ok=True)
        await run_in_threadpool(os.replace, incoming, destination)
        try:
            await run_in_threadpool(
                STORE.add_capture,
                (
                    capture_id,
                    install_id,
                    expected_sha,
                    relative.as_posix(),
                    int(time.time()),
                    received,
                    facts.uncompressed_bytes,
                    facts.sample_count,
                    schema,
                    app_version,
                    facts.capture_kind,
                    source_hash,
                ),
            )
        except Exception:
            await run_in_threadpool(destination.unlink, missing_ok=True)
            raise
        await run_in_threadpool(STORE.prune, RETENTION_DAYS, STORAGE_QUOTA_BYTES)
        return {"status": "accepted", "capture_id": capture_id, "sample_count": facts.sample_count}
    finally:
        await run_in_threadpool(incoming.unlink, missing_ok=True)
