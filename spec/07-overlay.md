# 7. Overlay (Concordance, Layer 1)

> **Status: Draft.** Describes the reference implementation at commit `8a35e12`.

The overlay is a pseudonymous network shared by many unrelated groups. It carries only metadata: PeerRecords
([06 §6.5](06-identity-and-documents.md)), channel advertisements (Decrees, [08 §8.6](08-channels.md)) and
discovery referrals. **It never forwards application or channel content.** Every L1 operation is
request/response between two directly connected nodes. Nothing is routed hop-by-hop.

## 7.1 Session kinds

After the Conjunction ([04](04-conjunction.md)), the initiator sends one frame on **stream 6** whose payload is a
single **session-kind** byte:

| Kind | Name | Purpose |
|---|---|---|
| 1 | Channel | pairing toward an Arcanum channel ([08](08-channels.md)) |
| 2 | Control | overlay control requests (§7.3) |
| 3 | Effigy | decoy session carrying cover traffic only ([09 §9.7](09-rites.md)) |
| 4 | Pageant | decoy clique edge ([09 §9.7](09-rites.md)) |
| 5 | Ferryman | hole-punch brokering ([11](11-traversal.md)) |

The responder MUST close the Vessel if the first post-handshake frame is not on stream 6 or has an empty payload.

> **Open (OI-07.1):** the reference responder treats an unknown kind as Channel and ignores extra payload bytes.
> Proposal: close the Vessel on an unknown kind.

## 7.2 Channel-session preamble

On a **Channel** session, two best-effort exchanges follow the kind byte, in order:

1. **Reflexive endpoint** (stream 5): each side tells the other the address it observed for it ([11 §11.3](11-traversal.md)).
2. **Bootstrap** (stream 1): each side sends one PeerRecord document, its own, as the raw frame payload. The
   initiator sends first. The receiver verifies the record and admits it into its peer table. It MUST discard a
   record whose Sigil is not the Sigil the Conjunction authenticated for this peer.

> **Resolved (OI-07.2):** the reference receiver used to admit any validly signed record sent in the bootstrap,
> including a third party's. It now admits only the authenticated peer's own.
>
> **Open (OI-07.3):** if only one side runs the reflexive exchange, its reader consumes the bootstrap frame and
> both exchanges fail. Neither exchange is negotiated.
>
> **Open (OI-07.4):** `src/CupriNet.Concordance/PeerExchange.cs` defines a different, typed stream-1 protocol
> (`0x01` request / `0x02` sample) that nothing uses. Remove it or adopt it.

## 7.3 Control requests (stream 6)

On a Control session, the initiator sends requests and the responder answers each one in order. There is **no
request id**: the client MUST NOT send a second request before receiving the response to the first.

```
request  = u8 op ‖ op-specific body
response = op-specific body        ; no op echo, no status byte unless stated
```

| Op | Name | Request body | Response |
|---|---|---|---|
| 1 | DIVINE | `bytes<len>` target (32 bytes) | PeerRecords |
| 2 | PUBLISH | `bytes<len>` decree document ‖ `bytes<len>` tribute nonce | Status |
| 3 | LOOKUP | `varuint n` (≤ 8) ‖ `n × bytes<len>` glyph | Decrees |
| 4 | SAMPLE | `varuint k` | PeerRecords (k is clamped to 16) |
| 5 | PING | `varuint downPad` ‖ `bytes<len>` upPad | `0x00` ‖ `min(downPad, 4096)` zero bytes |
| 6 | PAGEANT | `bytes<len>` Pageant invitation ([09 §9.7](09-rites.md)) | Status |

| Response | Encoding |
|---|---|
| PeerRecords | `varuint n` (≤ 16) ‖ `n × bytes<len>` PeerRecord document |
| Decrees | `varuint n` (≤ 16) ‖ `n × bytes<len>` Decree document |
| Status | `u8`: `0` Ok, `1` Rejected |

A server answers an unknown op with Status Rejected. A client MUST verify every PeerRecord it receives and MUST
discard a list whose count exceeds 16.

