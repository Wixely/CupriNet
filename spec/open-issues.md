# Open issues

> **Living document.** Every **Open (OI-xx.n)** callout in the specification has one row here.
>
> - An issue is closed by deciding the behaviour, updating the section and the code, and adding a test vector.
> - Closed issues move to the log at the bottom.

**Class** is one of:

- 🔴 **security**: an attacker gains something today;
- 🟠 **interop**: two conforming implementations, or two reference nodes, fail to work together;
- 🟡 **decide**: the spec must choose, and the code currently does something arbitrary;
- ⚪ **drift**: documentation disagrees with code.

## Fix first

| Id | Class | Summary |
|---|---|---|
| [OI-08.8](08-channels.md#854-admission-exchange-stream-2-after-consecration) | 🔴 | A responder can reflect the initiator's admission claim and be admitted to an owner-gated channel without an Investiture ([#9](https://github.com/Wixely/CupriNet/issues/9)) |
| [OI-04.1](04-conjunction.md#413-verification) | 🔴 | The Toll freshness check reuses the issuance time, so any cookie minted within ±30 s for the same IP is accepted ([#10](https://github.com/Wixely/CupriNet/issues/10)) |
| [OI-03.6](03-transport.md#35-noise-transport-framing) | 🔴 | Stream ids are outside the AEAD: they can be rewritten on-path, and they reveal the session type ([#11](https://github.com/Wixely/CupriNet/issues/11)) |
| [OI-11.5](11-traversal.md#115-ferryman-brokered-hole-punch) | 🔴 | A Ferryman RESERVE can be replayed by any relay operator: no time, relay or session binding ([#13](https://github.com/Wixely/CupriNet/issues/13)) |
| [OI-11.4](11-traversal.md#115-ferryman-brokered-hole-punch) | 🔴 | The relay learns the target's Sigil, contrary to the documentation; the handle is static ([#13](https://github.com/Wixely/CupriNet/issues/13)) |
| [OI-07.5](07-overlay.md#73-control-requests-stream-6) | 🟠 | DIVINE target framing mismatch: the server routes on `0x20 ‖ key[0..31]` ([#12](https://github.com/Wixely/CupriNet/issues/12)) |
| [OI-06.2](06-identity-and-documents.md#62-signed-document-envelope) | 🟠 | Some verifiers check a re-encoding and others check the received bytes, so the same document can be valid on one path and invalid on another |

## All issues

| Id | Class | Summary |
|---|---|---|
| OI-01.1 | 🟡 | Non-minimal varuints are accepted |
| OI-01.2 | 🟡 | 10th-byte varuint overflow is silently truncated |
| OI-01.3 | 🟡 | Invalid UTF-8 is replaced rather than rejected |
| OI-01.4 | 🟡 | Trailing bytes are ignored in every structure |
| OI-01.5 | 🟡 | Max frame size is not negotiated or fixed |
| OI-02.1 | 🟡 | Ed25519 verification strictness is left to BouncyCastle |
| OI-02.2 | 🟡 | All-zero X25519 output is not explicitly rejected |
| OI-02.3 | 🔴 | No domain separation across Seal signatures (binding, Intonation, Decree, LanPresence, PeerRecord) |
| OI-02.4 | 🟡 | Label style is inconsistent (`:` vs `/`) |
| OI-02.5 | 🔴 | `IsSecure` is enforced only in the Conjunction |
| OI-03.1 | 🟡 | Inner length need not fill the frame; double length prefix |
| OI-03.2 | 🟡 | DataChannel sender does not check message size |
| OI-03.3 | 🟡 | No reserved or private stream id range |
| OI-03.4 | 🟡 | No close reason or error signal; one bad frame kills all streams |
| OI-03.5 | 🔴 | Reliable UDP: no connection id, `una` not range-checked, unacknowledged CLOSE, state created for any datagram |
| OI-03.6 | 🔴 | Stream id unauthenticated (see above) |
| OI-03.7 | 🟡 | Noise messages exceed 65,535 bytes |
| OI-04.1 | 🔴 | Toll freshness (see above) |
| OI-04.2 | 🟠 | Toll on/off is not signalled |
| OI-04.3 | 🟡 | Toll not in the prologue; variable-length cookie; no binding on non-IP transports |
| OI-04.4 | 🟡 | Non-empty Noise handshake payloads are tolerated |
| OI-04.5 | 🔴 | Binding signature has no label (part of OI-02.3) |
| OI-04.6 | 🟡 | Identity revealed before the peer is authenticated |
| OI-04.7 | 🟠 | Negotiation failure is unsignalled; negotiated version is unused |
| OI-04.8 | 🟡 | `network` and refusal `reason` have no canonical form or length limit |
| OI-05.1 | 🟡 | One-byte document versions vs varuint handshake versions |
| OI-05.2 | 🟡 | Catalogue identity is never exchanged |
| OI-05.3 | 🟡 | Format bytes and CupriMark ordinals overlap |
| OI-06.1 | 🟡 | `network` canonical form |
| OI-06.2 | 🟠 | Re-encode verification (see above) |
| OI-06.3 | 🟡 | Beacon fields unvalidated; onion port handling inconsistent |
| OI-06.4 | 🟡 | base64url padding and whitespace |
| OI-06.5 | 🟠 | Moniker normalisation depends on the runtime's Unicode tables and changes signed bytes |
| OI-06.6 | 🟠 | Positional trailers; the `0x00` sentinel breaks older decoders |
| OI-06.7 | 🟡 | Litany and petition have no semantics |
| OI-06.8 | 🟡 | Intonation `issuedAt` unchecked; no maximum lifetime |
| OI-06.9 | 🟡 | PeerRecords never expire; Relay flag never set |
| OI-07.1 | 🟡 | Unknown session kind treated as Channel |
| OI-07.2 | 🔴 | Bootstrap PeerRecord not bound to the authenticated peer |
| OI-07.3 | 🟠 | Reflexive and bootstrap exchanges desynchronise if only one side enables reflexive discovery |
| OI-07.4 | ⚪ | Unused typed `PeerExchange` protocol on stream 1 |
| OI-07.5 | 🟠 | DIVINE framing (see above) |
| OI-07.6 | 🟡 | Control responses not self-describing; no timeout |
| OI-07.7 | 🟡 | PUBLISH Ok ≠ stored |
| OI-07.8 | 🟡 | Divination has no convergence termination or per-query timeout |
| OI-07.9 | ⚪ | "Bucketed" peer table; dead quarantine path |
| OI-07.10 | 🟡 | Tribute difficulty not advertised; replayable within the epoch window; capped at 24 |
| OI-08.1 | 🟡 | Watchword text rules are implementation-defined |
| OI-08.2 | 🟡 | VeilKey unused |
| OI-08.3 | 🟡 | Consecration validation gaps and message ordering |
| OI-08.4 | 🟡 | Sanction vs Sealed undefined |
| OI-08.5 | 🟡 | No wire encoding for ChannelDescriptor |
| OI-08.6 | 🟡 | Roles, serial and policyVersion undefined; ascension ignored in admission |
| OI-08.7 | ⚪ | AscensionLink unversioned; "accepted schism" unimplemented |
| OI-08.8 | 🔴 | Admission reflection (see above) |
| OI-08.9 | 🟠 | Admission is not signalled |
| OI-08.10 | 🟠 | Decree lifetime at the holder's maximum; no republish |
| OI-09.1 | 🟡 | Veil: random nonces, one key both directions, no AD, no rekey |
| OI-09.2 | 🟡 | Author signature over a re-encoding (malleable) |
| OI-09.3 | 🟡 | Epistle type byte unauthenticated; messageId length unchecked |
| OI-09.4 | 🟡 | Whether receivers MUST attest |
| OI-09.5 | 🟡 | Channel Conduit unversioned |
| OI-09.6 | 🔴 | Reliquary path not validated at decode; OS-dependent rules |
| OI-09.7 | 🟡 | Pageant schedule depends on floating point |
| OI-10.1 | 🟠 | No Bech32 decoder or decode rules |
| OI-10.2 | 🟡 | Empty Shrine target tag → primary Shrine |
| OI-10.3 | 🟡 | One bad rite frame ends the whole visit |
| OI-10.4 | 🟡 | Oracle: no field-length limits, no request id |
| OI-10.5 | 🟡 | Auspice topic length in UTF-16 units; Depart not acknowledged |
| OI-10.6 | 🟡 | Relic manifest: author not verified; path not validated |
| OI-10.7 | 🔴 | Browser Pilgrimages bypass the visit caps; Toll may bind no address |
| OI-11.1 | 🟡 | LanPresence port unchecked; IPv4 only |
| OI-11.2 | 🟡 | NAT-PMP deviations from RFC 6886 |
| OI-11.3 | 🟡 | Hole punch matches IP only; IPv4 only |
| OI-11.4 | 🔴 | Ferryman privacy (see above) |
| OI-11.5 | 🔴 | Ferryman replay (see above) |
| OI-11.6 | 🟡 | Ferryman limits, fence and version; design doc drift |
| OI-11.7 | ⚪ | TOFU fingerprint: hex vs Bech32 |
| OI-11.8 | 🟡 | Onion peers appear as loopback |
| OI-11.9 | 🔴 | TorOnly clearnet leaks (Ferryman, LAN, punch, WebRTC block) |
| OI-11.10 | 🟠 | Browser host/port pairing undefined; WebRTC credentials not stable across restarts |
| OI-11.11 | 🔴 | Browser connections bypass the fence and caps |

## Documentation drift found while drafting

These need a docs fix, not a protocol decision.

| Where | Says | Code does |
|---|---|---|
| `README.md` | Consecration is a "Noise channel handshake" | HKDF key confirmation, not Noise |
| `README.md` | `Watchword.Parse` | only `TryParse` / `Generate` exist |
| `README.md`, `ROADMAP.md` | "bucketed peer table" | trust categories, not distance buckets |
| `ROADMAP.md` | "accepted schism" | detection only |
| `SECURITY-REVIEW.md` | Toll "specified but not implemented"; out-of-range stream id no longer kills the mux | the Toll is implemented; the mux is still killed |
| `design/ferryman.md` | status "planned"; RENDEZVOUS_* and PUNCH messages; Brokered beacon; PoW; Tor fallback; relay never learns either Sigil | implemented with different messages; Relay beacon; no PoW; no Tor fallback; D's Sigil is revealed |
| `design/webrtc-clients.md` | SIPSorcery; stable ICE credentials; Toll drawn as one message | CupriWebRTC; credentials regenerated per start; Toll is a two-message exchange |
| `design/shrines.md`, `design/websites-l2.md` | Oracle "over Conduit"; a "session kind Shrine" marker; streamed bodies; Noise NK | Oracle has its own stream; no such marker; whole bodies ≤ 192 KiB; Noise XX |
| `design/transports-and-limits.md` | the reliable rites recover dropped frames; Arcanum Conduit 16 MiB | only Epistle+Vigil can recover, and the Vigil is not wired; 16 MiB − 64 KiB |
| `src/CupriNet.Marks/README.md` | 14 components; Conduit unversioned | 15 components; Conduit versioned on the Pilgrimage path |
| `CupriNode.Shrine.cs:43` | "v1 hosts one Shrine per node" | multiple Shrines |
| `CupriNode.Pageant.cs:225` | higher Sigil dials | lower Sigil dials |
| `CupriNodeOptions.cs:325` | LAN subnet broadcast | limited broadcast `255.255.255.255` |

## Closed

*(none yet)*
