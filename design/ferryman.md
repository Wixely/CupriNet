# CupriNet Ferryman — design

**The Ferryman lets two NAT'd clearnet peers that can't reach each other establish a *direct*,
end‑to‑end‑encrypted connection, brokered by a mutually‑reachable public relay — without the relay ever
carrying channel content.** It is CupriNet's answer to "D is behind a home router; how does E connect to D?"

> Status: design (planned). This elaborates the one‑line Ferryman item in the [roadmap](../ROADMAP.md). It is
> the same pattern as **libp2p circuit‑relay‑v2 + DCUtR** and **WebRTC ICE/STUN**, adapted to CupriNet's
> direct‑only invariant and identity model.

## The core invariant (what makes this safe)

The relay does **signaling only** — rendezvous + hole‑punch coordination — then drops out; the D↔E session is
direct. So the relay **cannot break end‑to‑end security**:

- **No content.** L2 (Arcanum) bytes never traverse the relay; the direct D↔E session is Noise‑encrypted.
- **No impersonation.** E pins D's **Sigil**; the Conjunction handshake proves D holds the key. A malicious
  relay that substitutes a fake "D" fails the handshake.

What a relay **can** do is **observe metadata**: that E is trying to reach D, the timing, and **both peers'
IP addresses** (it brokered them). That's the entire risk surface — and it's why the client‑side trust
decision (below) is framed as *metadata exposure*, not content security.

## Non‑goals

- **Not a content relay / TURN.** If the hole punch fails (e.g. both endpoints behind symmetric NAT), the
  fallback is **Tor or give up** — never relay L2 content. This preserves "L2 is never relayed".
- **Not anonymity for the punched pair.** D and E learn each other's IPs — inherent to *any* direct clearnet
  P2P connection. Peers who need IP anonymity use **Tor onion mode** instead.
- **Not a global identity DHT.** Relays are opt‑in and discovered the normal way (links / gossip).

## Roles

- **Ferryman (relay/rendezvous)** — an opt‑in, publicly reachable node (e.g. the A/B seed VMs) that consents to
  advertising its address and brokering punches. Its address is already public, so sharing it leaks nothing.
- **Target (D)** — a NAT'd node that wants to be reachable. It **reserves** with one or more Ferrymen (keeps a
  connection open so the Ferryman can notify it) and advertises "reach me via ⟨Ferryman⟩" in its link.
- **Requester (E)** — the node trying to reach D.

## End‑to‑end flow

1. **D reserves.** D (behind NAT) dials out to Ferryman B, opens a persistent control connection, and asks B for
   a **rendezvous reservation** under a **blinded, rotating handle** derived from D's key (not its raw Sigil). B
   observes D's reflexive (public) address (STUN role) and agrees to notify D of incoming requests for that handle.
2. **D advertises.** D's intonation carries a **`Brokered` beacon** — "reach me by asking Ferryman B (Sigil +
   address) to broker handle #abc" — plus B's reachable record (B is public, so this is fine). D's own
   private/unreachable beacons are still stripped as today.
3. **E gets D's link, can't dial D directly**, but sees the `Brokered(via B)` beacon.
4. **E consents (TOFU).** The app notices D is only reachable via a relay and prompts the user to **trust
   Ferryman B** (see UX below). On approval, E connects to B.
5. **Rendezvous.** E connects to B with an **ephemeral, throwaway identity** (never E's long‑term Sigil) and
   sends a `RENDEZVOUS_REQUEST` for D's **handle** (with E's candidates + a nonce, priced by a small Tribute). B
   relays it to D as `RENDEZVOUS_NOTIFY`; D answers with its candidates (`RENDEZVOUS_ANSWER`), relayed back to E.
   B, having observed both public addresses, also supplies each side's reflexive candidate.
6. **Coordinated hole punch.** B signals both "punch now" (timing sync); D and E simultaneously send UDP to each
   other's candidate addresses → NAT holes open → a direct path forms.
7. **Direct, encrypted session.** Over that path D and E run the ordinary **Conjunction** handshake (Noise XX +
   identity binding), E pinned to D's Sigil. **B drops out.** L2 content now flows **directly**, end‑to‑end
   encrypted.
8. **Failure → fallback.** If the punch can't succeed, surface "couldn't establish a direct connection — try
   Tor." Never fall back to relaying content through B.

## Protocol additions (wire)

- **Ferryman capability flag** on the signed **PeerRecord**: "I broker rendezvous" (+ optional Wards: max
  sessions, reservation TTL). Propagated by gossip so relays are discoverable overlay‑wide, not only via a link.
