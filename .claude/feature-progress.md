# TV Torrent — Samsung Tizen .NET app (UA65AU7000, Tizen 6.0)

Goal: Netflix-style browser for the user's OWN Torznab indexer (Prowlarr/Jackett on home server);
streams the chosen torrent directly on the TV (MonoTorrent → local HTTP → Tizen player). No built-in
content source; temp pieces deleted when playback stops.

## Phases
- [x] 0. Feasibility: Tizen.NET.Sdk builds .tpk with plain `dotnet build`; MonoTorrent 3.0.2 streams via
      CreateHttpStreamAsync (Mac test: 4 MB first read 5.7 s, mid-seek 2.1 s, 19 peers).
- [x] 1. Core (no UI): settings, Torznab client, catalog grouping, TMDB posters, stream engine, phone settings page. 18 tests (Core.Tests), incl. TVT_NET=1 end-to-end stream of Big Buck Bunny via fake indexer → 302 magnet.
- [x] 2. NUI UI: home rows, detail (seasons/episodes/releases), settings, player + OSD, episode chooser. Builds to .tpk (Debug+Release). NOT run on a TV/emulator yet.
- [x] 3. README.md + install.sh (sdb connect, re-sign with Samsung profile, tizen install). Not executed: no Tizen Studio on this Mac.

## Notes
- Web seeds are slow in MonoTorrent (≈15 KB/s vs 1.7 MB/s direct) — don't rely on them.
- Many failures are EncryptionNegotiationFailed → prefer plaintext first.
- DHT: MonoTorrent 3.0.2 bootstraps only from router.bittorrent.com (no reply here) → stuck Initialising.
  Fixed by seeding cache/dht_nodes.cache with dht.libtorrent.org etc. (StreamEngine.CreateAsync): Ready in 5 s.
- Review fix: open/close serialized (SemaphoreSlim) + per-session piece dir; a late close no longer wipes the next stream.

## Handover (2026-10-23)
Unverified until run on the TV: NUI layout/focus, IME behaviour of the search TextField, Tizen Player on
http://127.0.0.1 (MonoTorrent stream), HttpListener on :8099 under the TV sandbox, free space on the TV.
Next: user installs Tizen Studio + Samsung cert, runs ./install.sh <tv-ip>; then fix what the device shows.
