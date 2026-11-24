# 3. Transport (Vessel)

> **Status: Draft.** Describes the reference implementation at commit `8a35e12`. Rationale and the full size-limit
> table are in [design/transports-and-limits.md](../design/transports-and-limits.md).

A **Vessel** is one bidirectional connection carrying **frames**. Each frame is tagged with a **stream id**. There
are two families:

| Family | Carriers | Frame boundary | Max frame |
|---|---|---|---|
| byte-stream | TCP, reliable UDP (§3.4), Tor | Codex frame codec (01 §1.4) | 16 MiB |
| message | WebRTC DataChannel | one DataChannel message | the channel's negotiated max message size |

After the Conjunction (04), every frame payload is a Noise transport ciphertext (§3.5).

## 3.1 Byte-stream frame

Content of one Codex frame:

| Field | Type | Notes |
|---|---|---|
| streamId | `varuint` | 0–65,535 |
| payload | `bytes<len>` | |

The payload is length-prefixed twice: once by the outer `u32` frame length and once by `bytes<len>`.

A receiver MUST reject a `streamId` above 65,535 and MUST reject a `payload` length that runs past the frame.

*Reference:* `src/CupriNet.Vessel/Vessel.cs:49-81`.

> **Open (OI-03.1):** bytes after `payload` inside a frame are ignored. Decide whether the inner length must exactly
> fill the frame. The double length prefix is also redundant; consider dropping one in a future version.

## 3.2 Message frame (DataChannel)

One DataChannel message is:

| Field | Type |
|---|---|
| streamId | `varuint` (0–65,535) |
| payload | remainder of the message |

There is no inner length and no fragmentation. An empty message is malformed. A sender MUST NOT send a message larger
than the channel's negotiated maximum. The interoperable floor is 64 KiB. CupriWebRTC offers 256 KiB.

*Reference:* `src/CupriNet.Vessel/DataChannelVessel.cs:56-82`.

> **Open (OI-03.2):** the reference sender does not check the size before sending.

## 3.3 Streams

There are no stream open, close, reset or flow-control messages. A stream exists from the first frame that carries
its id and lasts as long as the Vessel. Order is preserved within the Vessel, and therefore within each stream.

### 3.3.1 Stream id registry

| Id | Pairing (node ↔ node) | Pilgrimage (visitor ↔ Shrine) |
|---|---|---|
| 0 | Toll, Noise handshake, identity binding | same |
| 1 | peer exchange ([07](07-overlay.md)) | — |
| 2 | Consecration ([08](08-channels.md)) | — |
| 3 | Epistles **and** Effigy cover traffic ([09](09-rites.md)) | — |
| 4 | Conduit (Veil-sealed) | Conduit (unsealed) |
| 5 | reflexive endpoint ([11](11-traversal.md)) | Oracle |
| 6 | overlay control: session kind ([07](07-overlay.md)) | — |
| 7 | — | Auspice |
| 8 | — | Relic |

The same id carries different things in the two connection kinds, and the kinds never share a Vessel. Stream 3 is
deliberately shared by Epistles and cover traffic, so that the stream id does not distinguish real traffic from cover.

Ids 9–65,535 are unassigned. **Open (OI-03.3):** define a reserved or private-use range.

### 3.3.2 Receiver limits

A receiver MUST bound its resources per Vessel. The reference implementation:

- admits at most **64** distinct stream ids per Vessel. Frames for further new ids are dropped;
- queues at most **1,024** undelivered frames per stream. When the queue is full, the newest frame is dropped.

Drops are silent: the sender is not told. Only the Epistle/Vigil rite recovers from a drop. Every other rite either
detects loss itself (the Relic, by hash) or loses the frame.

*Reference:* `src/CupriNet.Vessel/VesselMux.cs:14-93`.

> **Open (OI-03.4):** the protocol has no close reason or error signal. A malformed frame on any stream, or a Noise
> authentication failure, tears down every stream on the Vessel with no indication to the peer.

## 3.4 Reliable UDP

Reliable UDP is a simple selective-repeat ARQ carried in UDP datagrams. Its byte stream carries byte-stream frames
(§3.1) unchanged.

### 3.4.1 Segment

| Offset | Field | Type | Meaning |
|---|---|---|---|
| 0 | cmd | `u8` | `1` PUSH · `2` ACK · `3` CLOSE |
| 1 | seq | `u32` | sequence number of this segment (0 for ACK) |
| 5 | una | `u32` | cumulative ack: the next sequence number the sender expects to receive |
| 9 | len | `u16` | payload length |
| 11 | payload | `bytes[len]` | PUSH only |

- MSS is 1,152 payload bytes, so the largest datagram is 1,163 bytes.
- Each direction starts at seq 0. Sequence numbers compare with 32-bit serial arithmetic (RFC 1982).
- CLOSE carries no payload, consumes one sequence number and is delivered in order as end-of-stream.
- Every segment's `una` acknowledges everything below it.

A receiver MUST ignore:

- a datagram shorter than 11 bytes;
- a datagram shorter than `11 + len`;
- a datagram whose `cmd` is not 1, 2 or 3. Values `0xF1` and `0xF2` are hole-punch probes sharing the socket
  ([11](11-traversal.md)).

A receiver MUST also ignore:

- a `una` beyond the highest sequence number it has sent (`una > snd_next`): it cannot be an honest
  acknowledgement;
- any sequenced segment that arrives after the peer's CLOSE has been delivered.

Segments outside `[rcv_next, rcv_next + 256)` are dropped but still trigger an ACK.

A listener MUST NOT create state for an unknown source unless the datagram is a well-formed segment.

*Reference:* `src/CupriNet.Vessel/ReliableArq.cs:21-226`.

### 3.4.2 Sender behaviour (informative)

| Parameter | Reference value |
|---|---|
| send window | 128 segments |
| receive window | 256 segments |
| RTO | starts at 100 ms, doubles on every (re)transmission, capped at 4,000 ms |
| retransmission limit | none |
| congestion control | none |
| close linger | 80 ms |

> **Open (OI-03.5), partly resolved:** the reference implementation now ignores an out-of-range `una` and data after
> CLOSE, and its listener requires a well-formed segment. Still open: reliable UDP has no connection id, handshake,
> checksum or authentication; CLOSE is sent effectively once and never acknowledged; there is no retry limit or idle
> timeout; and it is IPv4-only. Noise above it detects tampering, but only by killing the connection.

## 3.5 Noise transport framing

After the handshake (04), each frame's payload is replaced by:

```
payload' = ChaCha20-Poly1305(key = k_send, nonce = 0x00000000 ‖ u64le(n), ad = "", plaintext = payload)
```

`n` is a single counter for each direction across all streams. It starts at 0 after Split, increments by one per
frame, and is never sent. The **stream id stays in cleartext** outside the ciphertext.

- Senders MUST serialise frames so that nonce order matches wire order.
- A receiver MUST treat an authentication failure as fatal to the Vessel.
- An implementation MUST NOT use nonce `2^64 − 1`. There is no rekey; the Vessel must be replaced first.

*Reference:* `src/CupriNet.Conjunction/NoiseVessel.cs:29-49`, `src/CupriNet.Noise/NoiseStates.cs:36-60`.

> **Open (OI-03.6):** the stream id is **not authenticated** because the AD is empty. An on-path attacker can change
> a frame's stream id undetected. Proposal: `ad = varuint(streamId)` in the next Conjunction version.
>
> **Open (OI-03.7):** transport messages may be up to 16 MiB. That deviates from the Noise specification's 65,535-byte
> limit (Noise rev. 34 §3). State the deviation as normative, or fragment.
