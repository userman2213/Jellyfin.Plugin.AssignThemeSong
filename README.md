# ThemeForge

A Jellyfin plugin that finds theme songs for your movies and TV shows, scores every candidate it
finds, and assigns the confident ones automatically. Anything it is unsure about waits in a
review queue instead of guessing.

Requires **Jellyfin 10.11 or 12**. Every release ships one build for each, and the plugin
catalogue picks the one your server can run. (The optional item-page button needs the File
Transformation plugin's own build for your Jellyfin version.)

## What it does

Point it at your library and it will, for each movie and series:

1. Work out what the item actually is — title, alternate titles, year, TVDB/TMDB/IMDb ids, and
   who wrote its music.
2. Build an ordered list of searches, most specific first.
3. Search YouTube with **yt-dlp** and collect candidates.
4. Score every candidate against eleven independent rules, each of which explains itself.
5. Assign the winner if it is confident, queue it for you if it is not, and record either way.
6. Download the audio, verify it plays, and write it beside your media **exactly as it was
   delivered** (`theme.opus`, `theme.m4a`, `theme.mp3`), unless you have asked for processing.

Everything it decides is recorded, so re-running is cheap and safe: settled items are skipped,
and anything you decided yourself is never touched again.

## What happens to the audio

By default, **nothing**. The best audio stream yt-dlp can get is written beside your media exactly
as it was delivered — the same stream at the same volume, in the container it arrived in
(`theme.opus`, `theme.m4a`, `theme.mp3`). It is not re-encoded, not faded and not made quieter.

Jellyfin has no theme volume control of its own — [jellyfin-web#3086](https://github.com/jellyfin/jellyfin-web/issues/3086)
is still open and Jellyfin 12 did not change that — so the level you hear is the level of the
upload. If that bothers you for some themes, **Settings → Audio processing** offers these, every
one of them off until you turn it on:

| Option | What it does |
|---|---|
| **Raise quiet themes** | Lifts a theme quieter than the floor (`-16 LUFS` by default) up to it, as a plain gain. It never lowers anything. |
| **Normalise every theme to one loudness** | Every theme ends up at the same level, which makes loud ones quieter too. |
| **Cut silence from the start and the end** | Removes dead air around the music. |
| **Fade in / fade out** | A length in seconds; `0` means no fade. |
| **Cut to a maximum length** | `0` keeps the whole theme. |
| **Always convert to MP3** | For a client that cannot play Opus or AAC and cannot let Jellyfin transcode for it. |

A theme is only re-encoded — to MP3, at the configured bitrate — when one of these actually has
to change it. Themes written by versions before 2.3 were normalised and faded; **Settings →
Themes already written → Re-download all themes** fetches each of them again and writes it with
your current settings, without touching a single decision.

Whether theme songs play at all stays where it belongs: your own Jellyfin setting under
**Display → Play theme music**.

## Who wrote the music

The composer's name is the most specific thing a search can ask for. *Firefly Main Title — Greg
Edmonson* cannot be about any other Firefly, and on a distributor's `- Topic` upload — the
best-sourced recordings on YouTube — the composer's name is the artist credit.

Jellyfin records a composer for very few items, and without one three things quietly stop working:
the search ladder drops its composer rung, an upload that names the composer earns no bonus, and a
title that is an ordinary word — *Lost*, *Alien* — has nothing to corroborate it and is held below
the auto-assign score.

So ThemeForge looks it up, **by the title's own database id and never by name**:

| Source | Keyed on | Cost | Licence |
|---|---|---|---|
| [Wikidata](https://www.wikidata.org) | IMDb (P345), TMDB (P4947/P4983) | one query per 50 titles | CC0 |
| [MusicBrainz](https://musicbrainz.org) | the IMDb URL of the film or show | one request per title, 1/second | CC0 |

Wikidata answers about three quarters of a library in a handful of requests. MusicBrainz is asked
only about what is left. Answers are kept for two months and misses for a fortnight, so a whole
library costs a few requests once and nothing thereafter. **Your library's own credits always win**
— the research only fills in what Jellyfin does not have.

Searching either service *by title* is deliberately not done, and this is not caution for its own
sake: MusicBrainz returns a J-pop single for "Alien", and the composer of the 1978 Battlestar
Galactica for the 2004 one. A wrong composer is worse than no composer, because it would be
searched for and believed.

The scheduled task **Look up who wrote the music** runs daily at 02:00 UTC, an hour before the
discovery run. **Diagnostics** shows how many of your titles somebody is known for; a title with no
IMDb or TMDB id cannot be looked up at all, so adding a metadata provider is what helps there.

Lyricists, conductors and arrangers Jellyfin already knows about are scored at half what the
composer is worth, and are never searched for: a conductor records dozens of scores, and a query
built from a lyricist's name returns the songs they wrote for everybody else.

All of this can be switched off, together or in pieces, under **Who wrote the music** in the
settings.

## Installing

### From the plugin repository (recommended)

1. In Jellyfin, go to **Dashboard → Plugins → Repositories** and press **+**.
2. Name it `ThemeForge` and paste this as the URL:

   ```
   https://raw.githubusercontent.com/userman2213/Jellyfin.Plugin.AssignThemeSong/plugin-repo/manifest.json
   ```

3. Save, then go to **Dashboard → Plugins → Catalog**, find **ThemeForge**, and install it.
4. Restart Jellyfin.
5. Open **Dashboard → Plugins → ThemeForge** and press **Run now**, or wait for the nightly task.

**Add that URL once.** New releases show up in the catalogue as updates on their own — there is
never a URL to change. The `plugin-repo` branch is an install channel holding nothing but the
manifest and the packages, so it is unaffected by branching, merging or renaming anything in the
source tree.

### By hand

1. Download the newest `dist/themeforge_*.zip` from the
   [`plugin-repo` branch](https://github.com/userman2213/Jellyfin.Plugin.AssignThemeSong/tree/plugin-repo/dist).
2. Extract it into `<jellyfin data>/plugins/ThemeForge_<version>/`.
3. Restart Jellyfin.

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

## Changing a theme by hand

When the song is wrong, or you simply want a different one, search for it yourself. The control is
in three places and behaves the same in all of them:

- on a movie or series page in Jellyfin, the **music note** button (needs the File Transformation
  plugin);
- on the plugin page under **Library**, the **Change theme** button on any row;
- under **Review queue**, **Search for a different one** on any item waiting for a decision.

It searches as soon as it opens, and **it does what an unattended run does**: it asks the
catalogues first and pins a ThemerrDB entry for this exact title at the top, then runs every
phrasing of the item's search ladder rather than one query. The results of all the phrasings are
pooled before it decides what is worth a closer look, which is something a run does not do — a run
takes the best few of each phrasing separately, so a video that comes sixth on two of them is never
examined. A line above the results says how many phrasings were tried and how many results came
back.

Type something in the box and it searches for exactly that instead. Nothing is pinned above what
you asked for, including the catalogue entry you may be overriding precisely because it is wrong.

Each result gives the title, the channel, the length, a link to watch it, and what ThemeForge makes
of it out of 100 with a one-line reason.

**Results the scoring rules reject are shown too**, greyed out with the reason they were rejected —
those are often exactly the video you are looking for, and your judgement beats the rules'. Press
**Use this** on any of them.

A theme chosen this way is final: it is recorded as your decision, and no later run will replace
it whatever the library's rule says. The recorded score is cleared at the same time, because a
score describes the candidate that earned it and not the one you picked.

If you already have a link, the item-page dialog still takes a pasted URL.

## What happens to themes you already have

By default, **nothing**. On its first pass ThemeForge notices that an item already has a
`theme.*` file or a `theme-music/` folder, marks it `ManualOverride` in its index, and never
looks at it again. Your existing themes are safe out of the box.

If you want that changed, it is set **per library** on the **Libraries** tab, because shows and
films usually want different answers:

| Setting | What it does |
|---|---|
| **Never replace an existing theme** | Default. An item that has a theme is left alone permanently. |
| **Replace themes ThemeForge chose** | Re-runs its own picks — useful after tuning the scoring — while leaving anything you placed by hand untouched. It tells the difference using the content hash recorded when it wrote the file. |
| **Replace any theme, including ones I placed** | Overwrites everything. Use this to hand a whole library over to ThemeForge. |

Each library can also be switched off entirely, so ThemeForge ignores it.

Replaced themes are copied aside first as `theme.<ext>.themeforge-backup-<timestamp>` unless you
turn backups off, and a theme assigned by hand or locked from the Library tab is never touched by
any of these settings.

## Logging

ThemeForge keeps its own log at `<jellyfin-data>/themeforge/logs/themeforge.log`, viewable under
the **Log** tab on the plugin page. It rotates at 5 MB and keeps three old files by default.

Everything in it also goes to the Jellyfin server log, so this hides nothing — it exists because
a run over a large library produces thousands of lines that only make sense together, and picking
them out of everything else the server logs is impractical.

Its level is set independently of Jellyfin's, so you can turn ThemeForge up to **Debug** to see
per-candidate scoring for one run without making the whole server log verbose.

## Uninstalling

Uninstalling from the Jellyfin dashboard removes the plugin **and** everything it stored:
`<jellyfin-data>/themeforge/` — the index, the logs, and the yt-dlp binary it downloaded.

**Your theme files stay.** The theme files in your media folders are your media now, and
uninstalling a plugin should never delete your files as a side effect.

If you *do* want them gone, use **Settings → Removing themes → Remove all ThemeForge themes**
before uninstalling. That deletes only files ThemeForge actually wrote: each one is checked
against the contents recorded when it was written, so anything you have since replaced by hand is
left alone.

### Upgrading from the old xThemeSong plugin

The previous plugin could patch Jellyfin's `index.html` on disk to inject its script. That edit
survives uninstalling it, so you may be left with a dead `<script plugin="xThemeSong" ...>` tag in
`<jellyfin-web>/index.html` that reloads on every page. Remove that line by hand, or reinstall
`jellyfin-web`. ThemeForge never writes to that file — it injects only through the File
Transformation plugin, in memory, per request.

## How scoring works

Each candidate is judged by eleven rules. Each returns a named signal with a reason, and the review
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
| `Composer` | Whether the candidate names the composer, or somebody else credited on the music. A bonus, never a penalty. |

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
folder there. ThemeForge writes `theme.<ext>` beside the item.

**Films in a shared folder are skipped by default.** In a flat library every film lives in one
directory, so a theme written there would become the theme for all of them. ThemeForge
detects this, refuses, and records the reason. Give each film its own folder, or turn off
*"only write a theme when the item has its own folder"* if that is really what you want.

## Releasing

`scripts/release.sh` is the only supported way to cut a release. It sets the version everywhere
it appears, builds, runs the tests, packages, computes the checksum, adds the entry to
`manifest.json`, and pushes the result to the `plugin-repo` channel — then re-fetches the
published manifest and fails if it does not match the package it just built.

```bash
scripts/release.sh 1.2.0.0             # build and update the manifest locally
scripts/release.sh 1.2.0.0 --publish   # ...and publish it to the channel
```

Put the release notes in `CHANGELOG_NEXT.md` first; the script uses that as the changelog for
the entry.

Tagging `v1.2.0.0` runs the same script through GitHub Actions, so a manual release and an
automated one cannot produce differently-built packages under the same version.

Two things this exists to prevent, both of which fail in ways that are miserable to diagnose from
the Jellyfin end: a checksum that does not match its package, which makes the install fail
verification with no useful message; and a three-part version number, which parses fine but never
compares as newer, so the update simply never appears.

## Building from source

```bash
dotnet build Jellyfin.Plugin.ThemeForge.sln -c Release
dotnet test  Jellyfin.Plugin.ThemeForge.sln -c Release
```

The encoder tests drive a real ffmpeg. They skip themselves if one is not installed, so install
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
    Acquisition/   downloads, verifies, writes the theme file (a copy unless asked otherwise)
    Placement/     puts theme.<ext> beside the item and refreshes it
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
