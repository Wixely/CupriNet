# 1. Encoding (Codex)

> **Status: Draft.** Describes the reference implementation at commit `8a35e12`.

Unless a structure says otherwise, every CupriNet wire structure is a sequence of the primitives below. Each is
written in field order with no padding, alignment or tags between fields.

## 1.1 Primitives

| Name | Wire form |
|---|---|
| `u8` | one byte |
| `u16` | 2 bytes, big-endian. *Codex has no u16 writer; u16 appears only inside signed contexts, e.g. 04 §4.4* |
| `u32` | 4 bytes, big-endian |
| `u64` | 8 bytes, big-endian |
| `varuint` | unsigned LEB128: 7 bits per byte, least-significant group first, bit 7 (`0x80`) = continuation. Maximum 10 bytes |
| `bytes<len>` | `varuint n` followed by exactly `n` bytes |
| `bytes[n]` | exactly `n` bytes, no prefix |
| `string` | `bytes<len>` whose content is UTF-8. No terminator, no BOM, no normalisation |

There is no signed integer primitive. Signed quantities (for example Unix timestamps) are written as the
two's-complement value cast to `u64`.

*Reference:* `src/CupriNet.Codex/CodexWriter.cs`, `src/CupriNet.Codex/CodexReader.cs`.

## 1.2 Encoding rules

An encoder:

- MUST write every `varuint` in its minimal (shortest) form;
- MUST write strings as well-formed UTF-8.

Signed structures depend on this: they assume one canonical byte encoding (`CodexWriter.cs:8-10`).

## 1.3 Decoding rules

A decoder MUST reject the input when:

- it ends before a fixed-width field or `bytes[n]` is complete;
- a `bytes<len>` or `string` length is larger than the bytes remaining in the enclosing structure. The decoder MUST
  check this before allocating;
- a `varuint` reaches an 11th byte.

The reference decoder is more lenient than the encoder in three places that the spec still has to settle:

> **Open (OI-01.1):** non-minimal `varuint` encodings (e.g. `80 00` for zero) are accepted. Proposal: MUST reject,
> as the CupriMark reader already does.
>
> **Open (OI-01.2):** in a 10-byte `varuint`, bits above bit 63 are silently discarded. Proposal: MUST reject.
>
> **Open (OI-01.3):** invalid UTF-8 is replaced with U+FFFD rather than rejected. Proposal: MUST reject.
>
> **Open (OI-01.4):** bytes after a structure's last field are ignored in every structure. Padding (10 §10.3.1) and
> the trailing optional `targetTag` (04 §4.4) rely on this. Proposal: make it explicit per structure. Structures
> that permit extension or padding say so; all others MUST reject trailing bytes.

On any decode failure, the receiver treats the containing message as malformed. What that does to the connection is
defined per layer.

## 1.4 Frame codec

Byte-stream transports carry a sequence of frames:

| Field | Type |
|---|---|
| length | `u32` (big-endian), `0 ≤ length ≤ max_frame` |
| content | `bytes[length]` |

`max_frame` is **16,777,216 bytes (16 MiB)** and excludes the 4-byte prefix. A zero-length frame is legal.

A receiver:

- MUST reject a length greater than `max_frame` before reading or allocating the content;
- MUST treat end-of-stream after a complete frame (zero bytes of the next prefix read) as a clean close;
- MUST treat end-of-stream partway through a prefix or content as an error.

A sender MUST NOT emit a frame longer than `max_frame`.

*Reference:* `src/CupriNet.Codex/FrameCodec.cs:13-46`.

> **Open (OI-01.5):** `max_frame` is a constructor parameter and is not negotiated. Proposal: fix 16 MiB as the value
> every implementation MUST accept.

## 1.5 Human-readable encodings

| Encoding | Used for | Defined in |
|---|---|---|
| Bech32 (BIP-173, HRP `cupri`) | node fingerprints, Shrine addresses | [10 §10.1](10-shrines.md#101-signet-and-the-cupri1-address) |
| `cuprinet://intone/…` URI | Intonation links | [06](06-identity-and-documents.md) |
| lowercase hex (64 chars) | Sigil debug rendering; not an interchange format | — |
