#!/usr/bin/env bash
# Skyline Together build script (docs/dev/building.md).
#
#   tools/build.sh [--deploy] [--package] [--game-dir <path>] [--deploy-dir <path>] [--skip-ui-checks] [--skip-api-check]
#
# Order: Release build -> type list (src/SkylineTogether/Data/mod-types.generated.txt) -> API check (tools/ApiCheck:
# every referenced game type and member exists) -> UI checks (when node is installed) -> Core tests -> deploy
# (--deploy) -> package (--package). Any failure stops the script before anything is deployed or packaged.
#
#   --deploy        copy the mod into the local Mods folder (default:
#                   %USERPROFILE%\AppData\LocalLow\Colossal Order\Cities Skylines II\Mods\SkylineTogether)
#   --package       write dist/SkylineTogether-<version>.zip (a SkylineTogether folder with the DLLs, PDBs,
#                   the UI module, LICENSE, THIRD-PARTY-NOTICES.txt and README.txt; extract it into the Mods folder);
#                   with Python the zip is reproducible (tools/release_common.py)
#   --game-dir      the game's install folder (default: the Steam path of Directory.Build.props; env GameDir)
#   --deploy-dir    deploy target (env ModDeployDir)
#   --skip-ui-checks / --skip-api-check   leave out the UI checks / the API check
#
# Works in Git Bash on Windows (and with any shell that has the .NET SDK on the PATH). No NuGet package is used.
set -euo pipefail

SCRIPT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/$(basename "${BASH_SOURCE[0]}")"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

DEPLOY=0
PACKAGE=0
UI_CHECKS=1
API_CHECK=1
GAME_DIR="${GameDir:-}"
DEPLOY_DIR="${ModDeployDir:-}"

usage() { sed -n '2,19p' "$SCRIPT" | sed 's/^# \{0,1\}//'; }

while [ $# -gt 0 ]; do
  case "$1" in
    --deploy) DEPLOY=1 ;;
    --package) PACKAGE=1 ;;
    --skip-ui-checks) UI_CHECKS=0 ;;
    --skip-api-check) API_CHECK=0 ;;
    --game-dir) shift; GAME_DIR="${1:?--game-dir needs a path}" ;;
    --deploy-dir) shift; DEPLOY_DIR="${1:?--deploy-dir needs a path}" ;;
    -h|--help) usage; exit 0 ;;
    *) echo "unknown option: $1" >&2; usage >&2; exit 2 ;;
  esac
  shift
done

step() { printf '\n== %s\n' "$*"; }
fail() { printf '\nBUILD FAILED: %s\n' "$*" >&2; exit 1; }
to_unix() { if command -v cygpath >/dev/null 2>&1; then cygpath -u "$1"; else printf '%s' "$1"; fi; }
to_native() { if command -v cygpath >/dev/null 2>&1; then cygpath -w "$1"; else printf '%s' "$1"; fi; }

# copies the shipped files (both DLLs, their PDBs, the UI module) into $1
copy_mod_files() {
  local target="$1" f
  for f in SkylineTogether.dll SkylineTogether.pdb SkylineTogether.Core.dll SkylineTogether.Core.pdb; do
    if [ -f "$OUT/$f" ]; then cp -f "$OUT/$f" "$target/"; fi
  done
  if [ -f ui/SkylineTogether.mjs ]; then cp -f ui/SkylineTogether.mjs "$target/"; fi
}

# ---------- tools ----------
# the first dotnet host that has an SDK global.json accepts (8.0.x; a dotnet on the PATH may have runtimes only, or
# only a newer SDK): `dotnet --version` run in the repository resolves global.json and fails without a matching SDK
has_matching_sdk() {
  (cd "$ROOT" && "$1" --version >/dev/null 2>&1)
}
DOTNET_CANDIDATES=()
if [ -n "${DOTNET:-}" ]; then DOTNET_CANDIDATES+=("$DOTNET"); fi
if command -v dotnet >/dev/null 2>&1; then DOTNET_CANDIDATES+=("$(command -v dotnet)"); fi
if [ -n "${DOTNET_ROOT:-}" ]; then DOTNET_CANDIDATES+=("$(to_unix "$DOTNET_ROOT")/dotnet.exe" "$(to_unix "$DOTNET_ROOT")/dotnet"); fi
if [ -n "${LOCALAPPDATA:-}" ]; then DOTNET_CANDIDATES+=("$(to_unix "$LOCALAPPDATA")/Microsoft/dotnet/dotnet.exe"); fi
DOTNET_CANDIDATES+=("$HOME/.dotnet/dotnet" "/c/Program Files/dotnet/dotnet.exe")
DOTNET=""
for candidate in "${DOTNET_CANDIDATES[@]}"; do
  if [ -x "$candidate" ] && has_matching_sdk "$candidate"; then DOTNET="$candidate"; break; fi
