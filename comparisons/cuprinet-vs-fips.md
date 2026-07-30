# CupriNet vs FIPS

**[FIPS](https://github.com/jmcorgan/fips)** — *the Free Internetworking Peering System* — is much closer to a
Tailscale/Yggdrasil-style **encrypted IP mesh** than to CupriNet. They share a lot of plumbing (Noise, key
identities, NAT traversal, no DHT, Tor-capable) but sit at **different layers** with **different trust models**.

> *Fact-checked against the FIPS README at `v0.5.0-dev` (2026-07). FIPS facts in quotes are from that README.*

## TL;DR

FIPS is a **Rust, Nostr-identified, routed encrypted IP mesh** — an infrastructure-free *network* you run other
apps over. CupriNet is a **managed-C#, application-layer private-messaging fabric** with **direct-only** content
and **native anonymity** — you build apps *with* it. **FIPS replaces your network; CupriNet is what your app
talks through.** The overlap is real; the layer, the direct-vs-relayed stance, and the discovery-infrastructure
dependency are where they part.

## What FIPS is

"A self-organizing encrypted mesh network built on Nostr identities, capable of operating over arbitrary
transports without central infrastructure." Concretely, it's an **IP-layer** system: a **TUN interface maps each
remote npub to an `fd00::/8` address** (plus a built-in `.fips` DNS resolver), so ordinary IP applications run
over it unchanged. It's a **routed mesh** — "spanning-tree coordinates with bloom-filter-guided discovery" — with
**two layers of Noise**: "IK at the link, XK at the session," with periodic hitless rekey. Discovery is
**Nostr-relay-mediated** (NIP-59 gift-wrapped offers/answers) plus STUN hole-punching and mDNS. Transports: "UDP,
TCP, Ethernet, Tor, Nym (mixnet), and BLE." Written in **Rust**, MIT-licensed.

## Side by side

| | FIPS | CupriNet |
|---|---|---|
| **Layer / what it is** | **L3 IP mesh** — IPv6 TUN adapter + `.fips` DNS; run any IP app over it | **Application layer** — a messaging/session/file API; no virtual IP |
| **Topology** | **Routed mesh** — traffic hops *through* intermediate nodes (hence hop-by-hop Noise) | **Direct-only** — private (L2) content never passes through a third party |
| **Discovery** | **Nostr relays** publish endpoint ads + NIP-59 offers; STUN + mDNS | Links / same-LAN / member introductions; **no relay/server** (opt-in routed lookup) |
| **Identity** | Nostr **secp256k1/schnorr** npub — one identity across the mesh | **Ed25519**; pseudonymous, with **per-channel identities unlinkable** from the overlay id |
| **Anonymity** | Delegated to **transports** (Tor, Nym); npub is linkable; ads live on relays | **Native** unlinkability + traffic-analysis cover (decoy connections/convos/groups), Tor optional on top |
| **Encryption** | Noise **IK** (link) + Noise **XK** (session), hitless rekey | Noise **XX/IK** handshakes, Ed25519 identity binding, end-to-end, forward secrecy |
| **Versioning** | (n/a documented) | **CupriMark** range-negotiation + security-floor lifecycle across handshakes & documents |
| **Transports** | UDP, TCP, **Ethernet**, Tor, **Nym**, **BLE** | TCP + self-contained reliable-UDP + **Tor onion** (dual-stack or onion-only) |
| **Language / deps** | **Rust** (native, systems-level), edition 2024 | **100% managed C#** (.NET 10), no native deps |
| **License** | MIT | MIT |
| **Use case** | Overlay *or* ground-up networks with no ISP; fold devices/subnets into one network | Private group communication embedded in apps |

## Where they meaningfully diverge

**1. IP mesh vs application messaging.** FIPS is a *network substrate* — it hands out addresses and carries
arbitrary IP traffic transparently, can fold entire LANs into the mesh, and can even stand up a ground-up network
with no ISP (Ethernet/BLE). CupriNet is a *communication library* — apps call its API for authenticated sessions
and structured messages; there is no virtual interface. FIPS ≈ "encrypted internet replacement"; CupriNet ≈
"private messaging fabric embedded in your app."

**2. Routed-through-the-mesh vs strictly direct.** FIPS is a routed mesh, so your (end-to-end-encrypted) traffic
flows *through* intermediate nodes — which is exactly why it needs hop-by-hop encryption as well. Those relays
can't read the payload, but the topology and per-link metrics reveal *that* traffic flows between endpoints.
CupriNet's core invariant is the opposite: **L2 content is never relayed** — no intermediary carries it or can
even confirm the session happened. (CupriNet's L1 overlay *does* relay metadata — discovery/referrals/adverts —
but never channel content.)

**3. Discovery infrastructure.** FIPS leans on **Nostr relays** as its signaling backbone — federated servers
publishing endpoint advertisements and gift-wrapped connection offers. That's "no ISP, no central company," but
it isn't server-*free*: it depends on relay infrastructure, and publishing endpoint ads is a metadata surface.
CupriNet is more strictly serverless for discovery — links, same-network, and member-to-member introductions,
with nothing published to any relay (a routed lookup exists but is opt-in and reveals you are searching).

**4. Anonymity: native vs delegated.** FIPS reaches anonymity by routing over **Tor/Nym transports**, but its
identity is a single npub used across the mesh (linkable), and its endpoint ads sit on relays. CupriNet bakes
unlinkability into the design — per-channel identities separable from your network presence — plus built-in
cover traffic to resist traffic analysis, *independently* of whether you also run over Tor.

**5. Runtime & reach.** FIPS is Rust — a good fit for a kernel-adjacent IP adapter with ECN/kernel-drop detection
and multi-transport down to Ethernet and Bluetooth; genuinely systems-level. CupriNet is managed C#, trading that
OS integration for drop-in embeddability in any .NET app, no native deps, and easier auditability.

## Where they're kin

Quite a lot, actually — more than CupriNet shares with something like IPFS:

- **Self-sovereign key identities, no registration, no central authority** on both sides.
- **Noise** for transport security on both (FIPS IK/XK; CupriNet XX/IK).
- **NAT traversal** via STUN/UDP hole-punching, and **mDNS** local discovery, on both.
- **Multi-transport and Tor-capable** on both.
- **Neither uses a DHT** — FIPS uses spanning-tree + bloom filters + Nostr; CupriNet uses relationships and
  bounded sampled exchange. The "connect key-identified peers across networks, securely, without a DHT" spirit
  is shared.

## Maturity — where FIPS is ahead

Worth being straight about: **FIPS is further along operationally.** It reports the "core protocol works
end-to-end over UDP, TCP, Ethernet, Tor, Nym, and Bluetooth on a global, public test mesh of thousands of
nodes" (at `v0.5.0-dev`). CupriNet is **pre-1.0**, covered by a green unit-test suite but **without a public
network yet**, and **not security-audited**. Both list a **security audit** as a near-term priority, and both
are MIT. If you need something you can join *today* and run real traffic over, FIPS is the more proven system;
CupriNet is the more embeddable library with a stricter direct-only/anonymity stance still maturing toward 1.0.

## When to pick which

- **Pick FIPS** if you want an **infrastructure-free network** — addresses + DNS for arbitrary IP apps, joining
  devices/subnets across the internet (or with no ISP at all), and you're comfortable at the systems/Rust level.
- **Pick CupriNet** if you're **building an app** that needs private, authenticated, **direct-only** group
  communication with **native anonymity**, embedded in .NET with no native dependencies — and you don't want a
  virtual network interface or any discovery relays in the picture.
