# 10. Shrines

> **Status: Draft.** Describes the reference implementation at commit `8a35e12`. Design rationale:
> [design/shrines.md](../design/shrines.md), [design/websites-l2.md](../design/websites-l2.md).

A **Shrine** is content a node serves at a **Signet** address. A **Pilgrim** is a visitor. It connects with a
**Pilgrimage**: a Conjunction handshake in which the Pilgrim pins the Signet. Then it uses up to four **rites** over the
same Vessel:

| Stream | Rite | Shape |
|---|---|---|
| 4 | Conduit | raw duplex frames under an application protocol id |
| 5 | Oracle | one request → one response |
| 7 | Auspice | named live feed: attend → snapshot → updates → sealed |
| 8 | Relic | manifest + chunk fetch for content past the message ceiling |

Rites on a Pilgrimage are **not** Veil-sealed (contrast [09-rites.md](09-rites.md)). Confidentiality and integrity come
from the Noise transport alone.

## 10.1 Signet and the `cupri1` address

A Signet is an Ed25519 key pair generated for hosting. It MUST be independent of the node's overlay Sigil
([06](06-identity-and-documents.md)), so that hosting reveals nothing about the node's overlay identity.

```
signet_sigil = SHA-256(signet_public_key)            ; 32 bytes
address      = Bech32Encode(hrp = "cupri", data = signet_sigil)
```

- The checksum is **Bech32 as defined in BIP-173** (constant `1`), **not** Bech32m.
- The data is the 32 sigil bytes regrouped from 8-bit to 5-bit groups with zero padding. That gives 52 data
  characters, 6 checksum characters and 64 characters in total, all lowercase.
- The address commits to the *hash* of the key. A Pilgrim learns the key itself during the handshake and checks its hash.

*Reference:* `src/CupriNet.Core/Signet.cs:6-38`, `src/CupriNet.Core/Bech32.cs:13-86`,
`src/CupriNet.Abstractions/Sigil.cs:12-26`.

> **Open (OI-10.1):** decoding is unspecified, and the reference implementation has no decoder. A conforming parser
> needs rules for mixed case, the 90-character limit, non-zero padding bits and a wrong HRP. Proposal: follow
> BIP-173 decoding exactly, reject non-zero padding, and require the HRP `cupri`.

## 10.2 Pilgrimage handshake

All handshake traffic is on stream 0. In order:

1. **Toll** (if the host requires it): see [04-conjunction.md §Toll](04-conjunction.md).
2. **Noise_XX:** see [04-conjunction.md](04-conjunction.md). The Pilgrim is the initiator.
3. **Identity binding:** see [04-conjunction.md](04-conjunction.md), with the Shrine-specific rules below.
4. **Rites:** the Vessel is demultiplexed by stream id.

The whole handshake (steps 1–3) SHOULD be bounded by a single timeout. The reference value is 30 s on both sides.
*Reference:* `src/CupriNet.Shrine/Pilgrimage.cs:22,49-50`, `src/CupriNet.Hosting/CupriNode.cs:29`.

### 10.2.1 Transport admission

Before the handshake, both sides MUST refuse a transport that reports a maximum message size below
**237,568 bytes**, which is the largest padded Auspice frame (229,376) plus 8,192 bytes of overhead. A transport with
no declared limit is admitted. *Reference:* `src/CupriNet.Rites/RiteTransport.cs:15,31-45`.

### 10.2.2 Pilgrim identity

The Pilgrim MUST generate a fresh Ed25519 identity for each visit and MUST NOT reuse its overlay identity. That
throwaway public key is sent to the host in the binding. *Reference:* `src/CupriNet.Shrine/Pilgrimage.cs:55`.

### 10.2.3 Blinded Shrine target

One host can serve several Shrines on one endpoint. The Pilgrim names the Shrine it wants without revealing the
Signet to a passive observer. It places a **target tag** in the trailing `targetTag` field of its identity binding:

```
target_tag = HKDF-SHA256(ikm  = signet_sigil,          ; 32 bytes
                         salt = handshake_hash,        ; Noise h after msg 3
                         info = "cuprinet:shrine-target:v1",
                         L    = 32)
```

The target tag is **not** covered by the binding signature.

The host:

1. computes `target_tag` for **every** hosted Signet and compares each with a constant-time comparison. It MUST NOT
   stop at the first match, so that timing does not reveal which Shrine was requested;
2. presents the matching Signet as its identity in its own binding (with no target tag);
3. if nothing matches, sends a **refusal** (see 04) with reason `"no such Shrine at this endpoint"` and closes.

If the tag is **empty**, the host presents its first-hosted ("primary") Shrine.

The Pilgrim MUST then check that `SHA-256(host binding public key) == signet_sigil`. This check is what pins the
Shrine: if any other key answers, the visit fails.

*Reference:* `src/CupriNet.Conjunction/NoiseConjunction.cs:40,56-60,134-188`,
`src/CupriNet.Hosting/CupriNode.Shrine.cs:113-134`.

