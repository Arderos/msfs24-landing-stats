import hashlib
import os
import re
import secrets
import shutil
import time
from contextlib import asynccontextmanager
from datetime import datetime, timezone
from pathlib import Path

from fastapi import FastAPI, HTTPException, Request, Response, status
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
from .validation import validate_capture


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
GLOBAL_ENROLLMENTS_PER_HOUR = int(os.environ.get("GLOBAL_ENROLLMENTS_PER_HOUR", "1000"))
MAX_INSTALLATIONS = int(os.environ.get("MAX_INSTALLATIONS", "100000"))
UNREFERENCED_INSTALLATION_RETENTION_DAYS = int(
    os.environ.get("UNREFERENCED_INSTALLATION_RETENTION_DAYS", "30")
)
PEPPER_PATH = Path(os.environ.get("SERVER_PEPPER_FILE", "/run/secrets/server_pepper"))
EXPECTED_HEADER = (Path(__file__).parent.parent / "schema-v5.header").read_text(encoding="utf-8").strip()
STORE = Store(DATA_ROOT)

if not 1024 * 1024 <= MAX_UPLOAD_BYTES <= 64 * 1024 * 1024:
    raise RuntimeError("MAX_UPLOAD_BYTES is outside policy")
if not 1 <= GLOBAL_ENROLLMENTS_PER_HOUR <= 10000:
    raise RuntimeError("GLOBAL_ENROLLMENTS_PER_HOUR is outside policy")
if not 1000 <= MAX_INSTALLATIONS <= 1_000_000:
    raise RuntimeError("MAX_INSTALLATIONS is outside policy")
if not 1 <= UNREFERENCED_INSTALLATION_RETENTION_DAYS <= 365:
    raise RuntimeError("UNREFERENCED_INSTALLATION_RETENTION_DAYS is outside policy")

SERVER_PEPPER = PEPPER_PATH.read_bytes().strip()
if len(SERVER_PEPPER) < 32:
    raise RuntimeError("server pepper is missing or too short")


@asynccontextmanager
async def lifespan(_: FastAPI):
    STORE.initialize()
    STORE.prune(RETENTION_DAYS, STORAGE_QUOTA_BYTES)
    STORE.prune_installations(UNREFERENCED_INSTALLATION_RETENTION_DAYS)
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


@app.middleware("http")
async def request_size_guard(request: Request, call_next):
    if request.method == "POST" and request.url.path == "/v1/enroll":
        content_length = request.headers.get("content-length")
        try:
            size = int(content_length) if content_length is not None else -1
        except ValueError:
            size = -1
        if size < 0:
            return Response(status_code=411, content="Content-Length is required")
        if size > 32 * 1024:
            return Response(status_code=413, content="enrollment request exceeds policy")
    return await call_next(request)


def _source_address(request: Request) -> str:
    value = request.headers.get("cf-connecting-ip", "")
    if not value or len(value) > 64 or not re.fullmatch(r"[0-9A-Fa-f:.]+", value):
        return "unknown"
    return value


def _fresh_timestamp(value: str) -> None:
    parsed = parse_utc(value)
    if abs((datetime.now(timezone.utc) - parsed).total_seconds()) > 900:
        raise ValueError("request timestamp is outside the 15 minute window")


@app.get("/health")
def health() -> dict[str, str]:
    return {"status": "ok"}


@app.get("/v1/config")
def config() -> dict[str, object]:
    return {
        "protocol": 1,
        "registration_mode": REGISTRATION_MODE,
        "telemetry_schema": 5,
        "max_upload_bytes": MAX_UPLOAD_BYTES,
    }


