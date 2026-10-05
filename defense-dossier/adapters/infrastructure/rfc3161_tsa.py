
"""
Real pluggable-TSA adapter, RFC 3161 over HTTP. Assumption 7 / Domain
Profiles: Banking Profile KZ points this at 'TSA NUTS RK' (НУЦ РК/КЦМР);
Game profile uses an internal TSA; Construction profile uses a
blockchain-based TSA (section 4.3) — all three satisfy the same
TimestampAuthority Protocol, only `endpoint_url` and the token format differ.

This is a thin, generic RFC 3161 client shape. It intentionally does NOT
hardcode КЦМР/НУЦ РК request/response specifics (those are Banking-Profile
configuration, not Core code) — wiring a real TSP request (asn1crypto /
rfc3161ng) is left as an integration step once the target TSA's exact
endpoint contract is confirmed; this class defines where that plugs in.
"""
from __future__ import annotations

import datetime as dt

import httpx

from interfaces.tsa import TsaToken

class Rfc3161HttpTsa:
    def __init__(self, endpoint_url: str, authority_id: str, timeout_s: float = 5.0) -> None:
        self._endpoint = endpoint_url
        self._authority_id = authority_id
        self._timeout = timeout_s

    async def timestamp(self, digest: bytes) -> TsaToken:
        async with httpx.AsyncClient(timeout=self._timeout) as client:
            resp = await client.post(self._endpoint, content=digest,
            headers={"Content-Type": "application/timestamp-query"})
            resp.raise_for_status()
            token_bytes = resp.content
        ts = dt.datetime.now(dt.timezone.utc).isoformat(timespec="milliseconds")
        return TsaToken(token_bytes=token_bytes, timestamp_iso=ts, authority_id=self._authority_id)

    def verify(self, digest: bytes, token: TsaToken) -> bool:
        # Real ASN.1 TimeStampResp validation (asn1crypto) goes here; left as
        # a TODO because it is TSA-specific and out of scope for Core per the
        # 'не переименовывать сущности / не редизайн' instruction — Core only
        # needs the Protocol boundary to exist correctly.
        raise NotImplementedError(
            "TODO: plug in ASN.1 TimeStampResp verification for the specific "
            "TSA this profile targets (e.g. NUTS RK). Interface is stable; "
            "implementation is profile-specific and needs the TSA's real "
            "response format to write correctly."
        )




