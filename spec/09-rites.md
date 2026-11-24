# 9. Channel rites

> **Status: Draft.** Describes the reference implementation at commit `8a35e12`. Rites on a **Pilgrimage** are
> specified in [10-shrines.md](10-shrines.md). This document covers rites on an **Arcanum channel**, plus the parts
> shared by both.

A rite is selected by its Vessel stream id ([03 §3.3.1](03-transport.md)). There is no rite-id byte.

## 9.1 Veil (channel-layer seal)

Every channel rite frame is sealed under the Consecration session key ([08 §8.4.3](08-channels.md)), inside the Noise
transport:

```
veil(pt) = nonce (12 random bytes) ‖ ChaCha20-Poly1305(key = session_key, nonce, ad = "", pt)
```

A receiver MUST reject input shorter than 28 bytes and any input that fails authentication.

*Reference:* `src/CupriNet.Rites/VeilCipher.cs:26-43`.

> **Open (OI-09.1):** the Veil uses random nonces under one key shared in both directions. It has no AD, so it does not
> authenticate a rite's outer type byte or the stream id. It has no replay protection of its own; it relies on Noise.
> It has no usage limit or rekey.

## 9.2 Author envelope

Some rite frames carry an optional signature by the sender's channel **persona**. A persona is a per-channel identity
that can differ from the overlay Seal.

| Field | Type |
|---|---|
| present | `u8`: `0` absent, `1` present; any other value is malformed |
| publicKey | `bytes<len>` (present only) |
| signature | `bytes<len>` (present only) |

```
digest    = SHA-256( "cuprinet/rite/" ‖ domain ‖ 0x00 ‖ content )
signature = Ed25519(persona, digest)
```

`content` is the frame's encoding with the envelope **absent**, so it ends in `0x00`. `domain` is one of
`epistle`, `conduit` or `reliquary`. A node configured to require signed authors (the reference default) MUST reject
frames that have no envelope.

*Reference:* `src/CupriNet.Rites/RiteAuthor.cs:12-100`.

> **Open (OI-09.2):** verification runs over a re-encoding, so a signed frame stays valid even if an attacker alters
> its trailing bytes or uses a non-minimal varint encoding.

## 9.3 Epistle (message), stream 3

Each frame is `u8 type ‖ bytes<len>(veil(body))`:

| type | body |
|---|---|
| `1` Message | an Epistle, below |
| `2` Attestation | the acknowledged message's `messageId`, raw |

**Epistle body**

| Field | Type | Notes |
|---|---|---|
| messageId | `bytes<len>` | 16 random bytes |
| timestamp | `u64` | sender's clock, Unix ms; not trusted |
| contentType | `string` | e.g. `text/plain; charset=utf-8` |
| payload | `bytes<len>` | |
| inReplyTo | `u8 flag` (0/1), then `bytes<len>` if 1 | |
| author | author envelope (§9.2), domain `epistle` | |

There is no rite-level size limit. The only bound is the 16 MiB Vessel frame.

*Reference:* `src/CupriNet.Rites/Epistle.cs:13-117`, `src/CupriNet.Rites/EpistleSession.cs:28-117`.

> **Open (OI-09.3):** the type byte is outside the Veil, so it is not authenticated. `messageId` length is not
> validated. Attestations are unsigned.

## 9.4 Vigil (acknowledged retry)

The Vigil makes Epistle delivery reliable on top of Attestations:

- the sender retransmits an Epistle until it receives an Attestation carrying the Epistle's `messageId`;
- retransmit delays are 1, 2, 4, 8, 16, 32, 60, 60 s;
- after the 8th transmission the message is abandoned;
- the receiver deduplicates by `messageId` (reference: the last 4,096 ids).

> **Open (OI-09.4):** the reference library provides the Vigil and the deduper as utilities but never wires them up.
> Applications decide whether to send Attestations. The spec must decide whether a receiver MUST attest. Until it
> does, Epistle delivery is at-most-once, like every other rite.

