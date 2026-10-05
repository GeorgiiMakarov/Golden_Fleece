# Defense-Dossier adapters

Signer / TSA / Anchor adapters for the Merkle-anchored audit trail.

- `infrastructure/ed25519_signer.py` — Ed25519 signer (`cryptography` package)
- `infrastructure/rfc3161_client.py` — RFC 3161 TSA client, pure stdlib
  (verifies PKIStatus + messageImprint; CMS signature chain validation out of scope)
- `infrastructure/rfc3161_tsa.py` — TSA transport (`httpx`)
- `application/merkle_anchor_service.py` + `application/merkle.py` — batching + anchoring
- `interfaces/` — `Signer`, `TimestampAuthority`, `AuditTrail`, `ProjectionStore` protocols

Layout mirrors `decision-intelligence-core` so imports keep working.