done
[ -n "$DOTNET" ] || fail "no .NET SDK matching global.json (8.0.x) found; install it, or set DOTNET to a dotnet executable that has one"
# a private (per-user) install needs DOTNET_ROOT so built apphosts (tools run with dotnet run) find their runtime
export DOTNET_ROOT="$(to_native "$(dirname "$DOTNET")")"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1

VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' Directory.Build.props | head -n 1)"
[ -n "$VERSION" ] || VERSION="0.0.0"

PROPS=(-c Release -p:DeployMod=false -nologo)  # never deploy before the tests (even with DeployMod=true in the environment)
if [ -n "$GAME_DIR" ]; then PROPS+=("-p:GameDir=$(to_native "$GAME_DIR")"); fi

OUT="src/SkylineTogether/bin/Release/net472"

# ---------- build ----------
step "Release build (SkylineTogether $VERSION)"
# the SDK (and so the compiler) is part of what determines the build id (docs/dev/building.md#reproducible-builds)
echo ".NET SDK $(cd "$ROOT" && "$DOTNET" --version)"
"$DOTNET" build SkylineTogether.sln "${PROPS[@]}" || fail "dotnet build"
for f in SkylineTogether.dll SkylineTogether.Core.dll; do
  [ -f "$OUT/$f" ] || fail "missing build output $OUT/$f"
done

# ---------- type list (docs/dev/building.md#type-list) ----------
# the types of the game layer, for the Core test that checks each maps to a feature (P-REFS attribution); CI has no
# game and so no game layer, and tests the committed list
TYPE_LIST="src/SkylineTogether/Data/mod-types.generated.txt"
step "Type list of SkylineTogether.dll"
"$DOTNET" build tools/ApiCheck/ApiCheck.csproj -c Release -nologo -v q || fail "dotnet build tools/ApiCheck"
"$DOTNET" tools/ApiCheck/bin/Release/net8.0/ApiCheck.dll --list-types "$OUT/SkylineTogether.dll" --out "$TYPE_LIST" || fail "ApiCheck --list-types"
if ! git -C "$ROOT" diff --quiet -- "$TYPE_LIST" 2>/dev/null; then
  echo "NOTE: $TYPE_LIST changed (types were added, renamed or removed): commit it with the code"
fi

# ---------- API check (docs/dev/building.md#api-check) ----------
# every type and member the DLLs reference, and every "Game.*" type name the game layer looks up, must exist in the
# game's assemblies; a game update that breaks one is found here instead of mid-session
if [ "$API_CHECK" = 1 ]; then
  MANAGED=""
  if [ -n "${GameManagedPath:-}" ]; then MANAGED="$(to_unix "$GameManagedPath")"
  elif [ -n "$GAME_DIR" ]; then MANAGED="$(to_unix "$GAME_DIR")/Cities2_Data/Managed"
  else MANAGED="$(to_unix "$(sed -n 's:.*<GameDir[^>]*>\(.*\)</GameDir>.*:\1:p' Directory.Build.props | head -n 1)")/Cities2_Data/Managed"
  fi
  if [ -f "$MANAGED/Game.dll" ]; then
    step "API check against the game's assemblies"
    "$DOTNET" tools/ApiCheck/bin/Release/net8.0/ApiCheck.dll --managed "$(to_native "$MANAGED")"       --dll "$OUT/SkylineTogether.dll" --dll "$OUT/SkylineTogether.Core.dll" || fail "API check (see MISSING above)"
  else
    step "API check skipped: no Game.dll in $MANAGED"
  fi
