# bitchat-Win

A Windows client for [bitchat](https://github.com/permissionlesstech/bitchat) -
a decentralized, serverless, account-free mesh messenger that works over
Bluetooth Low Energy and (optionally) Nostr relays. No phone numbers, no cloud,
no accounts.

Bitchat-Win is wire-compatible with the bitchat apps for iOS/macOS and Android.

> **Version:** 0.012
> **Status:** ALPHA

## ⚠️ Alpha - "AS IS"

This is an **early alpha** release. It is provided **"AS IS"**, without warranty
of any kind, express or implied, including but not limited to the warranties of
merchantability, fitness for a particular purpose, and non-infringement. See the
[LICENSE](LICENSE) file for the full text.

- Expect bugs, crashes, missing features, and unstable behavior.
- The protocol and on-disk formats may change without backward compatibility.
- Use it only on devices and data you are comfortable losing.
- It has not been audited and makes no security guarantees.

You run it entirely at your own risk.

## Features

### Bluetooth mesh (offline)

- Binary bitchat v1 BLE protocol: packets, DEFLATE, PKCS#7 padding, fragmentation.
- Noise XX (`Noise_XX_25519_ChaChaPoly_SHA256`) with persistent identity
  (X25519 + Ed25519), byte-compatible with the noise-c/noise-java layout.
- Signed packets (canonical encoding, fixed TTL=0, matching Android/iOS).
- Peer discovery (ANNOUNCE/TLV), private messages with delivery/read receipts.
- Multi-hop relay (TTL 7), dedup, adaptive relay probability.
- Gossip sync (REQUEST_SYNC) with GCS (Golomb-Coded Set) filters and automatic
  history backfill on connect.

### Nostr (internet)

- Geo-channels (kind 20000 ephemeral events, `g`/`n` tags), compatible with the
  mobile clients.
- BIP-340 Schnorr signatures, NIP-01 events, per-geohash identity.
- WebSocket relays with auto-reconnect and dedup.

### GUI (WPF)

- Per-peer conversations, global mesh, and geo-channels.
- Unread indicators, window/tray counters, minimize-to-tray.
- Delivery/read receipts and notifications when minimized.
- Voice messages (record, send as audio, play incoming voice).
- File/image transfer.

### Console (TUI)

A lightweight terminal client is included in `bitchat-windows/`.

## Requirements

- Windows 10 2004 (build 19041) or later.
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) to build from
  source.
- A Bluetooth adapter (peripheral role recommended, but not required).

## Build

```powershell
dotnet build -c Release
dotnet test .\BitchatWindows.Tests\BitchatWindows.Tests.csproj
```

## Run

```powershell
dotnet run -c Release --project .\BitchatApp
```

For the console client:

```powershell
dotnet run -c Release --project .\bitchat-windows -- --nick YourName
```

Your identity (private keys) is stored in `%LOCALAPPDATA%\bitchat\identity.bin`.
Delete that file to generate a new identity.

## Download

Prebuilt binaries for Windows x64 are attached to the
[v0.012 release](https://github.com/zavhozsf/bitchat-Win/releases/tag/v0.012).

## Structure

```
BitchatCore/           shared library: protocol, Noise, mesh, BLE, Nostr, voice
BitchatApp/            WPF GUI client (bitchat-gui)
bitchat-windows/       console (TUI) client (bitchat)
BitchatWindows.Tests/  xunit tests (protocol + Cacophony vectors)
```

## Roadmap

Work in progress and planned:

- **Live voice streaming (push-to-talk).** Voice is currently recorded and sent
  as an M4A file; the next step is real-time streaming of native
  `VoiceBurstPacket` frames by extracting raw AAC access units directly from
  Media Foundation (Source Reader, without re-muxing).
- **Nostr private messages** (gift-wrapped envelopes / kind 1059).
- **Persistent message history.** Gossip sync (REQUEST_SYNC) currently keeps its
  cache in memory only; add on-disk persistence across restarts.
- **Encrypted / password channels**, once the mobile clients re-enable them.
- **Wi-Fi Aware** transport.
- Broader BLE peripheral support and connection reliability.

## License

Released under the [MIT License](LICENSE). Third-party library licenses are
listed in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
