# ThemeForge

A Jellyfin plugin that finds theme songs for your movies and TV shows, scores every candidate it
finds, and assigns the confident ones automatically. Anything it is unsure about waits in a
review queue instead of guessing.

Requires **Jellyfin 10.11**.

## What it does

Point it at your library and it will, for each movie and series:

1. Work out what the item actually is — title, alternate titles, year, TVDB/TMDB/IMDb ids.
2. Build an ordered list of searches, most specific first.
3. Search YouTube with **yt-dlp** and collect candidates.
4. Score every candidate against ten independent rules, each of which explains itself.
5. Assign the winner if it is confident, queue it for you if it is not, and record either way.
6. Download the audio, **normalise it to a consistent loudness**, fade it, verify it plays, and
   write it as `theme.mp3` beside your media.

Everything it decides is recorded, so re-running is cheap and safe: settled items are skipped,
and anything you decided yourself is never touched again.

## Theme song volume

Themes downloaded from different uploaders arrive at wildly different volumes, and Jellyfin has
no theme volume control to compensate with — [jellyfin-web#3086](https://github.com/jellyfin/jellyfin-web/issues/3086)
is still open. A plugin cannot fix this from the browser either: Jellyfin's player re-reads its
saved global volume every time it creates a media element, so a volume set from a script is
overwritten moments later, and setting it at all leaks into your volume for normal playback.

ThemeForge fixes it in the file instead. Every theme is normalised to the same integrated
loudness (EBU R128, `-23 LUFS` by default) when it is encoded, with a fade in and out and an
optional length cap. That works on every client — including the ones no browser script can reach
— and needs no per-user setting, because there is nothing left to correct.

Whether theme songs play at all stays where it belongs: your own Jellyfin setting under
**Display → Play theme music**.

## Installing

1. Download the latest release zip.
2. Extract it into `<jellyfin data>/plugins/ThemeForge/`.
3. Restart Jellyfin.
4. Open **Dashboard → Plugins → ThemeForge** and press **Run now**, or wait for the nightly task.

### Requirements

- **ffmpeg** — auto-detected, preferring Jellyfin's own bundled build. No setup needed in the
  official Docker images.
- **yt-dlp** — ThemeForge downloads its own copy into its data directory on first use and keeps
  it current with a weekly task. Set an explicit path in the settings if you would rather manage
  it yourself.
- **File Transformation plugin** — *optional*. Installing it adds a "set theme song" button to
  movie and series pages. Without it everything else works exactly the same; only that button is
  missing. ThemeForge never edits Jellyfin's `index.html` on disk, so a server update cannot
  leave it in a broken state.

## How scoring works

Each candidate is judged by ten rules. Each returns a named signal with a reason, and the review
queue shows you the whole breakdown, so a score is always something you can argue with.

| Rule | What it looks at |
|---|---|
| `TitleSimilarity` | Whether the media title genuinely appears in the candidate's title. Vetoes if not. |
| `KeywordAffinity` | Words like *opening*, *main title*, *theme*, *OST*. |
| `NegativeKeywords` | *reaction*, *cover*, *tutorial*, *1 hour*, *loop*, *AMV*, *full episode*… |
| `DurationPlausibility` | Whether it is the right length. Vetoes ten-hour loops and three-second clips. |
| `ChannelReputation` | Trusts YouTube's auto-generated `- Topic` channels and your own allow list. |
| `Popularity` | View count, log-scaled and capped so it can never outvote the title. |
| `Recency` | Penalises uploads from years before the release date. |
| `Availability` | Rejects live, private and blocked videos outright. |
| `Duplicate` | Penalises a video already used as another item's theme. |
| `QuerySpecificity` | Prefers hits from a narrower search. |

Every weight, keyword list and threshold is editable in the settings — no rebuild needed.

Two thresholds decide what happens:

- **at or above the auto-assign threshold** (default 72) → downloaded and assigned;
- **at or above the review threshold** (default 45) → offered in the review queue;
- **below that** → recorded as having no acceptable candidate, and retried later with a backoff.

## Permissions

Every API endpoint requires an administrator. Assigning a theme writes into your media library
and starting a run makes outbound requests from your server, so there is no part of this the
plugin exposes to ordinary users. The plugin stores no per-user settings of its own.

## Where themes are written

Jellyfin looks for `theme.*` in an item's own folder, or any audio inside a `theme-music/`
folder there. ThemeForge writes `theme.mp3` beside the item.

**Films in a shared folder are skipped by default.** In a flat library every film lives in one
directory, so a `theme.mp3` written there would become the theme for all of them. ThemeForge
detects this, refuses, and records the reason. Give each film its own folder, or turn off
*"only write a theme when the item has its own folder"* if that is really what you want.

## Building from source

```bash
dotnet build Jellyfin.Plugin.ThemeForge.sln -c Release
dotnet test  Jellyfin.Plugin.ThemeForge.sln -c Release
```

The loudness tests drive a real ffmpeg. They skip themselves if one is not installed, so install
ffmpeg to run the full suite.

## Layout

```
src/Jellyfin.Plugin.ThemeForge/
  Engines/
    Identity/      turns a library item into a searchable identity
    Query/         builds the ordered search ladder
    Discovery/     runs yt-dlp and parses its output
    Scoring/       ten independent, explainable rules
    Decision/      applies the confidence thresholds
    Acquisition/   downloads, normalises, verifies
    Placement/     writes theme.mp3 and refreshes the item
    Index/         remembers every decision
    Tooling/       provisions yt-dlp, locates ffmpeg
    Orchestration/ drives the pipeline
  Api/             administrator-only HTTP surface
  Configuration/   settings and the dashboard page
  Web/             the optional injected client script
tests/             unit tests plus real-encoder integration tests
```

Each engine sits behind an interface and knows nothing about the ones on either side; the
orchestrator is the only thing that knows the shape of the whole pipeline.
