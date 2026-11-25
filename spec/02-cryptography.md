# 2. Cryptography (Alembic)

> **Status: Draft.** Describes the reference implementation at commit `8a35e12`.

CupriNet uses one fixed cipher suite. There is no in-band algorithm negotiation. The protocol changes algorithms
only through a new CupriMark version of the component that uses them ([05](05-versioning.md)).

## 2.1 Primitives

| Role | Algorithm | Sizes | Reference |
|---|---|---|---|
| Signature ("Seal") | Ed25519, pure (RFC 8032, no context, no prehash) | seed 32 · public key 32 · signature 64 | `src/CupriNet.Alembic.BouncyCastle/BouncyCastleSuite.cs:39-86` |
| Key agreement | X25519 (RFC 7748) | private 32 · public 32 · shared 32 | `BouncyCastleSuite.cs:148-169` |
| AEAD | ChaCha20-Poly1305 (RFC 8439) | key 32 · nonce 12 · tag 16; output = ciphertext ‖ tag | `BouncyCastleSuite.cs:88-146` |
| Hash | SHA-256 (and SHA-512 where stated) | 32 / 64 | `src/CupriNet.Alembic/BclPrimitives.cs:9-14` |
| MAC | HMAC-SHA256 | 32 | `src/CupriNet.Conjunction/Toll.cs:99` |
| KDF | HKDF-SHA256 (RFC 5869). An empty salt means 32 zero bytes. `1 ≤ L ≤ 8160` | — | `BclPrimitives.cs:23-57` |
| Password hardening | Argon2id, version `0x13`, m = 65,536 KiB, t = 3, p = 1 | output length set by the caller | `BouncyCastleSuite.cs:171-197` |

In this specification, `HKDF(ikm, salt, info, L)` means HKDF-Extract-then-Expand with those arguments.

## 2.2 Verification rules

- An Ed25519 verifier MUST return failure when a public key is not exactly 32 bytes or a signature is not exactly
  64 bytes.
- An implementation MUST NOT accept an AEAD ciphertext whose tag fails to verify, and MUST NOT release any
  plaintext from it.

> **Open (OI-02.1):** Ed25519 verification strictness is currently whatever BouncyCastle 2.6.2 does: cofactored or
> cofactorless checks, rejection of non-canonical `S`/`R`/`A`, and handling of small-order keys. Independent
> implementations will disagree on edge cases unless the spec pins a rule set. Proposal: RFC 8032 §5.1.7 with
> canonical `S` required and small-order `A` rejected.
>
> **Open (OI-02.2):** an all-zero X25519 output is not explicitly rejected by CupriNet code. Noise does not require
> the check. Decide whether to require it.

## 2.3 Identities derived from keys

| Name | Definition |
|---|---|
| **Sigil** | `SHA-256(ed25519_public_key)`, 32 bytes. A node's overlay identity. |
| **Signet sigil** | same construction over a Shrine's separate key ([10](10-shrines.md)) |

*Reference:* `src/CupriNet.Abstractions/Sigil.cs:12-28`.

## 2.4 Domain separation

Every signature and KDF use is listed here. All strings are UTF-8 with no terminator unless shown.

| Use | Construction | Defined in |
|---|---|---|
| Identity binding signature | Ed25519 over `"cuprinet/conjunction/binding/v2" ‖ h ‖ u16(min) ‖ u16(max)`, label raw | [04 §4.3.2](04-conjunction.md) |
| Shrine target tag | HKDF info `"cuprinet:shrine-target:v1"` | [10 §10.2.3](10-shrines.md) |
| Rite author signature | Ed25519 over `SHA-256("cuprinet/rite/" ‖ domain ‖ 0x00 ‖ content)`; domain ∈ {`reliquary`, `conduit`} | [09](09-rites.md) |
| CupriMark negotiation binding | `"cuprimark/negotiation/v1"` | [05](05-versioning.md) |
| Node routing key | `SHA-256("cuprinet/ascendant/v1" ‖ key)`, label raw (not length-prefixed) | [07 §7.4](07-overlay.md) |
| Tribute PoW | `SHA-256("cuprinet/tribute/v1" ‖ subject ‖ nonce)`, label raw | [07 §7.8](07-overlay.md) |
| Channel subkeys | HKDF info `cuprinet/arcanum/{ascendant,glyph,concord,veil}/v1` | [08 §8.2](08-channels.md) |
| Glyph | HKDF info `cuprinet/arcanum/glyph-token/v1` | [08 §8.3](08-channels.md) |
| Consecration | HKDF info `cuprinet/arcanum/consecration/{initiator,responder}/v1`, `cuprinet/arcanum/veil-session/v1` | [08 §8.4](08-channels.md) |
| Admission commitment | `SHA-256(string("cuprinet/admission/v2") ‖ string(role) ‖ …)`, label length-prefixed | [08 §8.5.4](08-channels.md) |
| Ferryman | handle `"cuprinet/ferryman/handle/v2"` (raw), over a rendezvous key; reserve `bytes("cuprinet/ferryman/reserve/v2")`, signed by that key | [11 §11.5](11-traversal.md) |
| Signed documents (Intonation, PeerRecord, Decree, LanPresence, ChannelDescriptor, Investiture, AscensionLink) | Ed25519 over `label ‖ 0x00 ‖ body`, label raw; see the table below | [06 §6.2](06-identity-and-documents.md), [08](08-channels.md) |
| Veil keys | HKDF info `cuprinet/arcanum/veil/{epistle,conduit,oracle,auspice}/{initiator,responder}/v1` | [09 §9.1](09-rites.md) |
| Reliable UDP connection id | `SHA-256("cuprinet/arq/connection/v1" ‖ sessionId)[0..8]`, label raw | [03 §3.4](03-transport.md) |

Signed-document labels. For a versioned document the label ends in the document's own `version` field, so the
version is under the signature twice: in the body and in the label.

| Document | Label |
|---|---|
| Intonation | `cuprinet/intonation/v<version>` (currently `v2`) |
| Decree | `cuprinet/decree/v<version>` (currently `v2`) |
| LanPresence | `cuprinet/lan-presence/v<version>` (currently `v2`) |
| ChannelDescriptor | `cuprinet/channel-descriptor/v<version>` (currently `v2`) |
| Investiture | `cuprinet/investiture/v<version>` (currently `v2`) |
| PeerRecord | `cuprinet/peer-record/v1` (the record carries no version) |
| AscensionLink | `cuprinet/ascension/v1` (the link carries no version) |

A verifier MUST build the signature input from the label for the document type it is verifying. The `0x00` ends the
label, so no label is a prefix of another label followed by a body.

> **Resolved (OI-02.3):** signed documents used to sign the bare body and were kept apart only because their
> layouts differed. Every Seal signature now begins with a label. Version 1 of each versioned document, which had
> none, is Buried ([05 §5.4](05-versioning.md)).
>
> **Open (OI-02.4):** the labels mix styles (`cuprinet:shrine-target:v1` vs `cuprinet/rite/…`). Pick one style before
> freeze.

## 2.5 Insecure suites

The reference implementation includes a test-only suite (`Simulacrum`) whose AEAD and signatures are no-ops. It
reports `IsSecure = false` and can never complete a Conjunction. A conforming implementation MUST NOT emit
signatures, documents or handshakes produced with a suite that does not provide the security properties above.

> **Resolved (OI-02.5):** the reference node used to check `IsSecure` only in the Conjunction, so a node configured
> with an insecure suite could mint documents before failing to handshake. It now refuses to start. The suite stays
> usable below the node, for codec tests.
