#!/usr/bin/env bash
#
# Cuts a ThemeForge release.
#
# Builds, tests, packages, and adds the new version to the plugin manifest so that Jellyfin
# offers it as an update. With --publish it also pushes the manifest and the packages to the
# release channel branch, which is the URL users have added to their server.
#
# One release is two packages: a net9.0 build for Jellyfin 10.11 and a net10.0 build for
# Jellyfin 12, from the same source. The version given here is the 10.11 package's; the 12
# package is numbered one higher in the last position (2.3.0.0 and 2.3.0.1), because Jellyfin
# offers the highest version whose targetAbi the server satisfies and on a 12 server both
# qualify. A 10.11 server never sees the 12 entry, since its targetAbi is too new for it.
#
# The point of this script is that the repository URL a user adds to Jellyfin never changes, and
# nobody edits a checksum by hand. Getting either wrong means the install silently fails
# verification, which is a miserable thing to debug from the Jellyfin end.
#
# Usage:
#   scripts/release.sh 2.3.0.0             # build and update the manifest locally
#   scripts/release.sh 2.3.0.0 --publish   # ...and push it to the release channel
#
set -euo pipefail

readonly REPO_SLUG="userman2213/Jellyfin.Plugin.AssignThemeSong"
readonly CHANNEL_BRANCH="plugin-repo"
readonly SOLUTION="Jellyfin.Plugin.ThemeForge.sln"
readonly META="src/Jellyfin.Plugin.ThemeForge/meta.json"
readonly OUTPUT_ROOT="src/Jellyfin.Plugin.ThemeForge/bin/Release"

# One line per package: target framework, targetAbi, and which Jellyfin line it is for. The
# build decides each package's version and writes its meta.json from these same values (see
# Directory.Build.props), and the packaging step below checks that the two agree.
readonly -a BUILDS=(
    "net9.0 10.11.0.0 10.11"
    "net10.0 12.0.0.0 12"
)

cd "$(dirname "$0")/.."
ROOT="$(pwd)"

die() { printf 'release: %s\n' "$*" >&2; exit 1; }
step() { printf '\n\033[1m==> %s\033[0m\n' "$*"; }

VERSION="${1:-}"
PUBLISH="${2:-}"

[ -n "$VERSION" ] || die "usage: scripts/release.sh <version> [--publish]"

# Jellyfin's manifest parser expects a four-part version. A three-part one is accepted by the
# JSON but then never compares as newer, so the update silently never appears. The last part
# must be 0: it is the slot that tells the two builds of one release apart.
[[ "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+\.0$ ]] \
    || die "version must have four parts and end in .0, for example 2.3.0.0 (got '$VERSION')"

# The Jellyfin 12 package's version: the same release, one higher in the last position.
VERSION12="${VERSION%.0}.1"

package_version() {
    case "$1" in
        net9.0)  printf '%s' "$VERSION" ;;
        net10.0) printf '%s' "$VERSION12" ;;
        *) die "no version rule for target framework '$1'" ;;
    esac
}

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
# Both target frameworks come out of one build; the tests run once per framework, one after the
# other so the two runs do not fight over ffmpeg and CPU.
dotnet build "$SOLUTION" -c Release --no-incremental
dotnet test "$SOLUTION" -c Release --no-build -p:TestTfmsInParallel=false

step "Packaging"
mkdir -p dist
declare -A CHECKSUMS=()

for build in "${BUILDS[@]}"; do
    read -r tfm abi line <<<"$build"
    pkg_version="$(package_version "$tfm")"
    out="$OUTPUT_ROOT/$tfm"
    archive="dist/themeforge_${pkg_version}.zip"

    [ -f "$out/Jellyfin.Plugin.ThemeForge.dll" ] || die "the $tfm build produced no plugin assembly"
    [ -f "$out/meta.json" ] || die "the $tfm build produced no meta.json"

    # The build wrote meta.json from the same values this script uses; a disagreement means one
    # of the two was edited without the other, and the package would lie to Jellyfin.
    python3 - "$out/meta.json" "$pkg_version" "$abi" "$tfm" <<'PY'
import json, sys
meta = json.load(open(sys.argv[1]))
expected = dict(zip(("version", "targetAbi", "framework"), sys.argv[2:5]))
actual = {key: meta.get(key) for key in expected}
if actual != expected:
    sys.exit(f"meta.json in the build output says {actual}, but this release expects {expected}")
PY

    rm -f "$archive"
    cp src/Jellyfin.Plugin.ThemeForge/images/icon.png "$out/"
    ( cd "$out" && zip -q -r "$ROOT/$archive" . )

    CHECKSUMS["$tfm"]="$(md5sum "$archive" | cut -d' ' -f1)"
    printf '  %s (Jellyfin %s)\n  md5 %s\n' "$archive" "$line" "${CHECKSUMS[$tfm]}"
done

step "Updating the manifest"
python3 - "$VERSION" "$VERSION12" "$REPO_SLUG" "$CHANNEL_BRANCH" \
    "net9.0 10.11.0.0 10.11 ${CHECKSUMS[net9.0]}" \
    "net10.0 12.0.0.0 12 ${CHECKSUMS[net10.0]}" <<'PY'
import json, sys, datetime, os
version, version12, slug, branch = sys.argv[1:5]
builds = [line.split() for line in sys.argv[5:]]

timestamp = datetime.datetime.now(datetime.timezone.utc).strftime('%Y-%m-%dT%H:%M:%SZ')
changelog = open('CHANGELOG_NEXT.md').read().strip() if os.path.exists('CHANGELOG_NEXT.md') else f"Release {version}."

manifest = json.load(open('manifest.json'))

# Both packages of a re-run release are replaced, never duplicated.
versions = [v for v in manifest[0]['versions'] if v['version'] not in (version, version12)]

for tfm, abi, line, checksum in builds:
    pkg_version = version if tfm == "net9.0" else version12
    versions.insert(0, {
        "version": pkg_version,
        "targetAbi": abi,
        "framework": tfm,
        "sourceUrl": f"https://github.com/{slug}/raw/{branch}/dist/themeforge_{pkg_version}.zip",
        "checksum": checksum,
        "timestamp": timestamp,
        "changelog": f"Build for Jellyfin {line}.\n\n{changelog}",
    })

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

# The manifest must never offer a package the channel does not hold.
python3 - "$WORKTREE" <<'PY'
import json, os, sys
worktree = sys.argv[1]
manifest = json.load(open(os.path.join(worktree, 'manifest.json')))
missing = [v['version'] for v in manifest[0]['versions']
           if not os.path.exists(os.path.join(worktree, 'dist', os.path.basename(v['sourceUrl'])))]
if missing:
    sys.exit(f"the manifest offers versions whose package is not in the channel: {missing}")
PY

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
SERVED="$(curl -fsSL "$MANIFEST_URL" | python3 -c '
import sys, json
by_version = {v["version"]: v["checksum"] for v in json.load(sys.stdin)[0]["versions"]}
for version in sys.argv[1:]:
    print(version, by_version.get(version, "missing"))
' "$VERSION12" "$VERSION")"
EXPECTED="$(printf '%s %s\n%s %s' "$VERSION12" "${CHECKSUMS[net10.0]}" "$VERSION" "${CHECKSUMS[net9.0]}")"
echo "  manifest offers:"; echo "$SERVED" | sed 's/^/    /'
echo "  expected       :"; echo "$EXPECTED" | sed 's/^/    /'
[ "$SERVED" = "$EXPECTED" ] || die "the published manifest does not match the packages that were built"
echo "  match"
