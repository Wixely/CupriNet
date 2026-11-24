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
| Identity binding signature | Ed25519 over `h ‖ u16(min) ‖ u16(max)`. **No context string.** | [04 §4.4](04-conjunction.md) |
| Shrine target tag | HKDF info `"cuprinet:shrine-target:v1"` | [10 §10.2.3](10-shrines.md) |
| Rite author signature | Ed25519 over `SHA-256("cuprinet/rite/" ‖ domain ‖ 0x00 ‖ content)`; domain ∈ {`reliquary`, `conduit`} | [09](09-rites.md) |
| CupriMark negotiation binding | `"cuprimark/negotiation/v1"` | [05](05-versioning.md) |
| Node routing key | `SHA-256("cuprinet/ascendant/v1" ‖ key)`, label raw (not length-prefixed) | [07 §7.4](07-overlay.md) |
| Tribute PoW | `SHA-256("cuprinet/tribute/v1" ‖ subject ‖ nonce)`, label raw | [07 §7.8](07-overlay.md) |
| Channel subkeys | HKDF info `cuprinet/arcanum/{ascendant,glyph,concord,veil}/v1` | [08 §8.2](08-channels.md) |
| Glyph | HKDF info `cuprinet/arcanum/glyph-token/v1` | [08 §8.3](08-channels.md) |
| Consecration | HKDF info `cuprinet/arcanum/consecration/{initiator,responder}/v1`, `cuprinet/arcanum/veil-session/v1` | [08 §8.4](08-channels.md) |
| Admission commitment | `SHA-256(string("cuprinet/admission/v1") ‖ …)`, label length-prefixed | [08 §8.5.4](08-channels.md) |
| Ferryman | handle `"cuprinet/ferryman/handle/v1"` (raw); reserve `string("cuprinet/ferryman/reserve/v1")` | [11 §11.5](11-traversal.md) |
| Signed documents (Intonation, PeerRecord, Decree, LanPresence, ChannelDescriptor, Investiture, AscensionLink) | Ed25519 over the raw body. **No label.** | [06](06-identity-and-documents.md), [08](08-channels.md) |

> **Open (OI-02.3):** the Seal key that signs the identity binding also signs Intonations, Decrees, LanPresence and
> other documents, and the binding message has no context string. Cross-protocol signature reuse is prevented today
> only because the formats happen to differ. Proposal: every Ed25519 signature input begins with a distinct
> `"cuprinet/<use>/v<n>"` label.
>
> **Open (OI-02.4):** the labels mix styles (`cuprinet:shrine-target:v1` vs `cuprinet/rite/…`). Pick one style before
> freeze.

## 2.5 Insecure suites

The reference implementation includes a test-only suite (`Simulacrum`) whose AEAD and signatures are no-ops. It
reports `IsSecure = false` and can never complete a Conjunction. A conforming implementation MUST NOT emit
signatures, documents or handshakes produced with a suite that does not provide the security properties above.

> **Open (OI-02.5):** the reference implementation enforces `IsSecure` only in the Conjunction. A node configured
> with an insecure suite could still mint documents signed with all-zero signatures.
