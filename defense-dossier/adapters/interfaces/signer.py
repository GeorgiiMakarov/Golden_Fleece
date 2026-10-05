
"""Signer interface. Spec 1.1 Assumption 5: 'Pluggable Signer'. Constraints
name Ed25519 as the concrete adapter used by Banking Profile KZ (section 4.1);
the interface itself stays algorithm-agnostic."""
from __future__ import annotations
from typing import Protocol, runtime_checkable

@runtime_checkable
class Signer(Protocol):
    def sign(self, payload: bytes) -> bytes: ...
    def verify(self, payload: bytes, signature: bytes, public_key: bytes) -> bool: ...
    def public_key_bytes(self) -> bytes: ...
    def key_id(self) -> str:
        """Identifies which key (and rotation generation) produced a signature,
so old inclusion proofs stay verifiable after rotation (Assumption 5)."""
        ...




