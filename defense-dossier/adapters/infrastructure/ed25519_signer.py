
"""
Concrete Ed25519 Signer — the adapter Banking Profile KZ (section 4.1) plugs
into the `Signer` Protocol. Assumption 5: key rotates every 90 days; key_id
lets an old inclusion proof still resolve to the correct historical public
key after rotation.

Uses `cryptography` (pyca/cryptography), the standard, audited Python Ed25519
implementation — not a hand-rolled one, deliberately, since this is exactly
the kind of primitive you do not want to reimplement.
"""
from __future__ import annotations

from cryptography.hazmat.primitives.asymmetric.ed25519 import Ed25519PrivateKey, Ed25519PublicKey
from cryptography.hazmat.primitives import serialization
from cryptography.exceptions import InvalidSignature

class Ed25519Signer:
    def __init__(self, private_key: Ed25519PrivateKey | None = None, key_id: str = "kz-banking-v1") -> None:
        self._private_key = private_key or Ed25519PrivateKey.generate()
        self._public_key = self._private_key.public_key()
        self._key_id = key_id

    def sign(self, payload: bytes) -> bytes:
        return self._private_key.sign(payload)

    def verify(self, payload: bytes, signature: bytes, public_key: bytes) -> bool:
        try:
            Ed25519PublicKey.from_public_bytes(public_key).verify(signature, payload)
            return True
        except InvalidSignature:
            return False

    def public_key_bytes(self) -> bytes:
        return self._public_key.public_bytes(
            encoding=serialization.Encoding.Raw,
            format=serialization.PublicFormat.Raw,
        )

    def key_id(self) -> str:
        return self._key_id

    @classmethod
    def from_pem_file(cls, path: str, key_id: str) -> "Ed25519Signer":
        with open(path, "rb") as f:
            key = serialization.load_pem_private_key(f.read(), password=None)
        if not isinstance(key, Ed25519PrivateKey):
            raise TypeError("key file does not contain an Ed25519 private key")
        return cls(private_key=key, key_id=key_id)




