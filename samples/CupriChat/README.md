# CupriChat

A small [Avalonia](https://avaloniaui.net/) desktop chat app built on the CupriNet library.

## What it does

- Runs a CupriNet node and joins a built-in public channel, **`CupriChat#Public`**
  (a deterministic Watchword every client derives from the same name).
- **Generate link** mints an invite link (`cuprinet://intone/…`) and renders it as a **QR code**.
- **Connect** takes someone else's link, pairs over the encrypted Noise transport, and Consecrates
  the public channel with them.
- Set a **username**; it travels with each message.
- The node also acts as an application-layer **hub**: messages it receives from one peer are relayed to
  its other peers, so everyone connected through an instance sees each other. (Channel *content* is never
  relayed at the transport layer — this rebroadcast is the app choosing to forward chat lines.)

## Run

```bash
dotnet run --project samples/CupriChat
```

Start two instances (on one machine, or two on the same LAN):

1. In instance A, click **Generate link** and copy the link (or scan the QR).
2. In instance B, paste it into **Connect to a link** and click **Connect**.
3. Type in the message box and press Enter — messages flow both ways, tagged with each username.

## Notes

- Transport is Noise-encrypted and mutually authenticated; the channel session key comes from the
  Consecration handshake over the shared Watchword.
- The invite link advertises the machine's LAN IPv4 so it is reachable on the local network; reflexive
  discovery adds a Mapped (public) beacon once peers agree on it.
- This is a demonstrator: a single node acts as the hub for the peers connected to it (a star), rather
  than a fully gossiped overlay.
