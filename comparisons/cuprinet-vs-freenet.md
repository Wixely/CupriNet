# CupriNet vs Freenet

**[Freenet](https://freenet.org/)** (at freenet.org) is the **new** Freenet — Ian Clarke's ground-up rewrite
(formerly "Locutus"): *"a peer-to-peer platform for apps that no company controls."* It is a **decentralized
substrate you build apps *on***, whereas CupriNet is a **library you build apps *with***. They're both P2P and
serverless, but they solve different problems at different layers.

> ⚠️ **Not the classic Freenet.** The original Freenet anonymous datastore was renamed **[Hyphanet](https://www.hyphanet.org/)**
> in 2023; **freenet.org** now hosts this new project. This page is about the new Freenet.
>
> *Fact-checked against freenet.org and `github.com/freenet/freenet-core` (checked 2026-07): Rust, **AGPL-3.0**,
> ~3k stars, active. Architecture terms (contracts, delegates, observable key-value store) are from Freenet's
> own docs/talks.*

## TL;DR

Freenet is a **decentralized "shared state" platform**: a global, observable **key-value store** where each key is
a **WebAssembly contract** that defines what a valid value is and how it may change, routed over a small-world
network so apps run "with no servers behind them." You publish state into the network and others observe/update it
live. CupriNet is the opposite data philosophy: **no shared datastore** — **direct, ephemeral, member-to-member**
sessions where content is never stored in or relayed through the network. **Freenet is a decentralized web/OS;
CupriNet is a private-messaging fabric.** Both even ship a group-chat example — Freenet's **River**, CupriNet's
**CupriChat** — but what's underneath is opposite: shared contract-governed state vs direct private transport.

## What the new Freenet is

- **An observable key-value store.** Data lives *in the network*, replicated across peers; anyone with the key can
  read it, and subscribers get **real-time updates** when it changes — even if the original author is offline.
- **Contracts (WebAssembly).** A key is a contract that specifies **what counts as a valid value and who/how it
  can be updated** — so state is self-validating without a server or central authority.
- **Delegates.** Persistent user agents that act on your behalf and can hold secrets (think: your always-on
  background logic).
- **Small-world routing.** "Peers form a small-world network organized by location on a ring. Messages find their
  destination in just a few hops, scaling efficiently to millions of peers, no servers required."
- **Apps.** You ship decentralized apps (Rust/TypeScript → WASM) that "can't be taken down, don't track you, and
  need no servers." Flagship example: **River**, a group-chat app.
- **Focus.** **Decentralization and censorship-resistance** first ("no company controls"), with app-level privacy
  ("don't track you"). It is *not* positioned primarily as an anonymity network the way the original Freenet /
  Hyphanet (or Tor) is.
- **Runtime / license.** Rust core (`freenet-core`), **AGPL-3.0** (strong network-copyleft).

## Side by side

| | Freenet (new) | CupriNet |
|---|---|---|
| **What it is** | A decentralized **app platform / shared datastore** | An **application-layer** private-comms library |
| **You build apps…** | **on** it (deployed to the network) | **with** it (embedded in your .NET app) |
| **Data model** | **Global observable key-value store**; state lives in the network, replicated | **No shared store** — direct, ephemeral sessions; content never stored in the overlay |
| **Programmability** | **WASM contracts** define valid state + updates; **delegates** run logic | No in-network compute; logic lives in your app |
| **Persistence** | Published state persists/replicates; readable when you're offline | Nothing persisted in the overlay; you must be reachable to talk |
| **Topology** | **Small-world ring**, location-routed; data flows through the network | **Direct-only** L2 content (never relayed); L1 carries only metadata |
| **Real-time** | **Subscriptions** to contract state changes | Direct sessions (messages/data/files) between members |
| **Anonymity** | Not a primary goal (censorship-resistance + "no tracking") | Native unlinkability + cover traffic; **optional Tor** for IP anonymity |
| **Identity** | Cryptographic keys; no accounts/servers | Ed25519 Sigil; per-channel identities unlinkable from it |
| **Runtime / license** | **Rust**, **AGPL-3.0** | **100% managed C#** (.NET 10), **MIT** |
| **Maturity** | Active, funded nonprofit, public test network, example apps; pre-1.0 rewrite | Pre-1.0, tested, unaudited, no public network yet |

## Where they meaningfully diverge

**1. Shared persistent state vs direct ephemeral sessions.** This is the crux. Freenet is fundamentally about
**data that lives in the network** — you put contract-governed state in, it replicates, and anyone (with the key)
can read or subscribe to it, even when you're gone. CupriNet stores **nothing** in the overlay: it's a transport
for **direct member-to-member** conversations that exist only while participants are connected. Freenet answers
"where does decentralized *state* live?"; CupriNet answers "how do two parties *talk privately* with nothing in
the middle?"

**2. A programmable substrate vs a fixed protocol.** Freenet is a *platform*: WASM contracts and delegates let you
define new decentralized applications (feeds, forums, chat, marketplaces) whose rules the network enforces.
CupriNet is a *protocol/library* with a fixed job — pseudonymous overlay + authenticated direct channels — and
your application logic lives in your own code, not in the network.

**3. Routed-through-the-network vs direct-only.** Freenet's small-world routing means requests and data traverse
intermediate peers (and are cached along the way) — that's how a global store scales without servers. CupriNet's
core invariant is that **L2 content is never relayed** through any third node; only L1 metadata hops. Different
goals produce opposite stances on who touches your bytes.

**4. Findability & persistence vs non-discoverability.** On Freenet, anything you publish is **globally
addressable by its key** and persists in the network. CupriNet is the reverse: you're **not globally findable**
(reached via an expiring link / LAN / introduction) and nothing you send is retained by the overlay — good for
private, closed groups, poor for "publish something the world can fetch later."

**5. License & runtime.** Freenet is **AGPL-3.0** Rust — strong network-copyleft that can reach services built
around it; a deliberate "keep it free" choice. CupriNet is **MIT** managed C# — permissive and drop-in embeddable
in proprietary .NET apps. For *embedding a comms feature in a closed-source product*, that difference is decisive.

## Where they're kin

- **P2P and serverless**, no central authority, self-generated **key identities**, no phone/email registration.
- **Small-world / non-classical-DHT routing** rather than a global identity DHT (Freenet routes by location on a
  ring; CupriNet uses relationships + an opt-in referral lookup).
- **FOSS**, and both ship a **decentralized group-chat** example (River / CupriChat) — a convenient head-to-head
  for the "how do you do decentralized chat?" question, with opposite answers underneath.

## Maturity

Both are young in their current form, but **Freenet has more visible momentum**: a funded nonprofit, a public test
network, working example apps (River), and a large contributor community (~3k stars). It is, however, an
ambitious from-scratch rewrite still short of 1.0. CupriNet is earlier still — tested but **unaudited, with no
public network yet** — and narrower in scope (a comms library, not a platform). Neither is production-hardened;
Freenet is further along as *something you can deploy an app to today*.

## When to pick which

- **Pick Freenet** if you want to build a **decentralized application with shared, persistent, observable state**
  — social apps, forums, collaborative data, "can't be taken down" services — and AGPL + a Rust/WASM stack suit
  you.
- **Pick CupriNet** if you want to embed **direct, private, ephemeral communication** (authenticated channels,
  messaging, files) into a **.NET** app, with **nothing stored in the network**, metadata resistance, optional
  Tor, and **MIT** licensing — accepting it's a narrower, younger library, not a platform.
