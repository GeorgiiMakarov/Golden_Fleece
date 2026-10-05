"""RFC 3161 Time-Stamp Protocol client (pure stdlib).

Implements the ``TimestampAuthority`` Protocol from ``interfaces/tsa.py``
against any RFC 3161 TSA speaking HTTP (e.g. https://freetsa.org/tsr,
or a national TSA such as NUTS RK once its endpoint contract is known).

Only the Python standard library is used (``hashlib``, ``urllib``,
``asyncio``, ``secrets``, ``datetime``): the DER codec below is handwritten
and intentionally minimal -- it covers exactly the ASN.1 subset that
RFC 3161 TimeStampReq / TimeStampResp need (SEQUENCE, SET, INTEGER,
OCTET STRING, OBJECT IDENTIFIER, NULL, BOOLEAN, GeneralizedTime,
plus context-specific [0]/[1] wrappers).

LIMITATIONS (documented honestly):
  * The CMS signature inside SignedData (signerInfos / certificates) is
    NOT verified. ``verify()`` checks the PKIStatus and that the
    messageImprint matches the digest -- i.e. structural integrity of the
    response -- but not the TSA's cryptographic signature. Full chain
    validation requires a CMS/X.509 stack (e.g. asn1crypto + certifi) and
    is out of scope for this stdlib-only client.
  * Only HTTP(S) POST transport is implemented (the standard TSP
    binding); TCP-socket transport from RFC 3161 Appendix A is not.
  * ``hash_name`` is limited to sha1/sha256/sha384/sha512 (OID map below).
"""

from __future__ import annotations

import asyncio
import base64
import datetime as dt
import hashlib
import http.client
import os
import re
import secrets
from urllib.parse import unquote, urlsplit
from typing import Optional


# ---------------------------------------------------------------------------
# Errors
# ---------------------------------------------------------------------------

class DerError(Exception):
    """Raised when DER bytes cannot be parsed or violate expectations."""


class TsaError(Exception):
    """Raised when the TSA reports a failure or its response is invalid."""


# ---------------------------------------------------------------------------
# Minimal DER codec
# ---------------------------------------------------------------------------

# Universal tag numbers we support.
_TAG_BOOLEAN = 0x01
_TAG_INTEGER = 0x02
_TAG_BIT_STRING = 0x03
_TAG_OCTET_STRING = 0x04
_TAG_NULL = 0x05
_TAG_OID = 0x06
_TAG_UTF8STRING = 0x0C
_TAG_SEQUENCE = 0x10
_TAG_SET = 0x11
_TAG_GENERALIZED_TIME = 0x18

_CLASS_UNIVERSAL = 0
_CLASS_CONTEXT = 2

_HASH_OIDS = {
    "sha1": "1.3.14.3.2.26",
    "sha256": "2.16.840.1.101.3.4.2.1",
    "sha384": "2.16.840.1.101.3.4.2.2",
    "sha512": "2.16.840.1.101.3.4.2.3",
}

OID_SIGNED_DATA = "1.2.840.113549.1.7.2"
OID_TST_INFO = "1.2.840.113549.1.9.16.1.4"

_PKI_STATUS_NAMES = {
    0: "granted",
    1: "grantedWithMods",
    2: "rejection",
    3: "waiting",
    4: "revocationWarning",
    5: "revocationNotification",
}

_GENERALIZED_TIME_RE = re.compile(
    r"^(\d{4})(\d{2})(\d{2})(\d{2})(\d{2})(\d{2})(?:\.(\d+))?(Z|[+-]\d{4})$"
)


