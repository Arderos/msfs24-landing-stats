"""LAN-only, read-only web application; deliberately separate from the ingress API."""

import ipaddress
import os
import re
from contextlib import asynccontextmanager
from pathlib import Path

from fastapi import FastAPI, HTTPException, Query, Request, Response
from fastapi.staticfiles import StaticFiles
from starlette.middleware.trustedhost import TrustedHostMiddleware

from .dashboard_index import DashboardIndex


def create_app(index=None, allowed_networks=None, allowed_hosts=None):
    if index is None:
        index = DashboardIndex(
            Path(os.environ.get("DATA_ROOT", "/data")),
            Path(os.environ.get("CACHE_ROOT", "/cache")),
        )
    networks = tuple(ipaddress.ip_network(value.strip()) for value in (
        allowed_networks if allowed_networks is not None else
        os.environ.get("DASHBOARD_ALLOWED_NETWORKS", "127.0.0.0/8,::1/128").split(",")
    ))
    hosts = allowed_hosts if allowed_hosts is not None else os.environ.get(
        "DASHBOARD_ALLOWED_HOSTS", "localhost,127.0.0.1,[::1]"
    ).split(",")

    @asynccontextmanager
    async def lifespan(_):
        index.start()
        try:
            yield
        finally:
            index.stop()

    app = FastAPI(title="Landing Stats · Telemetry", docs_url=None,
                  redoc_url=None, openapi_url=None, lifespan=lifespan)
    app.state.index = index
    app.add_middleware(TrustedHostMiddleware, allowed_hosts=[host.strip() for host in hosts])

    @app.middleware("http")
    async def private_network_only(request: Request, call_next):
        # Never use forwarded headers: this listener is reached directly from the LAN.
        try:
            address = ipaddress.ip_address(request.client.host) if request.client else None
            if isinstance(address, ipaddress.IPv6Address) and address.ipv4_mapped:
                address = address.ipv4_mapped
            permitted = address is not None and any(address in network for network in networks)
        except ValueError:
            permitted = False
        if not permitted or request.headers.get("sec-fetch-site") == "cross-site":
            response = Response("Private network access only", status_code=403)
        elif request.method not in ("GET", "HEAD"):
            response = Response(status_code=405, headers={"Allow": "GET, HEAD"})
        else:
            response = await call_next(request)
        response.headers.update({
            "Content-Security-Policy": "default-src 'self'; script-src 'self'; style-src 'self'; "
                "img-src 'self' data:; connect-src 'self'; object-src 'none'; "
                "base-uri 'none'; frame-ancestors 'none'; form-action 'none'",
            "X-Content-Type-Options": "nosniff",
            "X-Frame-Options": "DENY",
            "Referrer-Policy": "no-referrer",
            "Permissions-Policy": "camera=(), microphone=(), geolocation=()",
            "Cache-Control": "no-store",
        })
        return response

    @app.get("/health")
    def health():
        return {"status": "ok", "service": "telemetry-dashboard"}

    @app.get("/api/summary")
    def summary(days: int = Query(0, ge=0, le=3650)):
        return index.summary(days=days)

    @app.get("/api/captures")
    def captures(days: int = Query(0, ge=0, le=3650),
                 q: str = Query("", max_length=256),
                 airport: str = Query("", max_length=128),
                 aircraft: str = Query("", max_length=256),
                 version: str = Query("", max_length=64),
                 kind: str = Query("", max_length=64),
                 outcome: str = Query("", max_length=64),
                 page: int = Query(1, ge=1, le=100000),
                 page_size: int = Query(50, ge=1, le=100)):
        return index.list_captures(days=days, q=q, airport=airport, aircraft=aircraft,
                                   version=version, kind=kind, outcome=outcome, page=page, page_size=page_size)

    @app.get("/api/captures/{capture_id}")
    def capture(capture_id: str):
        if not re.fullmatch(r"[a-f0-9]{32}", capture_id):
            raise HTTPException(status_code=404, detail="Capture not found")
        detail = index.capture(capture_id)
        if detail is None:
            raise HTTPException(status_code=404, detail="Capture not found")
        return detail

    static = Path(__file__).parent / "dashboard_static"
    app.mount("/static", StaticFiles(directory=static), name="assets")
    app.mount("/", StaticFiles(directory=static, html=True), name="web")
    return app


app = create_app()
