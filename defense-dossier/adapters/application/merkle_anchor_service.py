
"""
Merkle Anchor Service — the piece that was still missing after the first
pass: MerkleBatchBuilder (application/merkle.py) can accumulate leaves and
knows WHEN to close, but nothing was calling close, signing the root, timestamping
it, or making a proof retrievable. This class is that missing link.

It closes a batch (by N leaves or T seconds — merkle.MerkleBatchBuilder
decides which), signs the root (Signer), timestamps it (TimestampAuthority),
and projects one proof-lookup row per leaf into ProjectionStore, keyed by
leaf_hash. GetMerkleProof (api/main.py) then becomes a pure O(1) read —
no on-demand tree rebuild per query, and no dependency on the batch object
surviving past the moment it closes.

CQRS note: this class writes to ProjectionStore. That is allowed here for
the same reason ScoreboardProjector is allowed to: both derive read-optimized
state from the append-only log. Neither is a command handler. See the
class docstring in scoreboard_projector.py for the same argument.
"""
from __future__ import annotations

import datetime as dt
import logging
import uuid

from application.merkle import MerkleBatchBuilder
from interfaces.audit_trail import AuditTrail
from interfaces.projection_store import ProjectionStore
from interfaces.signer import Signer
from interfaces.tsa import TimestampAuthority

logger = logging.getLogger("core.merkle_anchor_service")

class MerkleAnchorService:
    def __init__(
        self,
        builder: MerkleBatchBuilder,
        audit_trail: AuditTrail,
        projection_store: ProjectionStore,
        signer: Signer,
        tsa: TimestampAuthority,
    ) -> None:
        self._builder = builder
        self._audit = audit_trail
        self._store = projection_store
        self._signer = signer
        self._tsa = tsa

    async def maybe_close_and_anchor(self) -> str | None:
        """Called on a timer (api/main.py background task). No-op unless one
    of the two thresholds (N leaves / T seconds) has actually fired —
    checking should_close() here, not just closing unconditionally, is
    what keeps both merkle_requirements thresholds meaningful instead of
    one being dead code (see README 'Load target' section)."""
        now_ts = dt.datetime.now(dt.timezone.utc).timestamp()
        if not self._builder.should_close(now_ts):
            return None
        return await self._close_and_anchor()

    async def force_close(self) -> str | None:
        """Used by tests and graceful shutdown to flush a partially-filled
    batch instead of waiting for a threshold."""
        if not self._builder.has_pending():
            return None
        return await self._close_and_anchor()

    async def _close_and_anchor(self) -> str:
        batch, root, pending = self._builder.close_and_reset()
        batch_id = str(uuid.uuid4())
        signature = self._signer.sign(root)
        tsa_token = await self._tsa.timestamp(root)
        root_hex = root.hex()

        for idx, leaf in enumerate(batch.leaves):
            proof = batch.proof_for(idx)
            await self._store.set(
                f"merkle_proof:{leaf.hex()}",
                {
                "leaf_hash": leaf.hex(),
                "proof_path": [
                {"hash": step.sibling_hash.hex(), "position": step.position} for step in proof
                ],
                "root": root_hex,
                "anchor_signature": signature.hex(),
                "anchor_timestamp": tsa_token.timestamp_iso,
                "batch_id": batch_id,
                },
            )

        # Bug fix (caught by an external review of these corrections, but the
        # literal code proposed there assumed a different EventBus/
        # ProjectionStore/MerkleBatchBuilder API than the ones actually in
        # this repo — see README changelog): a single batch commonly covers
        # LEAVES FROM MANY DIFFERENT decision_ids. The previous version only
        # ever appended MerkleRootPublished under decision_id="system",
        # which ScoreboardProjector.rebuild(decision_id, ...) — scoped to one
        # specific decision_id — could never see. merkle_root_ref on
        # GetScoreboard was therefore always None, for every decision, even
        # after anchoring. Fix: append one correctly-scoped copy of the event
        # per unique decision_id actually present in this batch, in addition
        # to the global "system" one (kept for batch-level audit/ops
        # queries — 'how many batches closed today, how many leaves each').
        payload_common = {
            "batch_id": batch_id,
            "root": root_hex,
            "leaf_count": len(batch.leaves),
            "anchor_signature": signature.hex(),
            "anchor_timestamp": tsa_token.timestamp_iso,
        }
        await self._audit.append("system", "MerkleRootPublished", payload_common)
        for decision_id in sorted({p.decision_id for p in pending}):
            await self._audit.append(decision_id, "MerkleRootPublished", payload_common)

        await self._audit.mark_batched([p.event_id for p in pending], batch_id)
        logger.info("merkle batch closed: batch_id=%s leaves=%d decisions=%d root=%s",
        batch_id, len(batch.leaves), len({p.decision_id for p in pending}), root_hex)
        return batch_id




