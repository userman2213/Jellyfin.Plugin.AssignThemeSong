#!/usr/bin/env bash
#
# Cuts a ThemeForge release.
#
# Builds, tests, packages, and adds the new version to the plugin manifest so that Jellyfin
# offers it as an update. With --publish it also pushes the manifest and the package to the
# release channel branch, which is the URL users have added to their server.
#
# The point of this script is that the repository URL a user adds to Jellyfin never changes, and
# nobody edits a checksum by hand. Getting either wrong means the install silently fails
# verification, which is a miserable thing to debug from the Jellyfin end.
#
# Usage:
#   scripts/release.sh 1.2.0.0             # build and update the manifest locally
#   scripts/release.sh 1.2.0.0 --publish   # ...and push it to the release channel
#
set -euo pipefail

readonly REPO_SLUG="userman2213/Jellyfin.Plugin.AssignThemeSong"
readonly CHANNEL_BRANCH="plugin-repo"
readonly TARGET_ABI="10.11.0.0"
readonly PROJECT="src/Jellyfin.Plugin.ThemeForge/Jellyfin.Plugin.ThemeForge.csproj"
readonly SOLUTION="Jellyfin.Plugin.ThemeForge.sln"
readonly META="src/Jellyfin.Plugin.ThemeForge/meta.json"

cd "$(dirname "$0")/.."
ROOT="$(pwd)"

die() { printf 'release: %s\n' "$*" >&2; exit 1; }
step() { printf '\n\033[1m==> %s\033[0m\n' "$*"; }

VERSION="${1:-}"
PUBLISH="${2:-}"

[ -n "$VERSION" ] || die "usage: scripts/release.sh <version> [--publish]"