def _der_length(n: int) -> bytes:
    if n < 0:
        raise DerError("negative length")
    if n < 0x80:
        return bytes([n])
    raw = n.to_bytes((n.bit_length() + 7) // 8, "big")
    return bytes([0x80 | len(raw)]) + raw


def _tlv(tag: int, content: bytes) -> bytes:
    return bytes([tag]) + _der_length(len(content)) + content


def der_boolean(value: bool) -> bytes:
    return _tlv(_TAG_BOOLEAN, b"\xff" if value else b"\x00")


def der_integer(value: int) -> bytes:
    if value == 0:
        return _tlv(_TAG_INTEGER, b"\x00")
    if value < 0:
        raise DerError("only non-negative INTEGER supported")
    raw = value.to_bytes((value.bit_length() + 7) // 8, "big")
    if raw[0] & 0x80:
        raw = b"\x00" + raw
    return _tlv(_TAG_INTEGER, raw)


def der_octet_string(data: bytes) -> bytes:
    return _tlv(_TAG_OCTET_STRING, data)


def der_null() -> bytes:
    return _tlv(_TAG_NULL, b"")


def der_oid(dotted: str) -> bytes:
    arcs = [int(a) for a in dotted.split(".")]
    if len(arcs) < 2 or arcs[0] > 2 or arcs[1] > 39:
        raise DerError(f"bad OID: {dotted!r}")
    out = bytearray()
    out.append(40 * arcs[0] + arcs[1])
    for arc in arcs[2:]:
        if arc < 0:
            raise DerError(f"bad OID arc: {dotted!r}")
        stack = bytearray([arc & 0x7F])
        arc >>= 7
        while arc:
            stack.append((arc & 0x7F) | 0x80)
            arc >>= 7
        out.extend(reversed(stack))
    # DER: minimal encoding already guaranteed by construction above.
    return _tlv(_TAG_OID, bytes(out))


def der_generalized_time(value: str) -> bytes:
    # Caller supplies e.g. "20260923T..."? No -- plain GeneralizedTime text.
    return _tlv(_TAG_GENERALIZED_TIME, value.encode("ascii"))


def der_sequence(*children: bytes) -> bytes:
    return _tlv(0x30, b"".join(children))


def der_set(*children: bytes) -> bytes:
    return _tlv(0x31, b"".join(children))


def der_explicit(tag_no: int, inner: bytes) -> bytes:
    """Context-specific constructed [tag_no] EXPLICIT wrapper ([0]/[1])."""
    if not 0 <= tag_no <= 30:
        raise DerError("context tag number out of range")
    return _tlv(0xA0 | tag_no, inner)


class DerNode:
    """One parsed TLV. Primitive nodes carry ``content``; constructed nodes
    carry ``children`` (parsed from content)."""

    __slots__ = ("tag_class", "tag_number", "constructed", "content", "children")

    def __init__(self, tag_class: int, tag_number: int, constructed: bool,
                 content: bytes, children: list["DerNode"]) -> None:
        self.tag_class = tag_class
        self.tag_number = tag_number
        self.constructed = constructed
        self.content = content
        self.children = children

    def __repr__(self) -> str:  # pragma: no cover - debugging aid
        return (f"DerNode(class={self.tag_class}, tag={self.tag_number}, "
                f"constructed={self.constructed}, len={len(self.content)})")


def _parse_tlv(data: bytes, off: int) -> tuple[DerNode, int]:
    if off + 2 > len(data):
        raise DerError("truncated TLV header")
    tag_byte = data[off]
    off += 1
    tag_class = (tag_byte >> 6) & 0x03
    constructed = bool(tag_byte & 0x20)
    tag_number = tag_byte & 0x1F
    if tag_number == 0x1F:
        raise DerError("multi-byte tag numbers not supported")
    first_len = data[off]
    off += 1
    if first_len & 0x80:
        nbytes = first_len & 0x7F
        if nbytes == 0 or off + nbytes > len(data):
            raise DerError("bad DER length")
        length = int.from_bytes(data[off:off + nbytes], "big")
        off += nbytes
    else:
        length = first_len
    if off + length > len(data):
        raise DerError("truncated TLV content")
    content = data[off:off + length]
    off += length
    children: list[DerNode] = []
    if constructed:
        pos = 0
        while pos < len(content):
            child, pos = _parse_tlv(content, pos)
            children.append(child)
    return DerNode(tag_class, tag_number, constructed, content, children), off


def der_decode(data: bytes) -> DerNode:
    """Parse exactly one top-level TLV; trailing bytes are an error."""
    node, off = _parse_tlv(data, 0)
    if off != len(data):
        raise DerError("trailing bytes after top-level TLV")
    return node


def _expect_universal(node: DerNode, tag_number: int, what: str) -> DerNode:
    if not (node.tag_class == _CLASS_UNIVERSAL and node.tag_number == tag_number):
        raise DerError(f"expected {what}, got class={node.tag_class} tag={node.tag_number}")
    return node


def _expect_context(node: DerNode, tag_number: int, what: str) -> DerNode:
    if not (node.tag_class == _CLASS_CONTEXT and node.tag_number == tag_number
            and node.constructed):
        raise DerError(f"expected {what}, got class={node.tag_class} "
                       f"tag={node.tag_number} constructed={node.constructed}")
    return node


def der_decode_integer(node: DerNode) -> int:
    _expect_universal(node, _TAG_INTEGER, "INTEGER")
    if node.constructed or not node.content:
        raise DerError("bad INTEGER")
    raw = node.content
    value = int.from_bytes(raw, "big")
    if raw[0] & 0x80:  # negative two's complement (not expected from TSAs)
        value -= 1 << (8 * len(raw))
    return value


def der_decode_oid(node: DerNode) -> str:
    _expect_universal(node, _TAG_OID, "OBJECT IDENTIFIER")
    content = node.content
    if not content:
        raise DerError("empty OID")
    first = content[0]
    arcs = [first // 40, first % 40]
    val = 0
    for byte in content[1:]:
        val = (val << 7) | (byte & 0x7F)
        if not byte & 0x80:
            arcs.append(val)
            val = 0
    if val or content[-1] & 0x80:
        raise DerError("truncated OID")
    return ".".join(str(a) for a in arcs)


def der_decode_octet_string(node: DerNode) -> bytes:
    _expect_universal(node, _TAG_OCTET_STRING, "OCTET STRING")
    if node.constructed:
        raise DerError("constructed OCTET STRING not supported")
    return node.content


def der_decode_generalized_time(node: DerNode) -> str:
    _expect_universal(node, _TAG_GENERALIZED_TIME, "GeneralizedTime")
    try:
        return node.content.decode("ascii")
    except UnicodeDecodeError as e:
        raise DerError("non-ASCII GeneralizedTime") from e


def generalized_time_to_iso(gt: str) -> str:
    """'20260923102030Z' -> '2026-09-23T10:20:30+00:00'."""
    m = _GENERALIZED_TIME_RE.match(gt)
    if not m:
        raise DerError(f"unsupported GeneralizedTime format: {gt!r}")
    y, mo, d, h, mi, s, frac, tz = m.groups()
    micro = int((frac or "0")[:6].ljust(6, "0"))
    if tz == "Z":
        tzinfo = dt.timezone.utc
    else:
        sign = 1 if tz[0] == "+" else -1
        tzinfo = dt.timezone(sign * dt.timedelta(hours=int(tz[1:3]), minutes=int(tz[3:5])))
    return dt.datetime(int(y), int(mo), int(d), int(h), int(mi), int(s),
                       micro, tzinfo=tzinfo).isoformat()


# ---------------------------------------------------------------------------
# RFC 3161 structures
# ---------------------------------------------------------------------------

def _algorithm_identifier(hash_oid: str) -> bytes:
    # AlgorithmIdentifier ::= SEQUENCE { algorithm OID, parameters NULL }
    return der_sequence(der_oid(hash_oid), der_null())


def _message_imprint(hash_oid: str, digest: bytes) -> bytes:
    return der_sequence(_algorithm_identifier(hash_oid), der_octet_string(digest))


def build_timestamp_req(hash_oid: str, digest: bytes, nonce: int) -> bytes:
    """TimeStampReq ::= SEQUENCE {
        version INTEGER {v1(1)}, messageImprint, nonce INTEGER,
        certReq BOOLEAN DEFAULT FALSE }"""
    return der_sequence(
        der_integer(1),
        _message_imprint(hash_oid, digest),
        der_integer(nonce),
        der_boolean(False),
    )


class _ParsedTstInfo:
    __slots__ = ("gen_time", "hashed_message", "nonce")

    def __init__(self, gen_time: str, hashed_message: bytes,
                 nonce: Optional[int]) -> None:
        self.gen_time = gen_time
        self.hashed_message = hashed_message
        self.nonce = nonce


def _parse_tst_info(node: DerNode) -> _ParsedTstInfo:
    _expect_universal(node, _TAG_SEQUENCE, "TSTInfo")
    ch = node.children
    if len(ch) < 5:
        raise DerError("TSTInfo too short")
    # version INTEGER, policy OID, messageImprint, serialNumber, genTime, ...
    _expect_universal(ch[1], _TAG_OID, "TSTInfo.policy")
    mi = _expect_universal(ch[2], _TAG_SEQUENCE, "TSTInfo.messageImprint")
    if len(mi.children) != 2:
        raise DerError("bad MessageImprint")
    _expect_universal(mi.children[0], _TAG_SEQUENCE, "MessageImprint.hashAlgorithm")
    hashed = der_decode_octet_string(mi.children[1])
    gen_time = der_decode_generalized_time(_expect_universal(ch[4], _TAG_GENERALIZED_TIME,
                                                            "TSTInfo.genTime"))
    nonce: Optional[int] = None
    # Remaining fields: [accuracy SEQ] [ordering BOOL] [nonce INTEGER]
    # [[0] tsa] [[1] extensions]; nonce is the first universal INTEGER.
    for extra in ch[5:]:
        if extra.tag_class == _CLASS_UNIVERSAL and extra.tag_number == _TAG_INTEGER:
            nonce = der_decode_integer(extra)
            break
    return _ParsedTstInfo(gen_time, hashed, nonce)


def _parse_pki_status_info(node: DerNode) -> tuple[int, str]:
    _expect_universal(node, _TAG_SEQUENCE, "PKIStatusInfo")
    ch = node.children
    if not ch:
        raise DerError("empty PKIStatusInfo")
    status = der_decode_integer(_expect_universal(ch[0], _TAG_INTEGER, "PKIStatus"))
    detail = ""
    if len(ch) > 1 and ch[1].tag_class == _CLASS_UNIVERSAL and ch[1].tag_number == _TAG_SEQUENCE:
        # statusString: SEQUENCE OF UTF8String
        parts = []
        for s in ch[1].children:
            try:
                parts.append(s.content.decode("utf-8"))
            except UnicodeDecodeError:
                parts.append(repr(s.content))
        detail = "; ".join(parts)
    return status, detail


class _ParsedResponse:
    __slots__ = ("status", "status_text", "tst_info")

    def __init__(self, status: int, status_text: str,
                 tst_info: Optional[_ParsedTstInfo]) -> None:
        self.status = status
        self.status_text = status_text
        self.tst_info = tst_info


def parse_timestamp_resp(data: bytes) -> _ParsedResponse:
    """Parse TimeStampResp; raises DerError on malformed input."""
    root = _expect_universal(der_decode(data), _TAG_SEQUENCE, "TimeStampResp")
    ch = root.children
    if not ch:
        raise DerError("empty TimeStampResp")
    status, detail = _parse_pki_status_info(ch[0])
    tst_info: Optional[_ParsedTstInfo] = None
    if len(ch) > 1:
        # ContentInfo ::= SEQUENCE { contentType OID, content [0] EXPLICIT }
        ci = _expect_universal(ch[1], _TAG_SEQUENCE, "ContentInfo")
        if len(ci.children) != 2:
            raise DerError("bad ContentInfo")
        ctype = der_decode_oid(_expect_universal(ci.children[0], _TAG_OID,
                                                 "ContentInfo.contentType"))
        if ctype != OID_SIGNED_DATA:
            raise DerError(f"unexpected ContentInfo type {ctype}")
        content = _expect_context(ci.children[1], 0, "ContentInfo.content")
        if not content.children:
            raise DerError("empty ContentInfo.content")
        signed_data = _expect_universal(content.children[0], _TAG_SEQUENCE, "SignedData")
        sd = signed_data.children
        if len(sd) < 3:
            raise DerError("SignedData too short")
        # encapContentInfo ::= SEQUENCE { eContentType OID, eContent [0] EXPLICIT }
        eci = _expect_universal(sd[2], _TAG_SEQUENCE, "EncapsulatedContentInfo")
        if len(eci.children) != 2:
            raise DerError("bad EncapsulatedContentInfo")
        econtent_type = der_decode_oid(_expect_universal(eci.children[0], _TAG_OID,
                                                         "eContentType"))
        if econtent_type != OID_TST_INFO:
            raise DerError(f"unexpected eContentType {econtent_type}")
        econtent = _expect_context(eci.children[1], 0, "eContent")
        if not econtent.children:
            raise DerError("empty eContent")
        tst_der = der_decode_octet_string(econtent.children[0])
        tst_info = _parse_tst_info(_expect_universal(der_decode(tst_der),
                                                     _TAG_SEQUENCE, "TSTInfo"))
    name = _PKI_STATUS_NAMES.get(status, f"unknown({status})")
    status_text = f"PKIStatus={status} ({name})" + (f": {detail}" if detail else "")
    return _ParsedResponse(status, status_text, tst_info)


# ---------------------------------------------------------------------------
# Client
# ---------------------------------------------------------------------------

# Local import kept lazy to avoid a hard dependency cycle at import time;
# interfaces/tsa.py has no imports of its own, so a top-level import is safe,
# but tests may import this module standalone.
try:  # pragma: no cover - importable in both layouts
    from interfaces.tsa import TsaToken
except ImportError:  # pragma: no cover
    from tsa import TsaToken  # type: ignore[no-redef]


class Rfc3161TsaClient:
    """RFC 3161 TSA client over HTTP (pure stdlib), structurally implementing
    the ``TimestampAuthority`` Protocol from ``interfaces/tsa.py``.

    Known limitation: the CMS signature inside SignedData is NOT
    cryptographically verified (see module docstring).
    """

    def __init__(self, endpoint_url: str, authority_id: str = "",
                 hash_name: str = "sha256", timeout_s: float = 10.0) -> None:
        try:
            digest_size = hashlib.new(hash_name).digest_size
        except (ValueError, TypeError) as e:
            raise ValueError(f"unsupported hash_name: {hash_name!r}") from e
        if hash_name not in _HASH_OIDS:
            raise ValueError(f"no OID mapping for hash_name: {hash_name!r}")
        self._endpoint_url = endpoint_url
        self._authority_id = authority_id
        self._hash_name = hash_name
        self._hash_oid = _HASH_OIDS[hash_name]
        self._digest_size = digest_size
        self._timeout_s = timeout_s

    # -- request ---------------------------------------------------------
    def build_request(self, digest: bytes) -> tuple[bytes, int]:
        """Build a TimeStampReq. Returns (der_bytes, nonce)."""
        if len(digest) != self._digest_size:
            raise ValueError(
                f"digest must be {self._digest_size} bytes for {self._hash_name}, "
                f"got {len(digest)}")
        nonce = secrets.randbits(64)
        while nonce == 0:
            nonce = secrets.randbits(64)
        return build_timestamp_req(self._hash_oid, digest, nonce), nonce

    # -- transport --------------------------------------------------------
    @staticmethod
    def _proxy_for(scheme: str, host: str) -> "tuple[str, int, str | None] | None":
        """Return (proxy_host, proxy_port, basic_auth) for the target, or None.

        Honors the standard *proxy / no_proxy environment variables. Uses an
        explicit CONNECT tunnel via http.client rather than
        urllib.request.urlopen: on some egress proxies urllib's tunnel
        handshake is rejected while an explicit Proxy-Authorization tunnel
        works.
        """
        proxy_url = os.environ.get(f"{scheme}_proxy") or os.environ.get(f"{scheme.upper()}_PROXY")
        if not proxy_url:
            return None
        no_proxy = os.environ.get("no_proxy") or os.environ.get("NO_PROXY") or ""
        host_l = host.lower().lstrip(".")
        for entry in (e.strip().lower().lstrip(".") for e in no_proxy.split(",")):
            if entry and (host_l == entry or host_l.endswith("." + entry)):
                return None
        parts = urlsplit(proxy_url)
        if not parts.hostname:
            return None
        auth = None
        if parts.username:
            creds = unquote(parts.username)
            if parts.password:
                creds += ":" + unquote(parts.password)
            auth = "Basic " + base64.b64encode(creds.encode()).decode()
        return parts.hostname, parts.port or 8080, auth

    def _post(self, body: bytes) -> bytes:
        parts = urlsplit(self._endpoint_url)
        if parts.scheme not in ("http", "https"):
            raise TsaError(f"unsupported TSA URL scheme: {parts.scheme!r}")
        host, port = parts.hostname or "", parts.port or (443 if parts.scheme == "https" else 80)
        path = parts.path or "/"
        if parts.query:
            path += "?" + parts.query
        headers = {
            "Content-Type": "application/timestamp-query",
            "Accept": "application/timestamp-reply",
        }
        try:
            proxy = self._proxy_for(parts.scheme, host)
            if proxy is not None:
                proxy_host, proxy_port, proxy_auth = proxy
                if parts.scheme == "https":
                    conn: http.client.HTTPConnection = http.client.HTTPSConnection(
                        proxy_host, proxy_port, timeout=self._timeout_s)
                    tunnel_headers = {"Host": host if port == 443 else f"{host}:{port}"}
                    if proxy_auth:
                        tunnel_headers["Proxy-Authorization"] = proxy_auth
                    conn.set_tunnel(host, port, tunnel_headers)
                    conn.request("POST", path, body=body, headers=headers)
                else:
                    conn = http.client.HTTPConnection(proxy_host, proxy_port,
                                                      timeout=self._timeout_s)
                    if proxy_auth:
                        headers["Proxy-Authorization"] = proxy_auth
                    conn.request("POST", self._endpoint_url, body=body, headers=headers)
            else:
                conn = (http.client.HTTPSConnection(host, port, timeout=self._timeout_s)
                        if parts.scheme == "https"
                        else http.client.HTTPConnection(host, port, timeout=self._timeout_s))
                conn.request("POST", path, body=body, headers=headers)
            resp = conn.getresponse()
            raw = resp.read()
            status = resp.status
            conn.close()
        except (TimeoutError, OSError, http.client.HTTPException) as e:
            raise TsaError(f"TSA transport error: {e}") from e
        if status != 200:
            raise TsaError(f"TSA HTTP error {status}")
        return raw

    # -- Protocol ----------------------------------------------------------
    async def timestamp(self, digest: bytes) -> TsaToken:
        """Request a timestamp token from the TSA (network I/O)."""
        req_der, nonce = self.build_request(digest)
        raw = await asyncio.to_thread(self._post, req_der)
        try:
            parsed = parse_timestamp_resp(raw)
        except DerError as e:
            raise TsaError(f"malformed TimeStampResp: {e}") from e
        if parsed.status not in (0, 1):
            raise TsaError(f"TSA did not grant timestamp: {parsed.status_text}")
        info = parsed.tst_info
        if info is None:
            raise TsaError("granted TimeStampResp carries no TimeStampToken")
        if info.hashed_message != digest:
            raise TsaError("response messageImprint does not match request digest")
        if info.nonce is None:
            raise TsaError("response TSTInfo is missing the requested nonce")
        if info.nonce != nonce:
            raise TsaError("response nonce does not match request nonce")
        try:
            timestamp_iso = generalized_time_to_iso(info.gen_time)
        except DerError as e:
            raise TsaError(f"bad genTime in TSTInfo: {e}") from e
        return TsaToken(token_bytes=raw, timestamp_iso=timestamp_iso,
                        authority_id=self._authority_id)

    def verify(self, digest: bytes, token: TsaToken) -> bool:
        """Offline check: True iff the token parses as a TimeStampResp with
        PKIStatus granted(0)/grantedWithMods(1) and a messageImprint equal to
        ``digest``. Never raises on malformed input -- returns False."""
        try:
            parsed = parse_timestamp_resp(token.token_bytes)
        except Exception:
            return False
        if parsed.status not in (0, 1):
            return False
        if parsed.tst_info is None:
            return False
        return parsed.tst_info.hashed_message == digest
