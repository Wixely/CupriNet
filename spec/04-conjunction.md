# 4. Conjunction: first contact

> **Status: Draft.** Describes the reference implementation at commit `8a35e12`. This is the first layer intended to
> reach Candidate.

Every connection — node to node, Pilgrim to Shrine, and requester to Ferryman — starts with the same handshake on
stream 0:

```
 Initiator (I)                                Responder (R)
      │                                             │
      │◀──────────── Toll challenge ─────────────── │   §4.1  cleartext, if the Toll is enabled
      │ ───────────── Toll echo ──────────────────▶ │
      │ ───────────── Noise msg 1  (32 B) ────────▶ │   §4.2  cleartext Noise handshake
      │◀──────────── Noise msg 2  (96 B) ────────── │
      │ ───────────── Noise msg 3  (64 B) ────────▶ │
      │                 ── Split ──                 │         all later frames encrypted (03 §3.5)
      │ ───────────── identity binding ───────────▶ │   §4.3
      │◀──────────── identity binding | refusal ─── │
      │ ───────────── session kind (stream 6) ────▶ │   pairing only; see 07
```

Until the bindings have been exchanged, a frame on any stream other than 0 is a handshake failure.

On any handshake failure, a side closes the Vessel without sending anything. The refusal in §4.3.3 is the one
exception.

The whole handshake SHOULD be bounded by a single deadline. The reference value is 30 s.

## 4.1 Toll (pre-handshake cookie)

The Toll makes an initiator show it can receive at its source address before the responder does any public-key work.
The responder keeps no per-connection state: it recomputes the cookie from the echo.

### 4.1.1 Messages

**Challenge** (R → I, sent as soon as the transport connects):

| Field | Type | Notes |
|---|---|---|
| minVersion | `varuint` | lowest Toll version R accepts (≤ 65,535) |
| maxVersion | `varuint` | highest Toll version R accepts (≤ 65,535) |
| issuedAt | `u64` | Unix seconds |
| cookie | `bytes<len>` | 16 bytes |

**Echo** (I → R):

| Field | Type | Notes |
|---|---|---|
| agreedVersion | `varuint` | the version I selected from the range ([05](05-versioning.md)) |
| issuedAt | `u64` | copied from the challenge |
| cookie | `bytes<len>` | copied from the challenge |

### 4.1.2 Cookie

```
cookie = HMAC-SHA256(key = toll_secret, msg = addr ‖ u64(issuedAt))[0..16)
```

- `toll_secret` is 32 random bytes per responder, held only in memory.
- `addr` is the initiator's IP address: 4 bytes (IPv4) or 16 bytes (IPv6). The port is excluded. When the transport
  has no IP endpoint (DataChannel, Tor), `addr` is empty.

### 4.1.3 Verification

R MUST close the connection unless all of the following hold:

1. `agreedVersion` ≤ 65,535 and is accepted by R;
2. `0 ≤ now − issuedAt ≤ 30 s`, where `now` is read when the echo arrives;
3. the cookie matches the recomputed value, compared in constant time.

*Reference:* `src/CupriNet.Conjunction/Toll.cs:21-113`.

> **Resolved (OI-04.1):** the reference responder used to judge the echo against the time of issue, so the wait was
> never counted and a cookie stamped later than the present was accepted. It now reads the clock when the echo
> arrives. Validation stays stateless, so a cookie minted for the same address on another connection is still accepted
> within the 30 s window; that is by design.
>
> **Open (OI-04.2):** whether the Toll is used is configuration, not signalled in-band. A mismatched pair fails by
> parse error or timeout. Proposal: make the Toll mandatory, or give the first byte a discriminator.
>
> **Open (OI-04.3):** the Toll exchange is not bound into the Noise prologue, so its version negotiation is
> unauthenticated. The cookie is variable-length on the wire although only 16 bytes is valid. On non-IP transports
> the cookie binds nothing.

## 4.2 Noise handshake

| Parameter | Value |
|---|---|
| Protocol name | `Noise_XX_25519_ChaChaPoly_SHA256` (exactly 32 bytes, so `h₀` = the name, unhashed) |
| Pattern | `-> e` · `<- e, ee, s, es` · `-> s, se` |
| Prologue | empty |
| Static key `s` | a **fresh X25519 key pair per connection**. It is *not* an identity |
| Handshake payloads | empty, in all three messages |

| Message | Direction | Bytes |
|---|---|---|
| 1 | I → R | `e` (32) |
| 2 | R → I | `e` (32) ‖ enc(`s`) (48) ‖ enc(empty) (16) = 96 |
| 3 | I → R | enc(`s`) (48) ‖ enc(empty) (16) = 64 |

Each message is the whole payload of one stream-0 frame. After message 3, both sides call Split:

- I sends with `k1` and receives with `k2`; R does the reverse;
- `handshake_hash` (`h`, 32 bytes) is kept for §4.3.