> **Open (OI-10.2):** is an empty target tag normative or legacy behaviour? The reference Pilgrim always sends a tag.

## 10.3 Common rite rules

- Every frame below is the payload of one Vessel frame on the rite's stream.
- Version bytes are checked with CupriMark acceptance ([05](05-versioning.md)). Every rite is currently at version 1.
- A receiver that cannot decode a rite frame currently tears down **the whole visit**, not only that rite
  (`CupriNode.Shrine.cs:432-471`). **Open (OI-10.3):** should a malformed frame on one rite be isolated to that rite?
- No rite has acknowledgements. The Vessel multiplexer may drop frames when a stream's queue is full
  ([03](03-transport.md)). The Relic detects loss with hashes. Oracle and Conduit do not detect loss.

### 10.3.1 Padding

Auspice and Conduit frames on a Pilgrimage are padded with zero bytes after the encoded frame. This hides the
plaintext length from the transport layer:

- block mode (default): round up to a multiple of 512 bytes;
- power-of-two mode: round up to the next power of two, with a minimum of 256 bytes;
- a padded frame MUST NOT exceed 229,376 bytes (224 KiB).

A receiver MUST ignore bytes after the last decoded field. *Reference:* `src/CupriNet.Rites/RitePadding.cs:66-81`,
`src/CupriNet.Rites/Auspice.cs:78,147-190`.

> **Open (OI-01.4):** trailing bytes are ignored in *every* structure, not only padded ones. See 01-encoding.

### 10.3.2 Message ceiling

The largest body, payload or chunk any Pilgrimage rite carries is **196,608 bytes (192 KiB)**. This keeps every frame
under the WebRTC DataChannel limit ([design/transports-and-limits.md](../design/transports-and-limits.md)). Content
larger than this MUST be carried as a Relic (§10.6).

## 10.4 Oracle: request/response (stream 5)

### Envelope

| Field | Type | Notes |
|---|---|---|
| frameType | `u8` | `1` = Request, `2` = Response |
| body | `bytes<len>` | a Request or Response, below |

An unknown `frameType` is a decode error. Oracle frames are **not** padded.

### Request

| Field | Type |
|---|---|
| version | `u8` (= 1) |
| method | `string` |
| path | `string` |
| headerCount | `varuint` (≤ 4,096) |
| headers | `headerCount ×` (`string` name, `string` value) |
| body | `bytes<len>` (≤ 196,608) |

### Response

| Field | Type |
|---|---|
| version | `u8` (= 1) |
| status | `u32` (big-endian) |
| headerCount, headers | as in Request |
| body | `bytes<len>` (≤ 196,608) |

### Semantics

- Exchanges are strictly sequential: one Request, then one Response, with no request id. A Pilgrim MUST NOT send a
  second Request before reading the Response to the first.
- Header names compare case-insensitively. With duplicate names, the first occurrence wins.
- Status codes follow HTTP semantics. The reference host emits `200`, `404`, `405` (static handler: non-GET/HEAD) and
  `500` (handler threw, or its body was over the ceiling).
- The host ignores frames that are not Requests.

*Reference:* `src/CupriNet.Rites/Oracle.cs:111-265`, `src/CupriNet.Rites/OracleSession.cs:357-396`,
`src/CupriNet.Rites/OracleHandler.cs:448-472`.

> **Open (OI-10.4):** there are no limits on method, path or header lengths, so a frame can exceed the transport
> ceiling even when its body is within the limit. The status range is unconstrained. Pipelining is impossible
> without a request id.

## 10.5 Auspice: live feeds (stream 7)

Wire form: `bytes<len>(padded AuspiceFrame)`. The length prefix comes before the padded bytes.

| Field | Type | Notes |
|---|---|---|
| version | `u8` (= 1) | |
| kind | `u8` | `1` Attend · `2` Depart · `3` Snapshot · `4` Update · `5` Sealed |
| topic | `string` | 1–128 characters |
| payload | `bytes<len>` | ≤ 196,608 |

Directions: the Pilgrim sends Attend and Depart; the Shrine sends Snapshot, Update and Sealed. A receiver MUST ignore
kinds that are not valid in its direction.

Host behaviour:

| Event | Host sends |
|---|---|
| Attend an unknown topic | Sealed, payload `"no such feed"` |
| Attend while 32 feeds are already live | Sealed, `"too many feeds"` |
| Attend a topic already attended | nothing (ignored) |
| Depart | nothing; the feed stops |
| Feed source ends | Sealed, `"ended"` |
| Feed source fails | Sealed, `"failed"` |

The Sealed payload is a UTF-8 reason. After a Sealed frame the topic is closed. The intended order is Snapshot first,
then Updates, but the protocol does not enforce it.

*Reference:* `src/CupriNet.Rites/Auspice.cs:20-123`, `src/CupriNet.Rites/AuspiceSession.cs:263-308`,
`src/CupriNet.Rites/AuspiceSource.cs:382-473`.

