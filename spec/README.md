# CupriNet Protocol Specification

> **Status: DRAFT — unfinished, non-normative until frozen.** This specification is being written *from* the
> reference implementation in [`src/`](../src). Where this text and the code disagree, the code is currently the
> source of truth and the disagreement is a bug in one of them, recorded in [open-issues.md](open-issues.md).
> Nothing here is frozen. The wire format freezes at 1.0 (see [ROADMAP.md](../ROADMAP.md#before-10)).

## Purpose

This document set defines the CupriNet wire protocol precisely enough that:

1. an **auditor** can check the reference implementation against a stated intent rather than reverse-engineering it;
2. an **independent implementer** (another language, a browser peer, a fuzzer) can interoperate with a CupriNet node
   using this text and the test vectors alone;
3. the **1.0 wire freeze** has a concrete object to freeze.

It is *not* a tutorial or a rationale document. The *why* lives in [`design/`](../design) and is linked from each
section where it helps; this set states only *what* goes on the wire and what a conforming node must do.

## Documents

| # | Document | Covers | Status |
|---|---|---|---|
| 1 | [01-encoding.md](01-encoding.md) | Codex: integers, lengths, strings, byte arrays, frame codec | Draft |
| 2 | [02-cryptography.md](02-cryptography.md) | Alembic: primitives, sizes, domain separation | Draft |
| 3 | [03-transport.md](03-transport.md) | Vessel: TCP framing, stream multiplexing, reliable UDP, DataChannel | Draft |
| 4 | [04-conjunction.md](04-conjunction.md) | Toll cookie, Noise_XX, identity binding, first contact | Draft |
| 5 | [05-versioning.md](05-versioning.md) | CupriMark version negotiation and lifecycle | Draft |
| 6 | [06-identity-and-documents.md](06-identity-and-documents.md) | Sigil, signed documents, Intonation links | Draft |
| 7 | [07-overlay.md](07-overlay.md) | Concordance (L1): peer table, gossip, discovery, advertisements, Tribute | Draft |
| 8 | [08-channels.md](08-channels.md) | Arcanum (L2): Watchword, key schedule, Consecration, ownership | Draft |
| 9 | [09-rites.md](09-rites.md) | Veil, Epistle, Vigil, Conduit, Reliquary | Draft |
| 10 | [10-shrines.md](10-shrines.md) | Signet, Pilgrimage, Oracle, Auspice, Relic | Draft |
| 11 | [11-traversal.md](11-traversal.md) | LAN discovery, NAT-PMP, hole punching, Ferryman, Tor, WebRTC | Draft |
| — | [open-issues.md](open-issues.md) | Every ambiguity, code/doc mismatch and undecided point found so far | Living |
| — | [test-vectors/](test-vectors/README.md) | Byte-exact vectors generated from the reference implementation | Not started |

### Section status markers

Each section carries one of:

- **Draft** — describes current behaviour; may be incomplete or wrong; will change.
- **Candidate** — believed complete and matching the code; awaiting test vectors and review.
- **Frozen** — normative. Changing it requires a new CupriMark version (see [05-versioning.md](05-versioning.md)).

No section is Frozen yet. The intended order of freezing is: encoding → cryptography → transport → conjunction →
versioning → identity/documents, then the upper layers once their churn settles.

## Conventions

The key words **MUST**, **MUST NOT**, **REQUIRED**, **SHALL**, **SHALL NOT**, **SHOULD**, **SHOULD NOT**,
**RECOMMENDED**, **MAY**, and **OPTIONAL** are to be interpreted as described in
[BCP 14](https://www.rfc-editor.org/info/bcp14) (RFC 2119, RFC 8174) when, and only when, they appear in all capitals.

- Byte layouts are given as tables in wire order. Sizes are in bytes unless stated.
- `u8`, `u16`, `u32`, `u64` are unsigned integers; endianness is stated per structure in
  [01-encoding.md](01-encoding.md) and repeated where a structure departs from the default.
- `bytes[n]` is exactly *n* bytes; `bytes<len>` is a length-prefixed byte string as defined in 01-encoding.
- `||` is concatenation. String literals in `"quotes"` are ASCII/UTF-8 with no terminator unless stated.
- KiB = 1024 bytes, MiB = 1024² bytes.
- *Reference:* lines cite the reference implementation as `path:line` at the commit this draft was taken from. They
  are informative and will be removed when a section is frozen.
- **Open:** callouts mark something the spec has not decided yet; each has an entry in
  [open-issues.md](open-issues.md).

## Terminology

The reference implementation uses a ceremonial vocabulary. This specification uses the ceremonial term as the
*identifier* (so it can be grepped against the code) but always defines it plainly on first use. The full mapping:

| Term | Meaning |
|---|---|
| **Vessel** | one transport connection: framed, multiplexed into numbered streams |
| **Codex** | the binary encoding primitives used by every structure |
| **Alembic** | the cryptographic primitive suite (an abstraction seam over concrete algorithms) |
| **Toll** | stateless pre-handshake anti-DoS cookie |
| **Conjunction** | first-contact handshake: Noise_XX plus Ed25519 identity binding |
| **CupriMark** | per-component version negotiation and retirement |
| **Sigil** | a node's long-term pseudonymous identity key on the overlay |
| **Intonation** | a signed, expiring bootstrap document (`cuprinet://intone/…` link / QR code) |
| **Concordance** | Layer 1: the shared pseudonymous overlay. Routes metadata only; never forwards application traffic |
| **Tribute** | proof-of-work price attached to a channel advertisement |
| **Lodestar** | a well-known, always-on public overlay node (a deployment role, not a protocol role) |
| **Arcanum** | Layer 2: a channel between members. Direct-only; content is never relayed |
| **Watchword** | the shared channel code `Name#Salt` from which channel keys are derived |
| **Consecration** | the channel key-confirmation handshake |
| **Veil** | the inner per-rite encryption layer used on channels |
| **Rite** | an application-level protocol carried on a Vessel stream |
| **Epistle** / **Vigil** | a message / its acknowledgement-and-retry layer |
| **Conduit** | a raw duplex session under an application-chosen protocol id |
| **Reliquary** | chunked, hash-verified file transfer format |
| **Shrine** | content hosted over L2 at a Signet address |
| **Signet** | a site key; its bech32 `cupri1…` rendering *is* the address |
| **Pilgrimage** / **Pilgrim** | the connect handshake to a Shrine / the ephemeral visitor making it |
| **Oracle** | HTTP-shaped request/response rite on a Pilgrimage |
| **Auspice** | named live feed (attend → snapshot → updates) on a Pilgrimage |
| **Relic** | bulk content over a Pilgrimage, carried as a Reliquary transfer |
| **Ferryman** | opt-in relay that brokers a hole punch between two NAT'd peers, then drops out |

## Layering

```
 Rites           Epistle+Vigil · Conduit · Reliquary          | Oracle · Auspice · Relic · Conduit
                 (Veil-sealed, on an Arcanum channel)         | (unsealed, on a Pilgrimage)
 ─────────────────────────────────────────────────────────────┼────────────────────────────────────
 Session         Consecration (channel key confirmation)      | Pilgrimage (Signet pinning)
 ─────────────────────────────────────────────────────────────┴────────────────────────────────────
 Overlay (L1)    Concordance: peer exchange, discovery, advertisements      (metadata only)
 ──────────────────────────────────────────────────────────────────────────────────────────────────
 Handshake       Toll → Noise_XX → identity binding (Conjunction)            versioned by CupriMark
 ──────────────────────────────────────────────────────────────────────────────────────────────────
 Transport       Vessel: frames · streams · {TCP | reliable UDP | Tor | WebRTC DataChannel}
```

## Scope

**In scope:** everything that crosses between two nodes, and everything one node signs that another verifies
(documents, links, addresses rendered for humans when another implementation must parse them).

**Out of scope:** local storage formats, the .NET public API, configuration defaults that do not affect interop,
and the internals of the separately-specified dependencies:

- the Noise Protocol Framework ([noiseprotocol.org](https://noiseprotocol.org/noise.html), revision 34);
- WebRTC ICE/DTLS/SCTP as implemented by [CupriWebRTC](https://github.com/Wixely/CupriWebRTC);
- Tor onion services as implemented by [CupriTor](https://github.com/Wixely/CupriTor);
- the CupriMark encoding itself ([CupriMark](https://github.com/Wixely/CupriMark)), referenced from 05-versioning.

## Contributing to this draft

When you change a wire structure in `src/`, update the matching section here in the same commit. If you can't, add an
entry to [open-issues.md](open-issues.md). Promoting a section from Draft to Candidate requires test vectors for
every structure in it.