Noise processing follows the Noise Protocol Framework revision 34 exactly. A known-answer test against the
cacophony/snow vectors is in `tests/CupriNet.UnitTests/NoiseKnownAnswerTests.cs`.

*Reference:* `src/CupriNet.Noise/NoiseHandshakeState.cs:41-176`, `src/CupriNet.Conjunction/NoiseConjunction.cs:101-119`.

Because the static keys are thrown away after each connection, Noise authenticates nothing durable. **Identity comes
only from §4.3.**

> **Open (OI-04.4):** received handshake payloads are decrypted and discarded. Message 1 accepts trailing bytes, which
> are mixed into `h`. Proposal: receivers MUST reject non-empty handshake payloads, keeping them reserved for a future
> version.

## 4.3 Identity binding

Each side proves possession of its long-term Ed25519 identity key (its **Seal**) by signing the handshake hash. Both
messages are Noise transport messages on stream 0.

### 4.3.1 Order

1. I sends its binding.
2. R reads I's binding. R picks which identity to present: a node presents its own; a Shrine host selects by target
   tag (10 §10.2.3).
3. R sends its binding, or a refusal (§4.3.3).
4. Each side verifies the other's binding (§4.3.4).

No message follows the two bindings.

### 4.3.2 Binding

| Field | Type | Notes |
|---|---|---|
| bindingVersion | `u8` | `0x01`. This is the message format, not the CupriMark ordinal |
| network | `string` | the overlay name (Concordium). Peers MUST agree on it exactly |
| sealPublicKey | `bytes<len>` | Ed25519 public key, 32 bytes |
| minOrdinal | `varuint` | lowest Conjunction version supported, ≤ 65,535 |
| maxOrdinal | `varuint` | highest Conjunction version supported, ≤ 65,535 |
| signature | `bytes<len>` | 64 bytes, see below |
| targetTag | `bytes<len>` | empty, or 32 bytes on a Pilgrimage (10 §10.2.3). A receiver MUST treat a missing field as empty |

```
signed_message = handshake_hash (32) ‖ u16(minOrdinal) ‖ u16(maxOrdinal)       ; 36 bytes
signature      = Ed25519.Sign(seal_private_key, signed_message)
```

The signature does not cover `network` or `targetTag`. The Noise channel protects both, and `h` is unique to the
session.

### 4.3.3 Refusal

| Field | Type |
|---|---|
| marker | `u8` = `0xFF` |
| reason | `string` (UTF-8, human-readable) |

R MAY send a refusal instead of its binding when it has no identity to present for I's request. It then closes. The
only refusal currently defined is `"no such Shrine at this endpoint"`.

### 4.3.4 Verification

A receiver MUST perform these checks in order, and MUST close the Vessel on the first failure:

1. the first byte is `0x01` (or `0xFF`, which means a refusal: report it and close);
2. the binding parses, and each ordinal is ≤ 65,535;
3. `network` equals the receiver's own network name (byte-exact);
4. `minOrdinal ≤ maxOrdinal`;
5. `signature` verifies over `signed_message` under `sealPublicKey`;
6. if the receiver expects a particular peer, `SHA-256(sealPublicKey)` equals that peer's Sigil. This is how pinning
   works for a known peer, a Shrine Signet or a relay;
7. CupriMark negotiation of the `conjunction` component over `[minOrdinal, maxOrdinal]` succeeds ([05](05-versioning.md)).

The peer's identity is `SHA-256(sealPublicKey)`.

*Reference:* `src/CupriNet.Conjunction/NoiseConjunction.cs:31-264`.

> **Open (OI-04.5):** the signed message has no domain-separation label (see OI-02.3).
>
> **Open (OI-04.6):** I reveals its long-term identity before authenticating R, and a resolving R sends its own
> identity before verifying I's. For an ordinary pairing, the identity hiding XX normally provides is therefore lost.
> Pilgrims and Ferryman requesters use throwaway identities, which limits the exposure.
>
> **Open (OI-04.7):** negotiation failure is not signalled: one side may consider the session open while the other
> has closed it. The negotiated Conjunction version is computed but nothing dispatches on it yet.
>
> **Open (OI-04.8):** `network` has no defined canonical form (case, normalisation, maximum length). The refusal
> `reason` has no maximum length.

## 4.4 Initiator identities by connection kind

| Connection | Initiator identity | Responder pinned? |
|---|---|---|
| Node pairing | the node's Seal | if dialing a known peer |
| Pilgrimage | throwaway, fresh per visit | yes, to the Signet |
| Ferryman request | throwaway | see [11](11-traversal.md) |
| Effigy (cover traffic) | see [09](09-rites.md) | — |

## 4.5 Legacy handshake (not part of the protocol)

`src/CupriNet.Conjunction/ConjunctionHandshake.cs` defines an older unencrypted Hello/Binding exchange. Only tests use
it. It is **not** part of this specification, and a conforming implementation MUST NOT accept it.
