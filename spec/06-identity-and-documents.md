# 6. Identity and signed documents

> **Status: Draft.** Describes the reference implementation at commit `8a35e12`.

## 6.1 Node identity

A node's long-term identity is an Ed25519 key pair, its **Seal**. Its identifier is its **Sigil**:
`SHA-256(seal_public_key)` (02 §2.3). A Sigil is rendered for humans as Bech32 with HRP `cupri`, using the
same encoding as a Shrine address (10 §10.1).

The **network** (Concordium) is a UTF-8 string naming the overlay. Two nodes interoperate only if the strings are
byte-equal. There is no default value.

> **Open (OI-06.1):** `network` has no canonical form or maximum length (also OI-04.8).

## 6.2 Signed document envelope

PeerRecord, Intonation, Decree and LanPresence all share one envelope:

| Field | Type |
|---|---|
| body | `bytes<len>` |
| signature | `bytes<len>`: Ed25519 over `label ‖ 0x00 ‖ body`, the label naming the document (02 §2.4) |

A verifier MUST verify the signature over the label for the document type it expects, followed by `0x00` and the
`body` bytes **as received**. A node that stores a document and later
sends it on (a PeerRecord in gossip, a Decree in a lookup) MUST send the same `body` bytes it received. A decoder
MUST ignore bytes in a `body` after the last field it knows: they are fields added by a later version, and they
stay under the signature.

> **Resolved (OI-06.2):** several reference verifiers used to decode the body, re-encode it and verify the re-
> encoding, so a document could be valid on one path and refused on another, and no document could gain a field.
> Decoded documents now carry the body they arrived with; it is what is verified and what is forwarded. Canonical
> encoding is enforced at decode (01 §1.3).

## 6.3 Beacon (reachability candidate)

| Field | Type | Notes |
|---|---|---|
| kind | `u8` | `0` Host · `1` Mapped (NAT-PMP or reflexive) · `2` Manual · `3` Relay (a Ferryman) · `4` Onion |
| host | `string` | IP literal, hostname, or `<56 base32>.onion` |
| port | `varuint` | 1–65,535 |

A **beacon list** is `varuint count` followed by `count` beacons. The maximum count depends on the containing
document:

| Container | Max beacons |
|---|---|
| PeerRecord | 8 |
| Intonation | 32 |
| Decree | 8 |

Dial order is Host, then Mapped, then Manual, then Onion (via Tor) as a fallback. Kind `3` (Relay) is no longer
produced: a node reachable through a Ferryman publishes a relay route in its Intonation instead (§6.4.4). A receiver
MUST NOT dial a Relay beacon.

For Onion beacons, the reference node always listens on virtual port **43820**. Today the port in the beacon is
ignored when dialling from an Intonation and honoured when dialling from a PeerRecord.

*Reference:* `src/CupriNet.Core/Beacon.cs:4-20`, `src/CupriNet.Core/BeaconCodec.cs:12-35`.

> **Open (OI-06.3):** the decoder does not reject an unknown `kind`, an out-of-range `port`, or a malformed `host`.
> Onion port handling is inconsistent (see above).

## 6.4 Intonation (bootstrap link)

An Intonation is a signed, expiring invitation to connect to a node. It is usually shared as a link or QR code.

### 6.4.1 URI

```
cuprinet://intone/<base64url(document)>
```

- base64url is RFC 4648 §5, unpadded.
- The prefix is case-sensitive.
- The URI has no query or fragment.

*Reference:* `src/CupriNet.Core/IntonationUri.cs:9-49`.

> **Open (OI-06.4):** whether a decoder accepts padding or whitespace in the base64url is currently whatever .NET
> does. Proposal: reject both.

### 6.4.2 Body

The body is wrapped in the §6.2 envelope and signed by the inviter's Seal.

| # | Field | Type | Notes |
|---|---|---|---|
| 1 | version | `u8` | CupriMark `intonation` (currently 2) |
| 2 | network | `string` | |
| 3 | inviterSealPublicKey | `bytes<len>` | 32 |
| 4 | beacons | beacon list | ≤ 32 |
| 5 | litany | `varuint n` (≤ 32), then `n × bytes<len>` | each entry is a 32-byte Sigil: a sample of peers the inviter knows |
| 6 | issuedAt | `u64` | Unix seconds |
| 7 | severance | `u64` | Unix seconds; the expiry |
| 8 | nonce | `bytes<len>` | 16 random bytes |
| 9 | petition | `u8 flag` (0 or 1); if 1, `bytes<len>` | any other flag value is malformed |
| 10 | moniker | `string` | *optional trailer*: display name |
| 11 | webrtc | `u8` lead byte | *optional trailer*: `0x00` = absent; otherwise the WebRTC endpoint block (§6.4.3) starting with its version |
| 12 | shrine | `u8` lead byte | *optional trailer*: `0x00` = absent; otherwise the Shrine address block (10 §10.8) starting with its version |
| 13 | relayRoutes | relay-route block | *optional trailer*; see §6.4.4 |