- **`EndpointKind.Brokered`** beacon: `{ ferrymanSigil, ferrymanAddress, reservationHandle }` — "ask this
  Ferryman to broker this (blinded) handle." Advertised by D; interpreted by E.
- **Reachable relay records in the Intonation Litany.** Today the Litany is Sigils only; extend it to optionally
  carry the **full records of public Ferrymen** the inviter knows (addresses included). Only nodes flagged public
  Ferrymen are included — ordinary peers' addresses are never dumped. (This is the "opt‑in, and their IP is
  public anyway" point.)
- **Rendezvous control verbs** (L1 control, alongside `DIVINE`/`AUGURY`/`DECREE`):
  - `RESERVE` / `RESERVED` — D ↔ Ferryman: open/refresh a reservation.
  - `RENDEZVOUS_REQUEST` — E → Ferryman: reach target D; carries E's candidates + nonce + Tribute.
  - `RENDEZVOUS_NOTIFY` — Ferryman → D: E wants you; carries E's candidates.
  - `RENDEZVOUS_ANSWER` — D → Ferryman → E: D's candidates.
  - `PUNCH` — Ferryman → both: synchronised "go now" with a shared timing reference.
  All are small **signaling** messages; none carry L2 content.

## Reused machinery (already in the tree)

- **Reflexive address discovery** (`ReflexiveObserver`, reflexive exchange) — the Ferryman already learns peers'
  public addresses when they connect; that's the STUN role, no new component.
- **`HolePunch`** — the UDP punch primitive; the new part is that candidates/timing arrive via Ferryman
  signaling instead of a static mutual‑link exchange.
- **`NoiseConjunction`** — the direct handshake, with **Sigil pinning** (`expectedPeer`) already supported.
- **`Tribute`** (PoW) and the **Ward** quota pattern — for abuse control on rendezvous.
- **Subnet policy** — a Ferryman can still fence which subnets it brokers.

## Abuse controls (Wards)

- **Reservation required.** A Ferryman only brokers to targets that hold an active reservation with it — you
  can't ask a relay to punch at an arbitrary victim IP (no scanning/DoS amplification).
- **Priced requests.** `RENDEZVOUS_REQUEST` carries a small **Tribute** (PoW), bounding request floods.
- **Rate/quota limits.** Max concurrent brokered sessions, per‑requester and per‑target caps, reservation TTLs.
- **Target consent.** D can auto‑accept, or apply policy (only broker to peers I already know, etc.); the notify
  step lets D refuse.

## Client trust UX — TOFU `known_relays` (SSH‑style)

The protocol exposes the relay's identity; **the app decides trust**, once, on first use, and remembers it —
exactly like SSH `known_hosts`.

- **Prompt on first use.** When a target is only reachable via a relay, the app asks:
  *"⟨D⟩ isn't directly reachable. Public relay **⟨name⟩ `cupri1…`** can broker a direct connection. It will learn
  that you're connecting and both IP addresses, but **not what you say**. Trust this relay?"*
  Show the relay's **name (if any) + bech32 fingerprint** — the same alias/fingerprint scheme as
  [named nodes](../ROADMAP.md).
- **Remember to disk.** On approval, store `(relaySigil, name?, firstSeen, lastUsed)` in a **`known_relays`**
  file. Subsequent uses of an approved relay are **silent** — no re‑prompt.
- **New‑key notice.** A relay whose Sigil isn't in `known_relays` triggers the first‑use prompt (SSH's "the
  authenticity of host … can't be established").
- **Changed‑key warning.** If a relay advertises a **name** you previously pinned to a *different* Sigil →
  a loud warning ("⚠️ relay ⟨name⟩ is presenting a DIFFERENT key than before — possible impersonation"),
  mirroring SSH's "REMOTE HOST IDENTIFICATION HAS CHANGED." (This case exists only once relays carry names; by
  raw Sigil, a different key is simply a different relay and gets the first‑use prompt.)
- **Revoke.** Let the user remove an approved relay (re‑prompts next time).

Because the relay can't touch content or impersonate D, this store is about **which relays you'll expose
connection metadata to** — a deliberate, low‑stakes, one‑time choice per relay.

## What each side implements

- **Ferryman node:** `EnableFerryman` option; advertise the capability; accept `RESERVE`; broker
  `RENDEZVOUS_*` + `PUNCH`; STUN via existing reflexive observation; Wards. (Natural fit for **Lodestar** — a
  public keep‑alive node is the ideal Ferryman; add an `EnableFerryman` toggle there.)
- **Target (D):** maintain reservation(s); advertise the `Brokered` beacon; handle `RENDEZVOUS_NOTIFY` +
  consent; execute the punch.
