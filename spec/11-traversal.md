# 11. Traversal and alternate transports

> **Status: Draft.** Describes the reference implementation at commit `8a35e12`. Rationale:
> [design/ferryman.md](../design/ferryman.md), [design/webrtc-clients.md](../design/webrtc-clients.md).

## 11.1 LAN discovery

Off by default. Each node periodically broadcasts a signed presence datagram:

| Parameter | Value |
|---|---|
| Destination | `255.255.255.255:43821` (IPv4 limited broadcast, not multicast) |
| Interval | 5 s |
| Freshness window | ±120 s |

The datagram is a 06 §6.2 envelope whose body is:

| Field | Type |
|---|---|
| version | `u8` (CupriMark `lan-presence`) |
| network | `string` |
| sealPublicKey | `bytes<len>` (32) |
| listenPort | `varuint` (the node's TCP port, 1–65,535) |
| issuedAt | `u64` (Unix seconds) |

The receiver silently drops a datagram unless, in order:

1. it decodes;
2. its version is accepted;
3. `network` equals the receiver's own;
4. the signature verifies;
5. `|now − issuedAt| ≤ 120 s`;
6. the sender is not itself.

The peer's endpoint is the datagram's **source IP** together with `listenPort`. The receiver then dials it and runs
the Conjunction, pinned to the advertised key.

*Reference:* `src/CupriNet.Traversal/LanPresence.cs`, `src/CupriNet.Traversal/LanDiscovery.cs:28-100`.

> **Open (OI-11.1):** `listenPort` is not range-checked, the version is checked before the signature, and the
> broadcast is IPv4-only.

## 11.2 NAT-PMP

Port mapping follows RFC 6886, which is informative here; CupriNet adds no wire format. The reference node maps
**TCP only**, renews at half the lifetime (default lifetime 3,600 s), and advertises the result as a Mapped beacon if
the external address is publicly routable.

> **Open (OI-11.2):** the RFC 6886 retransmission schedule is not implemented, mappings are not removed on shutdown,
> and hole-punch sockets get no UDP mapping.

## 11.3 Reflexive endpoint

On stream 5 of a Channel session (07 §7.2), each side tells the other the address it observed for it:

| Field | Type |
|---|---|
| address | `bytes<len>` (4 or 16 bytes; IPv4-mapped IPv6 is sent as 4 bytes) |
| port | `varuint` (≤ 65,535) |

The initiator sends first, with a 5 s timeout. Only the initiator records what it learns. A node advertises a Mapped
beacon only on a quorum:

- the same `address:port` reported by at least 2 distinct reporters;
- from at least 2 distinct subnets (IPv4 /24, IPv6 /48);
- with a combined weight of at least 2.

A report is ignored if the observed address is not publicly routable.

*Reference:* `src/CupriNet.Traversal/ReflexiveEndpoint.cs:17-193`.

## 11.4 UDP hole punching

A hole-punch probe or acknowledgement is exactly 33 bytes:

| Offset | Size | Field |
|---|---|---|
| 0 | 1 | type: `0xF1` PROBE, `0xF2` ACK |
| 1 | 16 | sessionId |
| 17 | 16 | nonce |

Each side sends a PROBE (with its own random nonce) to every candidate of the other side every 50 ms.

- When a side receives a PROBE whose sessionId matches and whose source IP is one of the peer's candidates, it
  replies with an ACK that echoes the probe's nonce, sent to the probe's source endpoint.
- The punch succeeds when a side has both received a PROBE and received an ACK of its own nonce. The confirmed
  endpoint is the source of that ACK.

The socket then carries reliable UDP ([03 §3.4](03-transport.md)) and the normal Conjunction. Hole-punch type values
lie outside reliable-UDP's command range, so stray probes are ignored there.

| Use | sessionId | Timeout |
|---|---|---|
| Mutual link | `SHA-256(lower_sigil ‖ higher_sigil)[0..16]`, ordered bytewise; the lower Sigil initiates the Conjunction | 30 s |
| Ferryman | the relay-issued nonce (§11.5) | 15 s |

*Reference:* `src/CupriNet.Traversal/HolePunch.cs:26-215`.

> **Open (OI-11.3):** probes are matched on IP only, not port. Probes are unauthenticated by design; the Conjunction
> that follows authenticates the peer. Hole-punch sockets are IPv4-only.

## 11.5 Ferryman (brokered hole punch)

A Ferryman is an opt-in public relay. It introduces two NAT'd peers so they can hole-punch, then drops out. It
**never carries channel content.** If the punch fails, there is no fallback through the relay.

Roles:

- **D** is the target. It holds a reservation at the relay.
- **E** is the requester. It wants to reach D.

The relay learns both peers' addresses and that they were introduced. It learns **neither peer's Sigil**: both
connect with a throwaway identity, E names D only by a handle, and D proves it owns that handle with a key made for
this relay alone.

Both D and E run a normal Conjunction with the relay and send session kind `5` (07 §7.1). All messages are on
stream 6.

### 11.5.1 Rendezvous key and handle

For each relay it reserves with, D holds an Ed25519 **rendezvous key pair**. It is unrelated to D's Seal, and D
MUST use a different one for each relay, so that two relays cannot link their reservations. D SHOULD keep it across
restarts, so that links already handed out stay usable.

```
handle = SHA-256("cuprinet/ferryman/handle/v2" ‖ rendezvous_public_key)[0..16]      ; label raw ASCII
```

E cannot compute the handle. It learns it, with the relay's Sigil and address, from the relay-route block of D's
Intonation (06 §6.4.4).

### 11.5.2 Messages

Every message begins `u8 type ‖ u8 version`, where `version` is the CupriMark `ferryman` ordinal (currently 1). A
receiver MUST ignore a message whose version it does not accept.

| Type | Name | Direction | Body after the version byte |
|---|---|---|---|
| 1 | RESERVE | D → relay | `bytes<len>` handle (16) ‖ candidates ‖ `bytes<len>` rendezvous public key ‖ `bytes<len>` signature |
| 2 | RESERVED | relay → D | `u8` status (`0` Ok, `1` NotReserved) |
| 3 | RENDEZVOUS | E → relay | `bytes<len>` handle (16) ‖ candidates |
| 4 | OFFER | relay → E | `u8` status ‖ `bytes<len>` nonce (16) ‖ D's candidates |
| 5 | NOTIFY | relay → D | `bytes<len>` nonce (16) ‖ E's candidates |

A candidate list is `varuint n` (≤ 8), then `n ×` (`bytes<len>` address of 4 or 16 bytes, `varuint` port in
1–65,535).

```
signed_subject = bytes("cuprinet/ferryman/reserve/v2") ‖ bytes(handshake_hash) ‖ bytes(handle) ‖ candidates
signature      = Ed25519(rendezvous_private_key, signed_subject)
```

`handshake_hash` is the Noise handshake hash of the D↔relay connection the RESERVE is sent on.

### 11.5.3 Flow

1. D connects to the relay. An application SHOULD let the user approve the relay, identified by its
   Noise-authenticated Sigil, before D reserves.
2. D sends RESERVE. The relay MUST accept it only if the signature verifies under the supplied rendezvous key over
   **this connection's** handshake hash, and `handle` is the hash of that key. It then stores the reservation and
   replies RESERVED(Ok); otherwise RESERVED(NotReserved). The reservation lives as long as D's connection.
3. E reads a relay route from D's Intonation. An application SHOULD ask the user to approve the relay named in the
   route **before E connects to it**, and remember approvals trust-on-first-use (TOFU).
4. E connects to the relay, pinned to the route's relay Sigil (04 §4.3.4, check 6), and sends RENDEZVOUS with the
   route's handle.
5. The relay generates a random 16-byte nonce, sends NOTIFY(nonce, E's candidates) to D, and replies OFFER(Ok, nonce,
   D's candidates) to E. With no reservation, the relay replies OFFER(NotReserved, zeros, []).
6. D and E hole-punch (§11.4) with `sessionId = nonce`. E then runs the Conjunction **pinned to D's Sigil**.

Because the reservation signature covers the handshake hash, a RESERVE observed on one connection cannot be used on
another.

*Reference:* `src/CupriNet.Hosting/FerrymanProtocol.cs`, `src/CupriNet.Hosting/CupriNode.Ferryman.cs`.

> **Resolved (OI-11.4):** D used to sign its reservation with its Seal and send the public key, which told the relay
> D's Sigil; the handle was a plain hash of the Sigil. D now reserves under a per-relay rendezvous key. The relay
> still sees D's address, so if D also publishes that address in its PeerRecord the relay can match the two.
>
> **Resolved (OI-11.5):** the reservation signature covered nothing about the connection, so a relay that had seen a
> RESERVE could replay it at another. It now covers the handshake hash.
>
> **Open (OI-11.6), partly resolved:** Ferryman now has a CupriMark version, and the punched Vessel is subject to the
> subnet fence. Still open: the relay has no deadline for a first frame or for idle reservations, and no per-source
> limits or Tribute; brokered candidates are not filtered (loopback is always sent).
> [design/ferryman.md](../design/ferryman.md) still describes a capability flag, PoW and a Tor fallback that are not
> implemented.
>
> **Open (OI-11.7):** the TOFU fingerprint the reference app shows is grouped hex, while the docs say Bech32
> `cupri1…`. Pick one for user-facing consistency.
>
> **Open (OI-11.12):** a stored contact (KnownPeer) keeps beacons but no relay route, so reconnecting to a peer
> through its relay needs a fresh link.

## 11.6 Tor

A node MAY publish a v3 onion service. It advertises it as an Onion beacon (06 §6.3):

- host is the 62-character `<base32>.onion` name;
- the reference virtual port is 43820.

The Vessel over a Tor stream uses byte-stream framing (03 §3.1).

In **TorOnly** mode a node advertises only its onion beacon and dials nothing over clearnet. It MUST NOT open a
TCP connection or send a hole-punch probe from its own address, MUST NOT hold a Ferryman reservation, and MUST NOT
publish a WebRTC endpoint block. A link restricted to onion beacons MUST NOT carry a WebRTC endpoint block
either.

> **Open (OI-11.8):** onion peers arrive from `127.0.0.1` through the reverse proxy, so the Toll cookie and the subnet
> fence both see loopback.
>
> **Resolved (OI-11.9):** in the reference implementation the Ferryman, mutual-punch and LAN dial paths were not
> guarded in TorOnly mode, and a WebRTC block could ride in an onion-only link. All are now refused or omitted.

## 11.7 WebRTC (browser peers)

A node MAY accept browser peers over a WebRTC DataChannel with **no signalling server**. The browser reads the
node's WebRTC endpoint block (06 §6.4.3) from its Intonation and builds the SDP answer itself.

The node's side:

| Parameter | Value |
|---|---|
| ICE | ICE-lite |
| DTLS role | passive, DTLS 1.3 (1.2 fallback) |
| DTLS certificate | self-signed ECDSA P-256, fingerprinted with SHA-256 |
| SCTP port | 5000 |
| Max message size offered | 262,144 bytes |

The browser's side (informative):

- SDP attributes: `a=ice-lite`, `a=setup:passive`, `a=sctp-port:5000`, `a=max-message-size:262144`.
- `a=fingerprint` is built from the block's algorithm and fingerprint.
- One host candidate is built from a beacon's address and the block's `port`.
- The DataChannel is in-band (DCEP), ordered and reliable. Its label and protocol are not significant.

Each DataChannel is one message-family Vessel (03 §3.2). Data is sent with PPID 53 (binary); the node ignores the
PPID on receipt.

If the node hosts any Shrine, every browser DataChannel is treated as a Pilgrimage (10 §10.2). Otherwise it is
treated as a channel pairing.

> **Open (OI-11.10):** which beacon host a browser pairs with the block's UDP port is not defined. Beacon ports are
> TCP ports, and the default Host beacon can be `127.0.0.1`. ICE credentials and the certificate change every time
> the transport is constructed, so a minted link stops working after a restart.
>
> **Resolved (OI-11.11):** browser connections used to bypass the subnet fence, the handshake concurrency cap and the
> Pilgrimage caps (also OI-10.7). They are now admitted on the same terms as TCP callers.