## 9.5 Conduit (raw duplex), stream 4

Each frame is `veil(pad(ConduitFrame))`. It has no type byte and no version byte. Padding is off by default.

| Field | Type | Notes |
|---|---|---|
| protocolId | `u32` | chosen by the application |
| schemaVersion | `u32` | chosen by the application |
| flags | `u32` | all 32 bits belong to the application on this path |
| payload | `bytes<len>` | ≤ 16,711,680 (16 MiB − 64 KiB) |
| author | author envelope, domain `conduit` | |

Delivery is ordered and unacknowledged. The Pilgrimage Conduit is a **different** wire format
([10 §10.7](10-shrines.md)).

> **Open (OI-09.5):** the channel Conduit predates CupriMark and has no version of its own. Its compatibility
> "inherits" from Consecration, but nothing dispatches on the Consecration version.

## 9.6 Reliquary manifest

The Reliquary is the chunked, hash-verified file format. Its manifest:

| Field | Type |
|---|---|
| transferId | `bytes<len>` (16 random bytes) |
| fileCount | `varuint` (≤ 1,024) |
| per file: path | `string` (relative, `/`-separated) |
| per file: length | `u64` |
| per file: chunkSize | `varuint` (1 – 8 MiB) |
| per file: fullHash | `bytes<len>` (SHA-256, 32) |
| per file: chunkHashCount | `varuint`, MUST equal `ceil(length / chunkSize)` |
| per file: chunkHashes | `chunkHashCount × bytes<len>` (SHA-256, 32 each) |
| author | author envelope, domain `reliquary` |

Further limits: at most 2^20 chunks per file and at most 8 GiB in total.

A receiver MUST verify each chunk's length and SHA-256, and MUST verify each file's SHA-256 before releasing it.

A receiver MUST reject a `path` that:

- is empty, longer than 1,024 characters, or rooted;
- contains `:`, an empty segment, or a `.` or `..` segment;
- contains control characters.

*Reference:* `src/CupriNet.Rites/Reliquary.cs:10-167`, `src/CupriNet.Rites/FilePathGuard.cs`.

> **Open (OI-09.6):**
>
> - The reference decoder does not validate `path`; it does so only when writing to disk.
> - The rejected character set also depends on the OS (`Path.GetInvalidFileNameChars`).
> - There is no channel-side Reliquary transfer protocol: the library carries manifests and chunks only over a
>   Pilgrimage, as the Relic. Channel file transfer is application-defined.

## 9.7 Cover traffic

Cover traffic makes real channel activity harder to separate from decoy activity.

- **Effigy** (session kind 3): after the kind byte, both sides send zero-filled frames on stream 3. Stream 3 is the
  Epistle stream, so a frame's stream id does not reveal whether it is real or cover. Sizes and timing are local.
- **Pageant** (session kind 4): a small clique of nodes follows one shared, seeded schedule of decoy "conversations".

  The invitation (07 §7.3, op 6) has this layout:

  | Field | Type |
  |---|---|
  | id | `bytes<len>` (16 bytes) |
  | seed | `bytes<len>` (32 bytes) |
  | epoch | `u64`, Unix ms |
  | rosterCount | `varuint` (≤ 8) |
  | roster | `rosterCount × bytes<len>` PeerRecord documents |

  Each edge sends the raw pageant `id` as its first frame on stream 3. Every member MUST derive the schedule
  bit-exactly from the seed:

  - seed the PRNG with FNV-1a-64 over the seed bytes;
  - draw from SplitMix64;
  - the constants are in `src/CupriNet.Hosting/Pageant.cs:163-260`.
- **Hot fuzz:** PING (07 §7.3, op 5) heartbeats with random padding.

> **Open (OI-09.7):** cover traffic is indistinguishable on stream 3, but control sessions remain distinguishable from
> channel sessions by their cleartext stream ids (OI-03.6). The Pageant schedule depends on IEEE-754 double
> arithmetic. Document it as an algorithm, or replace it with integer-only arithmetic.