*Reference:* `src/CupriNet.Hosting/OverlayControl.cs:44-285`, `src/CupriNet.Hosting/CupriNode.Overlay.cs:152-271`.

> **Resolved (OI-07.5):** the reference server used to read the first 32 raw bytes of the body, taking the length
> prefix as part of the key, so every DIVINE was answered for a different target. It now reads the target as
> `bytes<len>` and rejects any length other than 32.
>
> **Open (OI-07.6):** responses don't describe themselves: a Rejected status (`0x01`) parses as a list with one
> entry. An empty request gets no response at all. The client has no response timeout.
>
> **Open (OI-07.7):** PUBLISH replies Ok even when the store then refuses the Decree (expired, too long-lived,
> stale sequence, full). Ok means "verified", not "stored".

### 7.3.1 Server limits (informative)

| Limit | Reference value |
|---|---|
| requests per peer Sigil | 120 per 10 s (fixed window) |
| control connections per peer Sigil | 8 |
| control connections in total | 256 |
| handshakes in progress | 64 |

Over budget, the server closes the connection.

## 7.4 Routing keys and distance

Every node and channel has a 32-byte coordinate called its **Ascendant**:

```
node_ascendant    = SHA-256("cuprinet/ascendant/v1" ‖ seal_public_key)   ; label is raw ASCII, not length-prefixed
channel_ascendant = defined in 08 §8.2
distance(a, b)    = a XOR b, compared as a 256-bit big-endian unsigned integer
```

*Reference:* `src/CupriNet.Concordance/RoutingKey.cs:14-82`.

## 7.5 Discovery (Divination)

Iterative, referral-only lookup toward a target key:

1. Start from a seed set of known peers.
2. Each round, query the α = 3 closest peers not yet queried with DIVINE.
3. Each response contributes up to 8 referrals, verified PeerRecords. These are added to the candidate set but **not**
   to the peer table.
4. Stop after 24 peers have been queried, or when no candidates remain. The result is the 8 closest peers.

A server answers DIVINE with the 16 records in its table closest to the target. It never forwards the query.

*Reference:* `src/CupriNet.Concordance/Divination.cs:14-107`.

> **Open (OI-07.8):** there is no Kademlia-style "no closer node found" termination and no per-query timeout.

## 7.6 Gossip

Every 60 s, a node sends SAMPLE(16) to 3 peers chosen at random from its better-standing peers. It admits verified,
reachable records from the replies. The interval and fanout are local policy.

## 7.7 Peer table (informative)

The peer table is local and not visible on the wire. The reference implementation's behaviour matters for
robustness, but it is not interop:

- Peers are kept in trust categories, not distance buckets.
- The table holds at most 2,000 records, with at most 2 per IPv4 /24.
- Eviction comes only from the Strangers and Excommunicate categories.
- Reputation is local and never propagated.

> **Open (OI-07.9):** the README calls the table "bucketed", which suggests Kademlia; it is not. The quarantine path
> (Taint, Promote) is never invoked.

## 7.8 Tribute (proof of work)

A PUBLISH carries a proof of work bound to the Decree, to the node receiving it (the holder), and to a 10-minute
epoch:

```
epoch   = floor(unix_seconds / 600)
subject = decree_document ‖ holder_sigil (32) ‖ u64(epoch)
H       = SHA-256("cuprinet/tribute/v1" ‖ subject ‖ nonce)   ; label is raw ASCII, not length-prefixed
valid  ⇔ leading_zero_bits(H) ≥ difficulty
```

- The holder accepts `epoch ∈ {e−1, e, e+1}`.
- The nonce is 1–64 bytes; the reference solver emits an 8-byte big-endian counter.
- Difficulty is the holder's local policy (reference: 16) and is **not** sent on the wire.
- Verifiers clamp the required difficulty to at most 24.

*Reference:* `src/CupriNet.Concordance/Tribute.cs:18-83`, `src/CupriNet.Hosting/CupriNode.Overlay.cs:461-475`.

> **Open (OI-07.10):** a publisher cannot learn the difficulty a holder requires. Within the epoch window a proof can
> be replayed to the same holder. The cap of 24 silently weakens a stricter policy.