fi

# ---------- UI checks (optional Node) ----------
if [ "$UI_CHECKS" = 1 ] && [ -f ui/SkylineTogether.mjs ]; then
  if command -v node >/dev/null 2>&1; then
    step "UI module checks"
    node --check ui/SkylineTogether.mjs || fail "node --check ui/SkylineTogether.mjs"
    if [ -f tools/ui_smoke.mjs ]; then
      node tools/ui_smoke.mjs || fail "UI smoke check"
    elif [ -f tools/UiSmoke/smoke.mjs ]; then
      node tools/UiSmoke/smoke.mjs || fail "UI smoke check"
    fi
  else
    step "UI checks skipped: node is not on the PATH (warning)"
  fi
fi

# ---------- tests ----------
# run the test assembly through the dotnet host (not the apphost): works with per-user SDK installs and long paths
step "Core tests"
TEST_DLL="tests/SkylineTogether.Core.Tests/bin/Release/net8.0/SkylineTogether.Core.Tests.dll"
[ -f "$TEST_DLL" ] || fail "missing test build output $TEST_DLL"
"$DOTNET" "$TEST_DLL" || fail "Core tests (exit code = number of failed tests)"

# ---------- deploy ----------
if [ "$DEPLOY" = 1 ]; then
  if [ -z "$DEPLOY_DIR" ]; then
    [ -n "${USERPROFILE:-}" ] || fail "USERPROFILE is not set; pass --deploy-dir"
    DEPLOY_DIR="$(to_unix "$USERPROFILE")/AppData/LocalLow/Colossal Order/Cities Skylines II/Mods/SkylineTogether"
  fi
  DEPLOY_DIR="$(to_unix "$DEPLOY_DIR")"
  step "Deploy to $DEPLOY_DIR"
  mkdir -p "$DEPLOY_DIR"
  copy_mod_files "$DEPLOY_DIR"
  echo "deployed: $(ls "$DEPLOY_DIR" | tr '\n' ' ')"
fi

# ---------- package ----------
if [ "$PACKAGE" = 1 ]; then
  NAME="SkylineTogether-$VERSION"
  STAGE_ROOT="dist/stage"
  STAGE="$STAGE_ROOT/SkylineTogether"
  ZIP="dist/$NAME.zip"
  step "Package $ZIP"
  rm -rf "$STAGE_ROOT" "$ZIP"
  mkdir -p "$STAGE"
  copy_mod_files "$STAGE"
  for f in LICENSE THIRD-PARTY-NOTICES.txt; do
    if [ -f "$f" ]; then cp -f "$f" "$STAGE/"; fi
  done
  cat > "$STAGE/README.txt" <<EOF
Skyline Together $VERSION - co-op multiplayer for Cities: Skylines II
https://github.com/ljkx/SkylineTogether