@app.post("/v1/enroll", status_code=status.HTTP_201_CREATED)
def enroll(payload: EnrollmentRequest, request: Request, response: Response) -> dict[str, str]:
    source_hash = address_key(SERVER_PEPPER, _source_address(request))
    if not STORE.rate_limit("enroll-ip-hour", source_hash, 3600, 10):
        raise HTTPException(status_code=429, detail="enrollment rate limit exceeded")
    if not STORE.rate_limit("enroll-global-hour", "global", 3600, GLOBAL_ENROLLMENTS_PER_HOUR):
        raise HTTPException(status_code=429, detail="global enrollment rate limit exceeded")
    was_enrolled = STORE.installation(payload.install_id) is not None
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
        if not was_enrolled and shutil.disk_usage(DATA_ROOT).free < MINIMUM_FREE_BYTES:
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
    if not STORE.rate_limit("capture-ip-minute", source_hash, 60, 6):
        raise HTTPException(status_code=429, detail="source rate limit exceeded")
    if not INSTALL_ID_RE.fullmatch(install_id):
        raise HTTPException(status_code=400, detail="invalid install_id")
    if not STORE.rate_limit("capture-install-minute", install_id, 60, 3):
        raise HTTPException(status_code=429, detail="installation rate limit exceeded")
    if not NONCE_RE.fullmatch(nonce):
        raise HTTPException(status_code=400, detail="invalid nonce")
    if not re.fullmatch(r"[a-f0-9]{64}", expected_sha):
        raise HTTPException(status_code=400, detail="invalid SHA-256")
    if schema != 5 or not VERSION_RE.fullmatch(app_version):
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

    installation = STORE.installation(install_id)
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
    if not STORE.use_nonce(install_id, nonce):
        raise HTTPException(status_code=409, detail="replayed nonce")

    accepted_today = STORE.accepted_bytes_since(install_id, int(time.time()) - 86400)
    if accepted_today + expected_size > DAILY_INSTALL_QUOTA_BYTES:
        raise HTTPException(status_code=429, detail="installation daily byte quota exceeded")
    free_bytes = shutil.disk_usage(DATA_ROOT).free
    if free_bytes - expected_size < MINIMUM_FREE_BYTES:
        raise HTTPException(status_code=507, detail="telemetry storage reserve is active")
    if not STORE.reserve_byte_budgets(
        [
            ("capture-install-day", install_id, 86400, expected_size, DAILY_INSTALL_QUOTA_BYTES),
            ("capture-source-day", source_hash, 86400, expected_size, DAILY_SOURCE_ATTEMPT_QUOTA_BYTES),
            ("capture-global-day", "global", 86400, expected_size, DAILY_GLOBAL_ATTEMPT_QUOTA_BYTES),
        ]
    ):
        raise HTTPException(status_code=429, detail="signed upload byte budget exceeded")

    existing = STORE.capture_by_hash(expected_sha)
    if existing is not None:
        return {"status": "already_received", "capture_id": existing["capture_id"]}

    capture_id = secrets.token_hex(16)
    incoming = DATA_ROOT / "incoming" / f"{capture_id}.part"
    digest = hashlib.sha256()
    received = 0
    try:
        with incoming.open("xb") as output:
            async for chunk in request.stream():
                received += len(chunk)
                if received > MAX_UPLOAD_BYTES or received > expected_size:
                    raise HTTPException(status_code=413, detail="upload exceeded signed size")
                digest.update(chunk)
                output.write(chunk)
            output.flush()
            os.fsync(output.fileno())
        if received != expected_size or digest.hexdigest() != expected_sha:
            raise HTTPException(status_code=400, detail="upload hash or size mismatch")
        try:
            facts = validate_capture(incoming, EXPECTED_HEADER, MAX_UNCOMPRESSED_BYTES)
        except (ValueError, OSError) as exc:
            raise HTTPException(status_code=422, detail=str(exc)) from exc

        now = datetime.now(timezone.utc)
        relative = Path("accepted") / now.strftime("%Y") / now.strftime("%m") / now.strftime("%d") / f"{capture_id}.zip"
        destination = DATA_ROOT / relative
        destination.parent.mkdir(parents=True, exist_ok=True)
        os.replace(incoming, destination)
        try:
            STORE.add_capture(
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
                    source_hash,
                )
            )
        except Exception:
            destination.unlink(missing_ok=True)
            raise
        STORE.prune(RETENTION_DAYS, STORAGE_QUOTA_BYTES)
        return {"status": "accepted", "capture_id": capture_id, "sample_count": facts.sample_count}
    finally:
        incoming.unlink(missing_ok=True)