> **Open (OI-10.5):** topic length is counted in UTF-16 code units, which is implementation-specific. It should be
> counted in UTF-8 bytes or code points. Should Depart be acknowledged with a Sealed frame?

## 10.6 Relic: bulk content (stream 8)

Wire form: a bare RelicFrame. It has no outer length prefix and no padding.

| Field | Type | Notes |
|---|---|---|
| version | `u8` (= 1) | |
| kind | `u8` | `1` RequestManifest · `2` RequestChunk · `3` Manifest · `4` Chunk · `5` Refused |
| name | `string` | 1–256 characters |
| fileIndex | `varuint` | < 1,024 |
| chunkIndex | `varuint` | < 1,048,576 |
| payload | `bytes<len>` | ≤ 196,608. A Manifest frame carries a Reliquary manifest ([09](09-rites.md)); a Chunk frame carries chunk bytes; a Refused frame carries a UTF-8 reason |

Flow (strictly sequential, one request outstanding, answers matched by `name`):

1. The Pilgrim sends RequestManifest(name). The host replies Manifest or Refused.
2. The Pilgrim MUST refuse a manifest whose `chunkSize` exceeds 196,608.
3. For each chunk `i` in order, the Pilgrim sends RequestChunk(name, fileIndex, i). The host replies Chunk or Refused
   with the same indices.
4. The Pilgrim MUST check each chunk's length and SHA-256 against the manifest. It MUST check the whole-file SHA-256
   before releasing any bytes.

Host refusal reasons: `"no such relic"`, `"manifest too large"`, `"no such chunk"`, `"chunk too large"`, `"failed"`.
A Refused frame echoes the request's `fileIndex` and `chunkIndex`.

Because the manifest must fit in one 192 KiB frame, a single relic is limited to about 1.1 GiB at the default chunk size.

*Reference:* `src/CupriNet.Rites/Relic.cs:51-154`, `src/CupriNet.Rites/RelicSession.cs:175-301`,
`src/CupriNet.Rites/RelicSource.cs:345-477`.

> **Resolved (OI-10.6):** the manifest decoder now validates every path (09 §9.6). A Pilgrim does not ask for an
> author signature on the manifest, by design: the Signet pinned in the handshake already authenticates the host.
> Its default client cap of 256 MiB is local policy.

## 10.7 Conduit: raw duplex (stream 4)

Pilgrimage wire form: `padded( u8 conduitVersion (= 1) || ConduitFrame )`. It has no outer length prefix.

| Field | Type | Notes |
|---|---|---|
| protocolId | `u32` | chosen by the application; the rite never dispatches on it |
| schemaVersion | `u32` | chosen by the application |
| flags | `u32` | bit 31 (`0x80000000`) = **Sealed** (reserved); bits 0–30 belong to the application |
| payload | `bytes<len>` | ≤ 196,608 on a Pilgrimage |
| author | AuthorEnvelope | see [09-rites.md](09-rites.md); not verified on a Pilgrimage |

A Sealed frame has `protocolId = schemaVersion = 0` and a UTF-8 reason as payload. After receiving Sealed, a side MUST
treat the conduit as closed for reading.

Host behaviour: with no conduit handler hosted, the host waits for the first frame and replies Sealed
`"no conduit hosted"`. When the handler returns, the host sends Sealed `"ended"`; when it throws, Sealed `"failed"`.
After sealing, the host keeps draining the stream so that the other rites stay alive.

The Conduit used on an Arcanum channel is a **different wire format**: no version byte, Veil-sealed, and a larger
payload limit. See [09-rites.md](09-rites.md).

*Reference:* `src/CupriNet.Rites/Conduit.cs:118-470`, `src/CupriNet.Rites/ConduitHandler.cs:531-580`.

## 10.8 Shrine addresses in Intonations

An Intonation ([06](06-identity-and-documents.md)) MAY carry **one** Shrine address block:

| Field | Type |
|---|---|
| version | `u8` (= 1) |
| signetSigil | `bytes<len>` (length MUST be 32) |

If the WebRTC block before it is absent, its slot holds the sentinel byte `0`. *Reference:*
`src/CupriNet.Core/ShrineAddressCodec.cs:157-179`.

## 10.9 Host policy (informative)

These values do not affect interoperability. They are RECOMMENDED defaults:

| Policy | Reference default |
|---|---|
| Handshake timeout (Toll + Noise + binding) | 30 s |
| Idle timeout (no traffic in either direction) | 5 min |
| Concurrent Pilgrimages per host | 256 |
| Concurrent Pilgrimages per remote IP | 8 |
| Live Auspice feeds per visit | 32 |
| Subnet allow/deny fence | applied before the handshake; see [11](11-traversal.md) |

> **Resolved (OI-10.7):** browser (WebRTC) Pilgrimages used to bypass the concurrency caps. They now draw on the same
> visit budget and per-address share as the Shrine port, and the Toll cookie is bound to the browser's address.
