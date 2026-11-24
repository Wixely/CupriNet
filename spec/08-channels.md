# 8. Channels (Arcanum, Layer 2)

> **Status: Draft.** Describes the reference implementation at commit `8a35e12`.

An **Arcanum** is a channel: a group of members who share a **Watchword**. Members find each other through the
overlay ([07](07-overlay.md)) and then talk **directly**. Channel content is never relayed through a third node. That
holds by construction: no part of the protocol forwards streams 2–4. No field on the wire enforces it.

## 8.1 Watchword

Text form: `Appellation "#" base64url(Obscuration)`.

| Part | Rule |
|---|---|
| Appellation | normalised to Unicode NFC; non-empty; at most 256 UTF-16 code units; contains no `#`; case- and whitespace-sensitive |
| Obscuration | 16–64 random bytes; written as unpadded base64url |

Parsing splits on the **last** `#`. A name that still contains `#` is rejected.

*Reference:* `src/CupriNet.Arcanum/Watchword.cs:16-92`.

> **Open (OI-08.1):** the length unit (UTF-16 units), the Unicode version used for NFC, the handling of lone
> surrogates (the reference throws rather than rejecting), and base64url padding/whitespace are implementation-defined.

## 8.2 Key schedule

```
materivane = Argon2id(password = UTF-8(NFC(Appellation)), salt = Obscuration,
                      m = 65536 KiB, t = 3, p = 1, version = 0x13, L = 32)

subkey(label) = HKDF-SHA256(ikm = materivane, salt = "", info = label, L = 32)
```

| Subkey | info label | Use |
|---|---|---|
| Ascendant | `cuprinet/arcanum/ascendant/v1` | the channel's overlay routing key **and** its ChannelId |
| GlyphKey | `cuprinet/arcanum/glyph/v1` | derives the rotating discovery tag (§8.3) |
| ConcordKey | `cuprinet/arcanum/concord/v1` | Consecration and session-key derivation (§8.4) |
| VeilKey | `cuprinet/arcanum/veil/v1` | derived but **unused** |

The Argon2id parameters are fixed by the protocol. Changing them creates a different channel.

*Reference:* `src/CupriNet.Arcanum/ArcanumKeys.cs:39-58`, `src/CupriNet.Alembic.BouncyCastle/BouncyCastleSuite.cs:171-197`.

> **Open (OI-08.2):** VeilKey is derived but never used. Either specify a use for it or drop it.

## 8.3 Glyph (rotating discovery tag)

```
epoch     = floor(unix_seconds / 600)
glyph(e)  = HKDF-SHA256(ikm = GlyphKey, salt = u64(e), info = "cuprinet/arcanum/glyph-token/v1", L = 32)
window    = { glyph(e−1), glyph(e), glyph(e+1) }
```

A Glyph lets members find each other's advertisements without revealing the channel to overlay nodes that don't know
the Watchword. *Reference:* `src/CupriNet.Arcanum/Glyph.cs:14-46`.

## 8.4 Consecration (channel key confirmation)

Consecration runs on **stream 2** of a Channel session, after the Conjunction and the preamble (07 §7.2). It proves
that both peers know the same Watchword and derives a session key. It is **not** a Noise handshake.

### 8.4.1 Messages

**Hello**

| Field | Type | Notes |
|---|---|---|
| type | `u8` | `1` |
| envelope | `u8` | `1`: the message format, not the CupriMark ordinal |
| min | `varuint` | `consecration` range, ≤ 65,535 |
| max | `varuint` | ≤ 65,535 |
| epoch | `u64` | `floor(unix_seconds / 600)` |
| nonce | `bytes<len>` | 16 random bytes |

**Confirm**

| Field | Type |
|---|---|
| type | `u8` = `2` |
| tag | `bytes<len>`, 32 bytes |

### 8.4.2 Flow

```
 Initiator                               Responder
   │ ── Hello ───────────────────────────▶ │  responder checks |epoch_I − epoch_R| ≤ 1
   │ ◀────────────────────────── Hello ─── │
   │ ── Confirm(tag_I) ──────────────────▶ │
   │ ◀────────────────────── Confirm(tag_R)│  responder sends, then verifies tag_I
   │ verify tag_R                          │
```

Both sides negotiate `consecration` from the two ranges ([05](05-versioning.md)). The **initiator's** epoch is used
throughout.

### 8.4.3 Transcript and keys

```
neg_digest = CupriMark TranscriptBinding digest for "consecration"   ; 05 §5.3
transcript = SHA-256( bytes(neg_digest) ‖ bytes(Ascendant) ‖ u64(epoch)
                    ‖ bytes(initiator_sigil) ‖ bytes(responder_sigil)
                    ‖ bytes(initiator_nonce) ‖ bytes(responder_nonce) )

tag_I       = HKDF(ConcordKey, salt = transcript, info = "cuprinet/arcanum/consecration/initiator/v1", 32)
tag_R       = HKDF(ConcordKey, salt = transcript, info = "cuprinet/arcanum/consecration/responder/v1", 32)
session_key = HKDF(ConcordKey, salt = transcript, info = "cuprinet/arcanum/veil-session/v1",           32)
```

The sigils are the two peers' **overlay** Sigils, as authenticated by the Conjunction. Tags MUST be compared in
constant time. The session key is the same in both directions.

*Reference:* `src/CupriNet.Arcanum/ConsecrationHandshake.cs:31-210`.

