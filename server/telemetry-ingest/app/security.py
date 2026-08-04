import base64
import binascii
import hashlib
import hmac
import re
from datetime import datetime, timezone

from cryptography.exceptions import InvalidSignature
from cryptography.hazmat.primitives import hashes
from cryptography.hazmat.primitives.asymmetric import padding, rsa


INSTALL_ID_RE = re.compile(r"^[a-f0-9]{32}$")
NONCE_RE = re.compile(r"^[A-Za-z0-9_-]{22,64}$")
VERSION_RE = re.compile(r"^[0-9]+\.[0-9]+\.[0-9]+(?:[-+][A-Za-z0-9.-]+)?$")


def strict_b64(value: str, maximum: int) -> bytes:
    if not value or len(value) > maximum:
        raise ValueError("invalid base64 length")
    try:
        return base64.b64decode(value, validate=True)
    except (ValueError, binascii.Error) as exc:
        raise ValueError("invalid base64") from exc


def public_key(modulus_b64: str, exponent_b64: str) -> rsa.RSAPublicKey:
    modulus = int.from_bytes(strict_b64(modulus_b64, 1024), "big")
    exponent = int.from_bytes(strict_b64(exponent_b64, 16), "big")
    if modulus.bit_length() < 2048 or modulus.bit_length() > 4096:
        raise ValueError("RSA key size is outside policy")
    if exponent < 3 or exponent % 2 == 0:
        raise ValueError("invalid RSA exponent")
    return rsa.RSAPublicNumbers(exponent, modulus).public_key()


def verify_signature(key: rsa.RSAPublicKey, message: bytes, signature_b64: str) -> None:
    signature = strict_b64(signature_b64, 1024)
    try:
        key.verify(signature, message, padding.PKCS1v15(), hashes.SHA256())
    except InvalidSignature as exc:
        raise ValueError("signature verification failed") from exc


def parse_utc(value: str) -> datetime:
    if len(value) > 40 or not value.endswith("Z"):
        raise ValueError("timestamp must be UTC")
    try:
        parsed = datetime.fromisoformat(value[:-1] + "+00:00")
    except ValueError as exc:
        raise ValueError("invalid timestamp") from exc
    return parsed.astimezone(timezone.utc)


def canonical_enrollment(
    install_id: str,
    sent_at_utc: str,
    nonce: str,
    modulus: str,
    exponent: str,
) -> bytes:
    return (
        "MSFS-LANDING-STATS-ENROLLMENT-V1\n"
        f"install_id={install_id}\n"
        f"sent_at_utc={sent_at_utc}\n"
        f"nonce={nonce}\n"
        f"public_modulus={modulus}\n"
        f"public_exponent={exponent}\n"
    ).encode("ascii")


def canonical_capture(
    install_id: str,
    sent_at_utc: str,
    nonce: str,
    sha256: str,
    size: int,
    schema: int,
    app_version: str,
) -> bytes:
    return (
        "MSFS-LANDING-STATS-TELEMETRY-V1\n"
        f"install_id={install_id}\n"
        f"sent_at_utc={sent_at_utc}\n"
        f"nonce={nonce}\n"
        f"sha256={sha256}\n"
        f"size={size}\n"
        f"schema={schema}\n"
        f"app_version={app_version}\n"
    ).encode("ascii")


def hash_invite(code: str) -> str:
    normalized = code.strip().upper().replace("-", "")
    if len(normalized) < 20 or len(normalized) > 128 or not normalized.isalnum():
        raise ValueError("invalid invitation code")
    return hashlib.sha256(normalized.encode("ascii")).hexdigest()


def address_key(pepper: bytes, address: str) -> str:
    return hmac.new(pepper, address.encode("utf-8"), hashlib.sha256).hexdigest()
