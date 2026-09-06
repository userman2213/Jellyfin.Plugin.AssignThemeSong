# ThemeForge plugin repository

This branch is the install channel for [ThemeForge](https://github.com/userman2213/Jellyfin.Plugin.AssignThemeSong). It holds only
`manifest.json` and the built packages, so the URL below keeps working no matter what happens
to branches in the source tree.

Add this to Jellyfin under **Dashboard → Plugins → Repositories**:

```
https://raw.githubusercontent.com/userman2213/Jellyfin.Plugin.AssignThemeSong/plugin-repo/manifest.json
```

New releases appear as updates in **Dashboard → Plugins → Catalog** automatically. Do not edit
this branch by hand; it is written by `scripts/release.sh`.