> **Open (OI-08.3):**
>
> - Nonce and tag lengths are not checked.
> - The responder's epoch is not in the transcript and is not checked by the initiator.
> - The responder emits its tag before verifying the initiator's, so either side's tag lets an offline guesser test
>   Watchwords, at Argon2id cost per guess.
> - Failure sends no signal.
> - A frame on any other stream aborts the handshake.

## 8.5 Ownership and admission

A channel MAY have an **owner** and an access mode:

| AccessMode | Value | Admission |
|---|---|---|
| Aperture | 0 | anyone with the Watchword |
| Sanction | 1 | owner or Investiture holder |
| Sealed | 2 | owner or Investiture holder |
| Anarch | 3 | anyone with the Watchword (no owner) |

> **Open (OI-08.4):** Sanction and Sealed are handled identically. Define the difference or merge them.

### 8.5.1 ChannelDescriptor (signed body)

| Field | Type |
|---|---|
| version | `u8` (CupriMark `channel-descriptor`) |
| channelId | `bytes<len>` (the Ascendant) |
| ownerPublicKey | `bytes<len>` (Ed25519) |
| accessMode | `u32` |
| createdAt | `u64` |
| policyVersion | `u32` |

Signed by the owner. `HashOf(descriptor) = SHA-256(body)`. **There is no wire encoding for the full signed
document.** Descriptors are distributed out of band. **Open (OI-08.5).**

### 8.5.2 Investiture (membership credential)

| Field | Type |
|---|---|
| version | `u8` (CupriMark `investiture`) |
| channelId | `bytes<len>` |
| memberSealPublicKey | `bytes<len>` |
| roles | `u32` flags: `1` Member · `2` Steward · `4` Magister |
| notBefore | `u64` |
| notAfter | `u64` |
| serial | `u64` |
| issuerPublicKey | `bytes<len>` |
| signature | `bytes<len>` (Ed25519 by the issuer over all preceding fields) |

Valid if: the version is accepted, the channelId matches, the issuer is the descriptor's owner,
`notBefore ≤ now ≤ notAfter`, and the signature verifies.

> **Open (OI-08.6):** roles, serial (revocation) and policyVersion have no defined semantics. Only the founding owner
> can issue: the ascension chain is not consulted during admission.

### 8.5.3 Ascension (ownership transfer)

An AscensionLink body is `bytes(channelId) ‖ u64(reign) ‖ bytes(newOwnerPublicKey) ‖ bytes(previousHash)`, signed by
the current owner. A chain resolves from the descriptor (reign 0). Each link MUST:

- increment `reign` by exactly 1;
- reference the previous element's hash.

Chains are limited to 4,096 links. A **schism** is two valid chains of the same length that end in different owners.
The reference implementation detects schisms but does not resolve them.

> **Open (OI-08.7):** links have no version byte and no wire encoding. The ROADMAP describes "accepted schism", which
> is unimplemented.

### 8.5.4 Admission exchange (stream 2, after Consecration)

This exchange runs only when the channel is Sanction or Sealed and both sides are configured for it.

```
commitment = SHA-256( string("cuprinet/admission/v1") ‖ bytes(channelId) ‖ bytes(session_key) )
claim      = bytes(persona_public_key) ‖ u8 tag ‖ [tag = 1: bytes(investiture)] ‖ bytes(Ed25519(persona, commitment))
```

`tag` is `0` when the persona is the owner, and `1` when an Investiture follows. The initiator sends its claim first.
Each side verifies the other's claim against the descriptor.

> **Open (OI-08.8) — security, mitigated:** both sides sign the *same* commitment, and the responder receives first,
> so a responder that knows the Watchword but holds no Investiture could echo the initiator's own claim back and be
> admitted. The reference implementation now refuses a claim that names the verifier's own persona, which blocks the
> reflection. As a side effect two devices sharing one persona cannot pair on a gated channel. The proper fix is
> planned for `consecration` v2: include the role (`"initiator"` or `"responder"`) in the commitment. *Reference:*
> `src/CupriNet.Hosting/CupriNode.cs` (`VerifyAdmissionClaim`).
>
> **Open (OI-08.9):** whether admission runs is a local setting that is not signalled. A mismatched pair deadlocks or
> fails with a stream error.

## 8.6 Decree (channel advertisement)

A member advertises that it serves a channel by publishing a Decree to the overlay nodes closest to the channel's
Ascendant ([07 §7.3](07-overlay.md), PUBLISH).

| Field | Type | Notes |
|---|---|---|
| version | `u8` | CupriMark `decree` |
| glyph | `bytes<len>` | 32 bytes (§8.3) |
| providerSealPublicKey | `bytes<len>` | the provider's **overlay** Seal |
| endpoints | beacon list | ≤ 8 |
| expiresAt | `u64` | Unix seconds |
| sequence | `u64` | higher replaces lower for the same provider |

The Decree is wrapped in the 06 §6.2 envelope and signed by the provider's overlay Seal. A searcher MUST check the
signature, the version, `now ≤ expiresAt`, and that `glyph` is in the current window.

Holder limits (informative): at most 8 providers per glyph, at most 4,096 glyphs, and a maximum lifetime of 3,600 s.
A holder rejects a Decree with `expiresAt > now + 3600`.

*Reference:* `src/CupriNet.Arcanum/Decree.cs:31-170`, `src/CupriNet.Arcanum/DecreeStore.cs:14-127`.

> **Open (OI-08.10):** the reference publisher sets exactly a 1 h lifetime, so any clock skew where the publisher is
> ahead makes holders reject it, while the PUBLISH still answers Ok (OI-07.7). A Decree stops matching lookups once
> its glyph leaves the ±1 epoch window (about 20 min), and nothing republishes it.
