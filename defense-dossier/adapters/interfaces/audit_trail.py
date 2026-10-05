
"""AuditTrail interface. Spec constraint: 'Persistence через интерфейсы'.

Exposes ONLY append + read — no update()/delete() exists anywhere on this
Protocol. Invariant I7 ('Core никогда не мутирует прошлое, только append')
is enforced by the SHAPE of the interface, not by convention.

Idempotency (I6) is handled INSIDE append(), atomically, rather than via a
separate check-then-act call from the command handler: a check-then-append
done as two calls is a race (two concurrent requests with the same
idempotency_key could both pass the check before either appends). Folding
the check into append() is also how this maps onto a real backend — e.g. a
Postgres UNIQUE constraint on idempotency_key, or a Kafka topic keyed so a
compacting consumer can dedupe.

This also keeps the command handler compliant with 'Команды не читают
projection': idempotency dedup is a write-side concern answered by
AuditTrail, never a read against ProjectionStore.
"""
from __future__ import annotations

from dataclasses import dataclass
from typing import Any, Protocol, runtime_checkable

@dataclass(frozen=True)
class AppendResult:
    event_id: str
    deduplicated: bool
    existing_event_id: str | None = None

@runtime_checkable
class AuditTrail(Protocol):
    async def append(
        self,
        decision_id: str,
        event_type: str,
        payload: dict[str, Any],
        idempotency_key: str | None = None,
    ) -> AppendResult:
        """Append one event. If idempotency_key was seen before, this is a
no-op: no new entry, AppendResult.deduplicated=True (I6)."""
        ...

    async def read_range(self, decision_id: str, since_seq: int = 0) -> list[dict[str, Any]]:
        """Ordered, replayable event stream for one decision_id (I2,
    deterministic state reconstruction)."""
        ...

    async def read_unbatched(self, limit: int) -> list[dict[str, Any]]:
        """Events not yet folded into a Merkle batch."""
        ...

    async def mark_batched(self, event_ids: list[str], batch_id: str) -> None: ...




