# Auto AVDump

A [ShokoServer](https://shokoanime.me) plugin that automatically dumps unrecognized video files to AniDB through the server's built-in AVDump — the first of the two steps needed to register a file on AniDB.

## What it does

When the server's **automatic release search** for a newly imported video fails — the file's hash matches nothing on any enabled release provider — this plugin:

1. Stages the video in a batch.
2. Every 30 seconds, **dumps** the batch through the server's built-in AVDump — at most **10 videos per flush**, to keep each AVDump session short; any remainder is flushed on a later interval (the AVDump binary is downloaded and installed automatically on first use). AVDump reads each file locally, computes its hashes and parses its container, and uploads the dumped data — hashes (CRC32, ED2K, MD5, SHA1, TTH), the file size, and technical metadata (codecs, resolution, bitrate, audio/subtitle/chapter info) — to AniDB. The file itself is never uploaded.
3. Records the outcome per video in a small state file (`auto-avdump-state.json` in the server's configuration directory):
   - **Success** — the video is marked dumped and is never re-dumped.
   - **Failure** — the video is retried when a later automatic search fails for it, up to **three failed attempts**, after which the plugin gives up on it.
   - **Environment-level AVDump problems** (missing API key, invalid AniDB credentials, install failures, timeouts) are logged with actionable guidance and do **not** burn the per-video retry budget.

It has **no settings and no user interaction** — nothing to configure beyond what AVDump itself requires.

> **Dumping is only half the job.** Registering a file on AniDB is a two-step process: first the file is *dumped* (its hashes and technical metadata are registered with AniDB — what this plugin does), then the dumped file must be *added to AniDB* — assigned to a series/episode in the AniDB catalog. The second step is still manual: once a dump succeeds, add the file on AniDB to complete the registration.

### Why the file is not recognized immediately

A dumped file is not a registered AniDB file: until it is *added* to a series/episode (the manual second step), release providers have nothing to match it against. Once it has been added, a **later** automatic search (or a manual "Find release" in the UI) can match its hash and recognize the video. This plugin automates only the dump half; it never claims a match itself.

## Requirements

- A ShokoServer built against **Shoko.Abstractions 6.0.0-alpha.94**.
- At least one **release search provider enabled** with automatic matching on, so automatic release searches actually run (and can fail) in the first place.
- An **AniDB AVDump API key** on the server (`Settings → AniDB`, or the `ANIDB_AVDUMP_API_KEY` environment variable). Without it, AVDump cannot run; the plugin logs a warning and waits.
- The AVDump binary itself needs no manual install — the plugin triggers the server's built-in installer on first use.

## Installing

**From a repository URL** (server → plugins → add from URL), pointing at the live manifest on the `metadata` branch:

```
https://raw.githubusercontent.com/harshithmohan/shoko-plugin-auto-avdump/metadata/manifest.json
```

**Manually:** download the latest `AutoAVDump-vX.Y.Z-any.zip` asset from the [releases](https://github.com/harshithmohan/shoko-plugin-auto-avdump/releases) page and unzip it into the server's `plugins` directory (`C:\ProgramData\ShokoServer\plugins` on Windows, `/home/shoko/.shoko/Shoko.CLI/plugins` in the Docker image), then restart the server.

## Building

```sh
dotnet tool restore
dotnet build -c Release
```

The plugin is built for the portable (`any`) runtime — no runtime identifier is pinned.

## Repository layout

- `Shoko.Plugin.AutoAVDump.csproj`, `Plugin.cs` (identity and DI registration), `AutoAvdumpService.cs` (staging, batching, retry, state) — the plugin project, at the repository root.
- `manifest.json` — a **stub** of the plugin manifest. It carries the real identity (id, name, overview, authors, tags) but an empty release list. The manifest that is actually published — with releases, checksums and download URLs — lives on the **`metadata` branch**. The release workflow builds from the fetched live manifest, uploads the archive to the GitHub release, and commits the updated manifest back to that branch only; nothing is ever written to `main`. So: expect no release entries in the `manifest.json` on this branch, and point the server at the `metadata` branch copy.

## TODO

- Settings: an enable switch to turn the plugin on/off.
- Settings: a regex pattern matcher to filter which videos get dumped.
- Settings: configurable flush interval and retry limit (currently fixed at 30s / 3).
- Settings: configurable max batch size (currently fixed at 10).
