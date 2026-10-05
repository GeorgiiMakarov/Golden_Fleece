
"""ProjectionStore interface — the CQRS read side. Command handlers never read
from this (coding_rules: 'Команды не читают projection'); only the projector
(application/scoreboard_projector.py) writes to it, and only GetScoreboard /
GetMerkleProof read from it (coding_rules: 'Запросы не пишут в Event Store')."""
from __future__ import annotations
from typing import Any, Protocol, runtime_checkable

@runtime_checkable
class ProjectionStore(Protocol):
    async def get(self, key: str) -> dict[str, Any] | None: ...
    async def set(self, key: str, value: dict[str, Any]) -> None: ...
    async def get_many(self, prefix: str) -> dict[str, dict[str, Any]]: ...




