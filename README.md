# TV Torrent

A Samsung TV app (Tizen .NET, for Tizen 6.0 / 2021 models such as UA65AU7000) that browses
**your own** Torznab indexers (Prowlarr or Jackett on your home server) as poster rows, and
streams the chosen torrent **on the TV itself**. There is no server for streaming and no built-in
content source.

- **Browse:** rows of latest movies and TV shows, plus search. Shows open as seasons → episodes →
  releases. Optional TMDB key for posters.
- **Stream:** MonoTorrent downloads the pieces near the playback position first and serves them on
  `http://127.0.0.1:5555/…`, which the TV's own player plays. Seeking works.
- **Nothing kept:** each playback session gets its own temporary folder, deleted when you stop
  (and again on the next start if the app was killed).
- **Settings from your phone:** the TV serves a settings page on `http://<tv-ip>:8099/`.

## Install (one-time setup)

1. **Tizen Studio** (macOS): install it, then in *Package Manager* add **Samsung Certificate
   Extension** and the **TV Extensions**. The `tizen` and `sdb` command-line tools come with it.
2. **TV Developer Mode:** on the TV open *Apps*, press **1 2 3 4 5** on the remote, switch
   Developer mode **On**, enter this Mac's IP address, then restart the TV (hold Power).
3. **Samsung certificate:** in Tizen Studio open *Tools → Certificate Manager → + → Samsung*,
   choose **TV**, sign in with your Samsung account and let it read the TV's DUID (connect to the TV
   first with `sdb connect <tv-ip>`). This makes a profile that can install apps on your TV.
4. **Install:**
   ```bash
   ./install.sh <tv-ip>            # uses the active certificate profile
   ./install.sh <tv-ip> MyProfile  # or name one
   ```

## Use

1. Open **TV Torrent** on the TV. The first screen shows the settings address.
2. On your phone, open that address and add your indexer's Torznab URL and API key:
   - Prowlarr: `http://<server>:9696/<indexer-id>/api` (one entry per indexer), with Prowlarr's API key.
   - Jackett: `http://<server>:9117/api/v2.0/indexers/all/results/torznab/api`, with Jackett's API key.
3. Remote keys:

   | Where | Key | Action |
   |---|---|---|
   | Home | ▲ ▼ ◀ ▶ | Move between rows and posters |
   | Home | OK on the search bar | Type a search |
   | Player | OK | Pause / play |
   | Player | ◀ ▶ | Seek 10 s |
   | Player | ▲ ▼ | Seek 1 min |
   | Anywhere | Back | Go back (stops playback and deletes its pieces) |

## Limits

- The app streams only while it is open on screen. Tizen pauses third-party apps in the background.
- The temporary buffer needs free space on the TV for the file being watched; the limit is set in
  Settings (default 3000 MB). Bigger files (most 4K remuxes) are refused rather than filling the TV.
- 2021 Samsung TVs can't decode **DTS** audio. Those releases are marked in orange and sorted last.
- Start time depends on the swarm: a well-seeded release starts in seconds.

## Develop

```bash
dotnet build TvTorrent/TvTorrent.csproj          # builds the .tpk (signed with the test certificate)
cd Core.Tests && dotnet test                      # parser, catalog, settings page tests (offline)
cd Core.Tests && TVT_NET=1 dotnet test            # + end-to-end stream of an open-licence film over the real swarm
```

`TvTorrent/Core` has no Tizen dependencies and is tested on the Mac. `TvTorrent/UI` is the NUI
interface; it only runs on a TV or the Tizen TV emulator.