- **Requester (E):** detect the `Brokered` beacon; the TOFU trust hook + `known_relays`; drive
  `RENDEZVOUS_REQUEST` and the punch; on failure, offer Tor.
- **App (CupriChat):** the consent prompt, fingerprint display, `known_relays` management.

## Implementation status (v1) & deferred hardening

**Implemented & tested** (`CupriNode.Ferryman.cs`, `FerrymanProtocol.cs`, `KnownRelays.cs`, CupriChat, Lodestar):
the relay/reserve/rendezvous protocol, the ephemeral-identity requester flow, the direct Sigil-pinned pairing,
the `KnownRelays` TOFU store + CupriChat confirm-and-explain dialog, and Lodestar-as-Ferryman by default.
**Reservations are authenticated** — the target signs the reservation with its Seal and the relay verifies the
signature *and* that `handle == hash(Sigil)`, so a handle (a hash of a public Sigil) **cannot be squatted or
hijacked**. NOTIFY pushes to a reserved target are serialized (a per-reservation gate), and a global
concurrent-session cap Wards the relay.

**Deferred hardening** (tracked; none are security-critical — the relay still can't read content or impersonate):
- **Reservation TTL / keep-alive** — today a reservation lives with its vessel; add a ~30 s keep-alive + ~90 s TTL
  so idle reservations are reaped promptly.
- **Per-source-IP / per-subnet caps** and a **Tribute (PoW)** on `RENDEZVOUS` — beyond the global session cap.
- **Fresh socket per incoming** on the target so it can serve concurrent requesters (v1 handles one per socket,
  then re-reserves).
- **Rotating handles** — the handle is currently a static (authenticated) hash; a passive relay can de-blind it
  by precomputing hashes of known Sigils, so blinding is weak. A rotating/relay-blinded handle would restore it.
- **Candidate hygiene** — filter loopback/RFC1918/unspecified candidates signaled via a relay, and address-family
  mismatches, to avoid reflected-punch noise and private-IP disclosure.
- **Name-based changed-key** warning is inert until relays carry names (Phase 3); by raw Sigil a changed key is
  simply a new relay (first-use prompt).

## Phasing

1. **MVP** — explicit relay: D reserves with a configured Ferryman and advertises the `Brokered` beacon; E
   brokers a punch through it; direct session; TOFU prompt + `known_relays`; Lodestar `EnableFerryman`. No
   discovery, no names.
2. **Discovery** — Ferryman capability gossiped; Litany carries public‑relay records so a NAT'd inviter's link
   points newcomers at a relay; pick a relay both peers can reach.
3. **Names** — integrate the alias/fingerprint layer so relays (and targets) can be shown/pinned by name, and
   the changed‑key warning becomes meaningful.

## Decisions

- **Symmetric‑NAT pairs → Tor‑or‑bust (strict).** When both peers are behind symmetric NAT the punch is
  impossible; the app falls back to **Tor**, or tells the user plainly. A Ferryman **never relays L2 content**
  (no TURN) — that invariant is absolute. It *may* relay **L1 signaling/metadata** (the Courier role), a separate
  allowed capability; the crisp line is **L1 yes, L2 never**.
- **Reservations: 2–3 diverse Ferrymen.** D reserves with 2–3 relays chosen for diversity (different
  operators/subnets, via the Temperance logic). **Keep‑alive ~30 s** (holds the NAT mapping open); **reservation
  TTL ~90 s** (≈3× keep‑alive; the relay drops stale ones). Relay‑side **Wards** cap reservations per relay and
  per source subnet.
- **Relay selection: trust/pin → nearest → spread.** E prefers a relay already in `known_relays`, then a
  **user‑pinned** relay, then the lowest‑RTT one (with light randomisation to avoid centralising). **Best
  practice for a private group: run your own public Lodestar with `EnableFerryman` and pin it**, so brokering
  metadata never leaves your trust domain — this is the recommended pattern.
- **Metadata minimisation.** D reserves under a **blinded, rotating handle derived from its key (not its raw
  Sigil)** — onion‑service style — so the relay sees "connections to handle #abc," not "to D's Sigil." E connects
  to the relay with an **ephemeral, throwaway identity** (never its long‑term Sigil) and just pays the Tribute; E
  reveals its real identity **only to D**, in the direct handshake the relay never sees. **IPs are unavoidably
  exposed to the relay in clearnet** (it brokered both) — for IP privacy use **Tor** (no Ferryman needed). Net:
  the relay learns *"IP X ↔ IP Y at handle Z, time T,"* not *"E's Sigil ↔ D's Sigil."*
