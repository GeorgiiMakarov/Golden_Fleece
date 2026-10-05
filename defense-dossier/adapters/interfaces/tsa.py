
"""TimestampAuthority interface. Spec 1.1 Assumption 7: 'pluggable TSA'.
Banking Profile KZ uses 'TSA NUTS RK' (RFC 3161); Game profile uses an
internal TSA; Construction profile uses a blockchain-based TSA — all satisfy
this same Protocol."""
from __future__ import annotations
from typing import Protocol, runtime_checkable

@runtime_checkable
class TimestampAuthority(Protocol):
    async def timestamp(self, digest: bytes) -> "TsaToken": ...
    def verify(self, digest: bytes, token: "TsaToken") -> bool: ...

class TsaToken:
    __slots__ = ("token_bytes", "timestamp_iso", "authority_id")

    def __init__(self, token_bytes: bytes, timestamp_iso: str, authority_id: str) -> None:
        self.token_bytes = token_bytes
        self.timestamp_iso = timestamp_iso
        self.authority_id = authority_id