Recommended: subscribe on Paradox Mods instead (https://mods.paradoxplaza.com/mods/163578/Windows).
Use one of the two, not both.

Install: extract this archive into
  %USERPROFILE%\\AppData\\LocalLow\\Colossal Order\\Cities Skylines II\\Mods
so that the files end up in Mods\\SkylineTogether, then start the game.

Every player of a session needs the same build of the mod (the build ID at the bottom of the Skyline Together
panel), the same game version and, by default, the same code mods. If the mods differ, the joining player is told
exactly what to enable, disable or update.

Play:
  Host:  load your city, click the Skyline Together button (top left) and press "Host this city".
         Send the other players your Steam ID (copy icon at the top of the panel), or use "Invite friends".
  Join:  on the title screen click "Multiplayer", then "Join a friend". Paste the host's Steam ID (or IP:port)
         and the password if there is one, and press "Join".
         Your game downloads and loads the host's city; the host's game pauses until you have joined.

Back up your saves before you host or join.

To report a bug, click "Write diagnostics" in the panel and attach these files of every player to a GitHub issue
(https://github.com/ljkx/SkylineTogether/issues):
  %USERPROFILE%\\AppData\\LocalLow\\Colossal Order\\Cities Skylines II\\Logs\\SkylineTogether.log
  %USERPROFILE%\\AppData\\LocalLow\\Colossal Order\\Cities Skylines II\\Logs\\SkylineTogether-diagnostics-*.txt
They contain player names, Steam IDs, the session name and IP addresses, but only in shortened form; never passwords
or chat. Your user folder is written as %USERPROFILE%. Never attach crash dumps (.dmp): they can contain passwords.
Details:
https://github.com/ljkx/SkylineTogether/blob/main/docs/guide/troubleshooting.md#reporting-a-bug
EOF
  # every file a player needs, or no package (copy_mod_files skips missing files silently)
  for f in SkylineTogether.dll SkylineTogether.pdb SkylineTogether.Core.dll SkylineTogether.Core.pdb SkylineTogether.mjs            LICENSE THIRD-PARTY-NOTICES.txt README.txt; do
    [ -f "$STAGE/$f" ] || fail "the package would lack $f"
  done
  PY=""
  for candidate in python3 python py; do
    if command -v "$candidate" >/dev/null 2>&1 && "$candidate" -c "import zipfile" >/dev/null 2>&1; then PY="$candidate"; break; fi
  done
  # what make_installer.py and publish_paradox.py refuse: modified tracked files, and untracked files among the build
  # inputs (the projects compile every .cs below their folder, so an untracked file is in these DLLs); the list of
  # inputs is BUILD_INPUTS of tools/release_common.py
  if [ -n "$PY" ]; then
    DIRTY="$("$PY" tools/release_common.py dirty 2>/dev/null || true)"
  else
    DIRTY="$(git -C "$ROOT" status --porcelain --untracked-files=no 2>/dev/null; git -C "$ROOT" status --porcelain --untracked-files=all -- src ui 2>/dev/null | grep '^??' || true)"
  fi
  if [ -n "$DIRTY" ]; then
    echo "WARNING: uncommitted changes or untracked build inputs; make_installer.py and publish_paradox.py refuse this package"
    echo "         (a release is built from a clean commit):"
    printf '%s\n' "$DIRTY" | sed -n '1,10s/^/           /p'
  fi
  # Python's zipfile through tools/release_common.py (reproducible: sorted entries, the commit time as every timestamp),
  # else zip, else the bsdtar that ships with Windows 10+, else PowerShell (which writes backslashes into entry names);
  # every fallback warns, since its zip is not reproducible
  if [ -n "$PY" ]; then
    "$PY" tools/release_common.py zip "$STAGE" "$ZIP" || fail "zip (tools/release_common.py)"
    "$PY" tools/release_common.py check-zip "$ZIP" || fail "the package is incomplete"
  elif command -v zip >/dev/null 2>&1; then
    echo "WARNING: no Python; the zip is not reproducible (it records the current time)"
    (cd "$STAGE_ROOT" && zip -qr "../$NAME.zip" SkylineTogether) || fail "zip"
  elif [ -x /c/Windows/System32/tar.exe ]; then
    echo "WARNING: no Python and no zip; Windows tar writes the zip, which is not reproducible (it records the current time)"
    (cd "$STAGE_ROOT" && /c/Windows/System32/tar.exe -a -c -f "../$NAME.zip" SkylineTogether) || fail "tar -a"
  elif command -v powershell.exe >/dev/null 2>&1 || command -v pwsh >/dev/null 2>&1; then
    echo "WARNING: no Python, zip or Windows tar; PowerShell writes the zip, which is not reproducible (it records the current"
    echo "         time) and has backslashes in its entry names"
    PS="$(command -v powershell.exe || command -v pwsh)"
    "$PS" -NoProfile -NonInteractive -Command \
      "Compress-Archive -Path '$(to_native "$ROOT/$STAGE")' -DestinationPath '$(to_native "$ROOT/$ZIP")' -Force" \
      || fail "Compress-Archive"
  else
    fail "no zip tool found (Python, zip, Windows tar or PowerShell)"
  fi
  [ -f "$ZIP" ] || fail "the archive was not created"
  rm -rf "$STAGE_ROOT"
  echo "packaged: $ZIP"
fi

step "Done"