**Trailer rule.** Fields 10–13 are positional and each one is present only if bytes remain. To include a later
field, an encoder MUST include every earlier trailer field: an empty moniker, and `0x00` for an absent WebRTC or
Shrine block. With no trailer fields, the body ends after field 9.

**Moniker normalisation** (applied before signing):

1. null or whitespace-only input becomes absent;
2. Unicode categories Cc, Cf, Cs, Co, Cn, Zl, Zp and Zs are removed;
3. runs of whitespace fold to one space, and the result is trimmed;
4. the result is clamped to 48 Unicode scalar values.

*Reference:* `src/CupriNet.Core/IntonationCodec.cs:14-163`, `src/CupriNet.Core/Monikers.cs:21-77`.

> **Open (OI-06.5), reduced:** moniker normalisation depends on the runtime's Unicode tables. It no longer affects
> whether a document verifies (OI-06.2), but two implementations can still display different names for the same bytes.
> Pin a Unicode version, or move normalisation to display time.
>
> **Open (OI-06.6):** the `0x00` WebRTC sentinel makes pre-Shrine decoders reject links that carry a Shrine but no
> WebRTC block. Positional trailers cannot be extended cleanly. Proposal: type-length-value (TLV) extensions in the
> next `intonation` version.
>
> **Open (OI-06.7):** litany and petition are signed but no receiver reads them. Define their semantics or remove
> them.

### 6.4.3 WebRTC endpoint block

| Field | Type | Notes |
|---|---|---|
| version | `u8` | CupriMark `webrtc-endpoint` (currently 1); MUST NOT be 0 |
| iceUfrag | `string` | ICE-lite username fragment (reference: 8 characters) |
| icePassword | `string` | ICE-lite password (reference: 24 characters) |
| fingerprintAlgorithm | `string` | e.g. `"sha-256"` |
| fingerprint | `bytes<len>` | raw digest of the node's DTLS certificate, 1–64 bytes |
| port | `varuint` | the node's WebRTC UDP port, 1–65,535 |

The node's address comes from the Intonation's beacons. How a browser uses this block is specified in
[11](11-traversal.md).

*Reference:* `src/CupriNet.Core/WebRtcEndpoint.cs:36-94`.

### 6.4.4 Relay-route block

Lists the Ferryman relays through which the inviter can be reached when none of its beacons can be dialled
([11 §11.5](11-traversal.md)).

| Field | Type | Notes |
|---|---|---|
| version | `u8` | CupriMark `relay-route` (currently 1) |
| count | `varuint` | 1–4 |
| per route: relaySigil | `bytes<len>` | 32 bytes: the relay's Sigil |
| per route: host | `string` | 1–255 bytes: the relay's address |
| per route: port | `varuint` | 1–65,535 |
| per route: handle | `bytes<len>` | 16 bytes: what to ask the relay for |

The handle is the hash of a rendezvous key the inviter made for that relay. It cannot be derived from the inviter's
Sigil, which is why it is carried here. Because the Intonation is signed by the inviter's Seal, a holder of the link
knows the handle is the inviter's.

A relay route MUST NOT appear in a PeerRecord or a Decree: both are public, and publishing a handle beside the Sigil
it belongs to would let a relay link the two. A link restricted to onion beacons MUST NOT carry one either, since a
route ends in a clearnet hole punch.

*Reference:* `src/CupriNet.Core/RelayRoute.cs`.

### 6.4.5 Validation

A receiver MUST reject the Intonation unless, in order:

1. it parses;
2. its version is accepted ([05](05-versioning.md));
3. `network` equals the receiver's own;
4. the signature verifies under `inviterSealPublicKey`;
5. `now ≤ severance` (Unix seconds).

On success, the receiver dials the beacons and runs the Conjunction **pinned to**
`SHA-256(inviterSealPublicKey)`.

> **Open (OI-06.8):** `issuedAt` is never checked, so future-dated links are accepted. No maximum lifetime is
> enforced. The reference mint defaults to a 2 h lifetime.

## 6.5 PeerRecord

A PeerRecord is a node's self-description on the overlay. It travels in gossip and discovery responses
([07](07-overlay.md)).

| # | Field | Type | Notes |
|---|---|---|---|
| 1 | sealPublicKey | `bytes<len>` | 32 |
| 2 | endpoints | beacon list | ≤ 8 |
| 3 | sequence | `u64` | higher replaces lower; the reference uses Unix milliseconds |
| 4 | issuedAt | `u64` | Unix seconds |
| 5 | capabilities | `u32` | flags: `1` Relay, `2` ChannelProvider |
| 6 | moniker | `string` | *optional trailer*, normalised as in §6.4.2 |

The record is wrapped in the §6.2 envelope and signed by the record's own Seal. The record's Sigil is
`SHA-256(sealPublicKey)`.

*Reference:* `src/CupriNet.Concordance/PeerRecord.cs:10-140`.

> **Open (OI-06.9):** there is no freshness rule: `issuedAt` is never checked and records never expire. The Relay
> capability flag is defined but never set (see 11).

## 6.6 Local-only formats (not part of the protocol)

KnownPeer, KindredBook, the constellation warm-start cache, KnownRelays and the secret store are node-local storage.
They are listed here only so that readers know they are deliberately out of scope.
