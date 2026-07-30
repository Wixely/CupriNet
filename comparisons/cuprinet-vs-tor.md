# CupriNet vs Tor

**[Tor](https://www.torproject.org/)** (The Onion Router) is not really an *alternative* to CupriNet — it's an
**anonymity transport/network**, and CupriNet can run **over** it. This page compares them where a comparison is
meaningful (layer, trust model, relaying, what each actually gives you) and then explains how they **compose**.

> *Grounded in the Tor Project's documentation and the long-standing Tor design (onion routing, directory
> authorities, v3 onion services). Checked 2026-07. These are stable fundamentals, not a moving target.*

## TL;DR

Tor **anonymizes connections** — it hides your IP/location by relaying your traffic through volunteer relays, and
it lets you host **onion services** reachable without revealing a server IP. It is a *transport and network*, not
an application protocol: you still run something on top. CupriNet is that "something on top" — an app-layer
private-communication library — and it can use the **real Tor network** as one of its transports (via
[CupriTor](https://github.com/Wixely/CupriTor), a managed Tor client). So the honest framing isn't "CupriNet vs
Tor" but **"CupriNet over Tor"**: Tor supplies IP-level anonymity; CupriNet supplies identities, channels,
messages, and metadata resistance on top.

## What Tor is

- **Onion routing.** Your traffic is wrapped in layers of encryption and sent through a **3-relay circuit**
  (guard/entry → middle → exit). Each relay peels one layer and knows only its immediate neighbours, so no single
  relay sees both who you are and where you're going.
- **Onion services (`.onion`).** A **v3 onion address is itself an ed25519 public key** (self-authenticating — no
  CA). A client and service meet via **introduction points** and a **rendezvous point**, so an onion connection is
  ~6 hops and **both** parties' IPs stay hidden.
- **Trust root: directory authorities.** A small, hardcoded set (≈9) of trusted **directory authorities** vote
  hourly on the **consensus** — the signed list of relays. Clients trust that consensus. It's decentralized in
  *operation* (thousands of volunteer relays) but has a **semi-centralized trust/bootstrap root**.
- **Threat model.** Strong anonymity against a **non-global** adversary. Tor **explicitly does not** defend
  against a global passive adversary that can watch both ends of a circuit and **correlate traffic** by timing/
  volume — a documented, fundamental low-latency limitation.
- **Implementation.** The reference `tor` is **C** (BSD-3-licensed); **Arti** is the Rust rewrite. Run by **The
  Tor Project** (US nonprofit); ~20 years old, millions of daily users, thousands of relays.

## Side by side

| | Tor | CupriNet |
|---|---|---|
| **What it is** | An **anonymity network / transport** (anonymized TCP + onion services) | An **application-layer** comms library (identities, channels, messages, files) |
| **Layer** | Below your app — you run a protocol *over* it (SOCKS or an onion service) | The app protocol itself — no virtual IP, no SOCKS |
| **Anonymity mechanism** | **Onion routing** over volunteer relays (hides IP/location) | **Native**: pseudonymity, per-channel unlinkability, traffic-analysis cover — but **not IP-hiding by itself** |
| **Relaying** | **Fundamental** — anonymity *comes from* relaying through 3+ hops | **L2 content is never relayed by CupriNet nodes**; L1 metadata may hop |
| **Trust root** | **Directory authorities** (≈9 trusted signers) + volunteer relays | **None** — discovery via links / same-LAN / member introductions |
| **Hidden-service analogue** | **v3 onion services** (self-authenticating `.onion`) | Uses Tor's onion services (via CupriTor) for its onion transport |
| **What you get** | Anonymized transport; you still build the app | The whole app layer; borrow Tor for IP anonymity |
| **Language** | **C** (reference) / **Rust** (Arti) | **100% managed C#** (.NET 10) |
| **License** | BSD-3-Clause | MIT |
| **Maturity** | Production, ~20 yrs, millions of users | **Pre-1.0**, tested, unaudited, no public network yet |

## How they compose (the important part)

CupriNet's `CupriTor` transport is a **managed Tor client that joins the real Tor network** — it fetches the
consensus, builds circuits through actual Tor relays, publishes its **own v3 onion service**, and dials peers'
`.onion` addresses. So enabling Tor in CupriNet (dual-stack or onion-only) means your CupriNet traffic **rides
Tor**. What each layer contributes:

- **Tor gives CupriNet:** IP/location hiding, NAT-free reachability via onion services, and Tor's relay network —
  none of which CupriNet provides on its own.
- **CupriNet gives you on top of Tor:** authenticated channels, per-channel identities unlinkable from your node,
  structured messaging/file transfer, versioned handshakes, and metadata-resistance (cover traffic) — none of
  which Tor provides.

This is why they're better read as a **stack** than a rivalry. The rough analogy: *Tor : CupriNet ≈ a VPN/anonymity
layer : the messaging app you run over it.*

## The one nuance worth stating plainly

CupriNet's **"L2 content is never relayed"** invariant is about the **CupriNet overlay** — no *CupriNet* node
carries your channel bytes. When you run CupriNet **over Tor**, those bytes still traverse **Tor's** circuits
(Tor relays carry the onion-encrypted, then Noise-encrypted, stream). That's not a contradiction: no third
*CupriNet* participant relays or can confirm your session; Tor is an orthogonal transport you opted into for IP
anonymity.

## Clearnet CupriNet ≠ "no privacy"

CupriNet runs on **clearnet by default** — no Tor required. Clearnet does reveal your IP to a peer you connect to
directly, and hiding your IP is squarely **Tor's job**. But it would be wrong to read "no Tor" as "no privacy":
CupriNet provides real, **non-IP** anonymity properties that Tor by itself does not.

- **Pseudonymity.** Your identity is a *Sigil* (a public-key hash) — no account, no registration, nothing tied to
  a real-world identity. The overlay never asks who you are.
- **Unlinkability.** Your channel identities can be **cryptographically separate** from your overlay Sigil, so an
  observer can't tie "this node on the network" to "a member of that channel."
- **Opaque advertisements.** Channel adverts carry only a **rotating, per-epoch token (Glyph)** — never the
  channel name or a stable id — so neither an observer nor the node storing the advert learns which channel it is.
- **Cover traffic / fuzzing.** Optional decoys blur what is real: **gossip fuzzing** on the overlay, **hot fuzz**
  (long-lived decoy connections), **effigies** (decoy channel sessions), and **pageants** (fake groups). To an
  on-path observer, genuine connections and sessions are mixed in with plausible fakes.
- **Direct-only content.** No CupriNet node relays your channel bytes, so no intermediary can record — or even
  confirm — that a conversation happened.

Together these give a form of **plausible deniability**. To be precise about *what kind*: this is **not** the
cryptographic message-repudiation of OTR (CupriNet messages can be author-signed), but **traffic- and
association-level** deniability:

- an observer who watches your connections **can't tell which are real** and which are decoys (cover traffic), so
  you can plausibly deny that any given connection carries real communication; and
- an observer **can't prove which channels you belong to** (unlinkable identities + opaque, rotating adverts), so
  you can plausibly deny association with a particular group.

So on clearnet an observer may see your IP and *that* two addresses have a connection — but not **what** it is,
**whether** it's real rather than a decoy, or **which channel** it belongs to. Add **Tor** (dual-stack or
onion-only) and you additionally hide the IP/location; the two layers stack.

*Caveat:* the cover-traffic machinery is real but **young and unproven** (pre-1.0, unaudited) and it costs
bandwidth — treat its traffic-analysis resistance as a design goal still being hardened, not a guarantee.

## Maturity — Tor is vastly ahead here

No contest: Tor is two decades of production hardening, formal research, funding, and a global relay network with
millions of daily users; its anonymity properties (and their limits) are well studied. CupriNet is pre-1.0,
unit-tested but **not security-audited**, with **no public network of its own** — and for IP anonymity it
deliberately **relies on Tor** rather than reinventing it. Treat CupriNet's *native* traffic-analysis cover as
young and unproven; treat the Tor layer under it as the mature part.

## When to think about which

- **You just need to anonymize connections** (browse, host a hidden service, tunnel an existing app): that's
  **Tor**, directly.
- **You're building an app that needs private authenticated group communication**: that's **CupriNet** — and if
  you also need to hide participants' IPs, run it in **Tor mode**, getting both layers at once.
- **You want maximum anonymity for CupriNet:** onion-only mode, so every CupriNet connection is a Tor onion
  circuit and no clearnet address is ever advertised.
