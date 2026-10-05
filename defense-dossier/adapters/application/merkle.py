
"""
Merkle Batch Builder — core algorithm. Spec merkle_requirements:

Leaf hash = SHA256(decision_id + metric_name + value + timestamp
                    + evidence_refs_sorted + idempotency_key)

Batching: every 5 seconds OR every 1000 leaves (profile-configurable, v1.2
Assumption 4: 'Два порога: N листьев ИЛИ T секунд').

Design note carried over from the v1.1 spec review: at the originally-proposed
10k decisions/sec the 'T seconds' branch was dead code (the N-leaf threshold
always won). This implementation makes both branches real and lets a Domain
Profile pick N/T that fit its own load (v1.2 Assumption 8: 'Нагрузка задаёт
профиль').

Every function here is pure / stdlib-only on purpose: this is the one module
in the whole service whose correctness matters most (a bug here silently
breaks I5 and I9), so it must be testable without Kafka, Redis, FastAPI or
even Pydantic. See tests/verify_core_logic_stdlib.py for an executed proof.
"""
from __future__ import annotations

import hashlib
import json
from dataclasses import dataclass, field
from typing import Any

def canonical_json(value: Any) -> str:
    """Deterministic serialization so the same logical value always hashes
the same way regardless of dict key insertion order."""
    return json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=False)

def compute_leaf_hash(
    *,
    decision_id: str,
    metric_name: str,
    value: Any,
    timestamp_iso: str,
    evidence_refs: list[str],
    idempotency_key: str,
) -> bytes:
    """Exact formula from merkle_requirements. evidence_refs is sorted AND
deduplicated here — the caller does not need to pre-sort; this makes the
function the single source of truth for what 'evidence_refs_sorted'
means. Dedup matters for consistency with domain.models.MetricRecord,
whose evidence_refs validator also dedupes: citing the same evidence_id
twice must hash identically to citing it once, or a rebuilt MetricRecord
would never reproduce the original leaf_hash."""
    sorted_refs = sorted(set(evidence_refs))
    payload = "|".join(
        [
        decision_id,
        metric_name,
        canonical_json(value),
        timestamp_iso,
        ",".join(sorted_refs),
        idempotency_key,
        ]
    )
    return hashlib.sha256(payload.encode("utf-8")).digest()

def _hash_pair(left: bytes, right: bytes) -> bytes:
    return hashlib.sha256(left + right).digest()

@dataclass
class ProofStep:
    sibling_hash: bytes
    position: str  # "LEFT" | "RIGHT" — position of the SIBLING relative to the node

@dataclass
class MerkleBatch:
    """A closed batch: leaves in insertion order, plus the tree needed to
produce inclusion proofs. Root is computed once, at close time — this is
the value that gets Ed25519-signed and RFC3161-stamped (Assumption 7)."""

    leaves: list[bytes] = field(default_factory=list)
    _levels: list[list[bytes]] = field(default_factory=list, repr=False)

    def close(self) -> bytes:
        """Build the tree bottom-up. Odd level: duplicate the last node
    (standard convention, same as e.g. Bitcoin's Merkle tree) so every
    level has an even width until the root. The PADDED version of each
    level (not the raw one) is what gets stored in self._levels, so
    proof_for() below can index into it directly with no risk of the
    padding logic drifting out of sync between the two methods."""
        if not self.leaves:
            raise ValueError("cannot close an empty batch")
        level = list(self.leaves)
        levels: list[list[bytes]] = []
        while len(level) > 1:
            if len(level) % 2 == 1:
                level = level + [level[-1]]
            levels.append(level)  # padded level, about to be hashed up
            level = [_hash_pair(level[i], level[i + 1]) for i in range(0, len(level), 2)]
        levels.append(level)  # final level: [root]
        self._levels = levels
        return level[0]

    def proof_for(self, leaf_index: int) -> list[ProofStep]:
        """Inclusion proof for leaf at leaf_index (0-based, into the
    ORIGINAL unpadded self.leaves — padding only ever appends a
    duplicate at the end, so real leaf indices are unaffected by it).
    Requires close() to have been called first."""
        if not self._levels:
            raise ValueError("call close() before requesting a proof")
        if not (0 <= leaf_index < len(self.leaves)):
            raise IndexError(leaf_index)

        steps: list[ProofStep] = []
        idx = leaf_index
        for level in self._levels[:-1]:  # every level except the root
            is_right = idx % 2 == 1
            sibling_idx = idx - 1 if is_right else idx + 1
            sibling = level[sibling_idx]
            # If I am the RIGHT child, my sibling is on my LEFT, and vice versa.
            steps.append(ProofStep(sibling_hash=sibling, position="LEFT" if is_right else "RIGHT"))
            idx //= 2
        return steps

def verify_inclusion_proof(leaf_hash: bytes, proof: list[ProofStep], expected_root: bytes) -> bool:
    """Pure, offline verification (I9): no Core access needed, only
leaf_hash + proof_path + the published root. Tampering with any leaf in
the covered subtree changes every hash on the path to the root, so this
fails for any retroactive edit — this IS the mechanism referenced by
merkle_requirements' 'любое изменение истории должно ломать
верификацию root'."""
    current = leaf_hash
    for step in proof:
        if step.position == "RIGHT":
            current = _hash_pair(current, step.sibling_hash)
        else:
            current = _hash_pair(step.sibling_hash, current)
    return current == expected_root

@dataclass(frozen=True)
class PendingLeaf:
    """One leaf still waiting in an open batch. Carries decision_id — this
is the piece that was missing before and that any fix for the
'MerkleRootPublished scoped to "system"' bug needs: without knowing
which decision_id each leaf belongs to, there is no way to publish a
correctly-scoped event per decision once the batch closes."""
    leaf_hash: bytes
    event_id: str
    decision_id: str

class MerkleBatchBuilder:
    """Stateful accumulator used by the application layer. Two independent
close triggers, per Assumption 4 / merkle_requirements:
- leaf_count >= max_leaves
- elapsed_seconds >= max_seconds since the first leaf in the open batch
Both are checked by the caller (command_handler / a scheduler tick); this
class only tracks whether a trigger condition is currently satisfied.
"""

    def __init__(self, max_leaves: int, max_seconds: float) -> None:
        self.max_leaves = max_leaves
        self.max_seconds = max_seconds
        self._pending: list[PendingLeaf] = []
        self._first_leaf_ts: float | None = None

    def add_leaf(self, leaf_hash: bytes, event_id: str, decision_id: str, now_ts: float) -> None:
        if self._first_leaf_ts is None:
            self._first_leaf_ts = now_ts
        self._pending.append(PendingLeaf(leaf_hash=leaf_hash, event_id=event_id, decision_id=decision_id))

    def should_close(self, now_ts: float) -> bool:
        if not self._pending:
            return False
        if len(self._pending) >= self.max_leaves:
            return True
        if self._first_leaf_ts is not None and (now_ts - self._first_leaf_ts) >= self.max_seconds:
            return True
        return False

    def has_pending(self) -> bool:
        return bool(self._pending)

    def close_and_reset(self) -> tuple[MerkleBatch, bytes, list[PendingLeaf]]:
        pending = self._pending
        batch = MerkleBatch(leaves=[p.leaf_hash for p in pending])
        root = batch.close()
        self._pending = []
        self._first_leaf_ts = None
        return batch, root, pending