# Jellyfin's manifest parser expects a four-part version. A three-part one is accepted by the
# JSON but then never compares as newer, so the update silently never appears.
[[ "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$ ]] \
    || die "version must have four parts, for example 1.2.0.0 (got '$VERSION')"

command -v dotnet >/dev/null || die "dotnet is not on PATH"
command -v zip    >/dev/null || die "zip is not installed"

step "Setting version $VERSION"
python3 - "$VERSION" "$META" <<'PY'
import json, re, sys
version, meta_path = sys.argv[1], sys.argv[2]

props = open('Directory.Build.props').read()
props = re.sub(r'<ThemeForgeVersion>[^<]*</ThemeForgeVersion>',
               f'<ThemeForgeVersion>{version}</ThemeForgeVersion>', props)
open('Directory.Build.props', 'w').write(props)

meta = json.load(open(meta_path))
meta['version'] = version
json.dump(meta, open(meta_path, 'w'), indent=4)
open(meta_path, 'a').write('\n')
print(f"  Directory.Build.props and meta.json -> {version}")
PY

step "Building and testing"
dotnet build "$SOLUTION" -c Release --no-incremental
dotnet test "$SOLUTION" -c Release --no-build

step "Packaging"
OUT="src/Jellyfin.Plugin.ThemeForge/bin/Release/net9.0"
ARCHIVE="dist/themeforge_${VERSION}.zip"

[ -f "$OUT/Jellyfin.Plugin.ThemeForge.dll" ] || die "the build produced no plugin assembly"

mkdir -p dist
rm -f "$ARCHIVE"
cp src/Jellyfin.Plugin.ThemeForge/images/icon.png "$OUT/"
( cd "$OUT" && zip -q -r "$ROOT/$ARCHIVE" . )

CHECKSUM="$(md5sum "$ARCHIVE" | cut -d' ' -f1)"
printf '  %s\n  md5 %s\n' "$ARCHIVE" "$CHECKSUM"

step "Updating the manifest"
python3 - "$VERSION" "$CHECKSUM" "$REPO_SLUG" "$CHANNEL_BRANCH" "$TARGET_ABI" <<'PY'
import json, sys, datetime
version, checksum, slug, branch, abi = sys.argv[1:6]

manifest = json.load(open('manifest.json'))
entry = {
    "version": version,
    "targetAbi": abi,
    "framework": "net9.0",
    "sourceUrl": f"https://github.com/{slug}/raw/{branch}/dist/themeforge_{version}.zip",
    "checksum": checksum,
    "timestamp": datetime.datetime.now(datetime.timezone.utc).strftime('%Y-%m-%dT%H:%M:%SZ'),
    "changelog": open('CHANGELOG_NEXT.md').read().strip() if __import__('os').path.exists('CHANGELOG_NEXT.md') else f"Release {version}.",
}

versions = [v for v in manifest[0]['versions'] if v['version'] != version]
versions.insert(0, entry)

# Re-point every entry at the current channel. Older entries can carry a URL from before the
# channel existed, or from a branch that has since been deleted, and a manifest that offers a
# version Jellyfin cannot download is worse than one that never offered it.
for v in versions:
    v['sourceUrl'] = f"https://github.com/{slug}/raw/{branch}/dist/themeforge_{v['version']}.zip"

# Newest first: Jellyfin takes the highest compatible version, but a human reading the file
# should see the current release at the top.
versions.sort(key=lambda v: [int(p) for p in v['version'].split('.')], reverse=True)
manifest[0]['versions'] = versions

json.dump(manifest, open('manifest.json', 'w'), indent=4)
open('manifest.json', 'a').write('\n')
print(f"  manifest now offers: {', '.join(v['version'] for v in versions)}")
PY

if [ "$PUBLISH" != "--publish" ]; then
    step "Done (local only)"
    cat <<EOF
  Nothing has been pushed. Review the changes, then either:
    scripts/release.sh $VERSION --publish
  or commit and run the publish step yourself.
EOF
    exit 0
fi

step "Publishing to the '$CHANNEL_BRANCH' channel"

# The channel branch carries only what a Jellyfin server needs to fetch: the manifest and the
# packages. Keeping it separate from main means the install URL is unaffected by branching,
# merging, or renaming anything in the source tree.
WORKTREE="$(mktemp -d)"
trap 'git worktree remove --force "$WORKTREE" >/dev/null 2>&1 || true; rm -rf "$WORKTREE"' EXIT

if git ls-remote --exit-code --heads origin "$CHANNEL_BRANCH" >/dev/null 2>&1; then
    git fetch --quiet origin "$CHANNEL_BRANCH"
    git worktree add --quiet "$WORKTREE" "origin/$CHANNEL_BRANCH" --detach
    git -C "$WORKTREE" switch --quiet -c "$CHANNEL_BRANCH" 2>/dev/null \
        || git -C "$WORKTREE" switch --quiet "$CHANNEL_BRANCH"
else
    echo "  '$CHANNEL_BRANCH' does not exist yet; creating it"
    git worktree add --quiet --detach "$WORKTREE"
    git -C "$WORKTREE" switch --quiet --orphan "$CHANNEL_BRANCH"
    find "$WORKTREE" -mindepth 1 -maxdepth 1 ! -name .git -exec rm -rf {} +
fi

mkdir -p "$WORKTREE/dist"
cp manifest.json "$WORKTREE/manifest.json"
cp dist/*.zip "$WORKTREE/dist/"

cat > "$WORKTREE/README.md" <<EOF
# ThemeForge plugin repository

This branch is the install channel for [ThemeForge](https://github.com/$REPO_SLUG). It holds only
\`manifest.json\` and the built packages, so the URL below keeps working no matter what happens
to branches in the source tree.

Add this to Jellyfin under **Dashboard → Plugins → Repositories**:

\`\`\`
https://raw.githubusercontent.com/$REPO_SLUG/$CHANNEL_BRANCH/manifest.json
\`\`\`

New releases appear as updates in **Dashboard → Plugins → Catalog** automatically. Do not edit
this branch by hand; it is written by \`scripts/release.sh\`.
EOF

git -C "$WORKTREE" add -A
if git -C "$WORKTREE" diff --cached --quiet; then
    echo "  nothing changed"
else
    git -C "$WORKTREE" commit --quiet -m "Publish ThemeForge $VERSION"
    git -C "$WORKTREE" push --quiet origin "$CHANNEL_BRANCH"
    echo "  pushed $VERSION to $CHANNEL_BRANCH"
fi

step "Verifying what the server will actually fetch"
MANIFEST_URL="https://raw.githubusercontent.com/$REPO_SLUG/$CHANNEL_BRANCH/manifest.json"
sleep 3
SERVED="$(curl -fsSL "$MANIFEST_URL" | python3 -c 'import sys,json; v=json.load(sys.stdin)[0]["versions"][0]; print(v["version"], v["checksum"])')"
echo "  manifest offers: $SERVED"
echo "  expected       : $VERSION $CHECKSUM"
[ "$SERVED" = "$VERSION $CHECKSUM" ] || die "the published manifest does not match the package that was built"
echo "  match"
