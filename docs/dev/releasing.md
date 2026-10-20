# Releasing

Releases are built by hand on a PC with the game installed (the game layer compiles against the game's own
assemblies, which a CI runner does not have). Each release publishes SHA-256 checksums of its zips so players can check
their download.

## Versions and identities

| What | Where | Notes |
|---|---|---|
| Release version | `<Version>` in `Directory.Build.props` | Used for the zip names and the file version of both DLLs. See [Version numbers](#version-numbers). |
| Assembly version | `<AssemblyVersion>1.0.0.0</AssemblyVersion>` in both projects | **Never change it.** The network id system is saved in cities by assembly-qualified name. |
| Protocol version | `ProtocolVersion.Current` in `src/SkylineTogether.Core/Protocol/ProtocolVersion.cs` | Increment it for every change of the wire format (see [Protocol version policy](#protocol-version-policy)); update the golden byte vectors of the tests (`Protocol/*GoldenTests.cs`) and the pinned corpus of `Protocol/WireCorpusTests.cs`, which fails for any layout change and names the samples that changed. Peers with different versions refuse each other, and solo test recordings cannot be replayed by another protocol version. |
| Build ID | First 12 hex digits of the SHA-256 of `SkylineTogether.dll` | Printed by the installer and by `publish_paradox.py`, shown in the panel footer and compared by the host on join (the game hashes the DLL file its mod manager loaded, in the local Mods folder or a Paradox Mods subscription; only if the file cannot be read does it fall back to the assembly's MVID, shown as "(mvid)"). |
| Paradox Mods id and version | `ModId` and `ModVersion` in `src/SkylineTogether/Properties/PublishConfiguration.xml` | `ModId` is 163578 ([the mod's page](https://mods.paradoxplaza.com/mods/163578/Windows)); `ModVersion` must equal `<Version>`. See [publishing-paradox-mods.md](publishing-paradox-mods.md). |

Any change of the DLL's bytes is a different build that refuses to play with any other build, even at the same version
number. Rebuilding the same commit with the same SDK and game version gives the same bytes
([Reproducible builds](building.md#reproducible-builds)), but a release is still built once and every route ships those
same files.
Players must therefore all install the same release: the same Paradox Mods version or the same setup zip (both carry
the same build ID).

### Version numbers

Versions follow [Semantic Versioning](https://semver.org/):

- **Minor** (`0.2.0`, `0.3.0`): new features, or a new protocol version.
- **Patch** (`0.2.1`): bug fixes only. A patch release may still need a new protocol version when a fix changes the
  wire format; the changelog says so.
- A **pre-release suffix** (`-beta.1`) only for a build that is deliberately published as a preview, on GitHub only
  and marked as a pre-release there. Regular releases have no suffix and are published on GitHub and Paradox Mods.

The releases `0.1.0-alpha.1` to `0.1.0-alpha.4` predate this rule.

### Protocol version policy

The handshake compares the build ID anyway, so two different builds never play together whatever their protocol
versions. The protocol version is still bumped on **every** wire change, because the host checks it right after the
ban list and the join policy: a joiner with another version is refused with "Skyline Together versions differ" before the password
challenge and before any build, game or mod detail is compared or revealed, and solo recordings of another wire
format are refused by version instead of failing half-way through a replay.

- **What counts as a wire change**: a new, removed or reordered field of an envelope, message, control payload, sync
  record, global-channel payload or recording; a new record kind, message kind, control kind or global channel; a
  changed limit that a peer of the old version would refuse (or accept beyond what the new one reads); a different
  meaning of an existing value. Adding a new record kind counts even though older readers would refuse the batch as
  malformed: that refusal is exactly what the version check replaces with a readable message.
- **What does not**: host-side validation, rate limits, budgets and anything that changes only what a peer decides
  to send, as long as every byte keeps its layout and meaning.
- **Versioned payloads**: the tool preview inside a presence message is opaque to the message and starts with its own
  version byte (`ToolPreviewCodec.Version`); a decoder ignores a preview of another version (the player is shown
  without a preview) instead of failing, so neither a peer nor a replayed recording breaks on it. A change confined to
  the preview layout bumps that version, not the protocol version.
- **When**: in the change itself, or at the latest in the commit that merges it to `main` (one increment per merge,
  however many layout changes the branch made). Say "wire change, protocol N" in the commit message.
- **With it**: update `ProtocolVersion.Current`, the pinned value in `Protocol/LimitsAndTextTests.cs`, and the golden
  vectors of the layouts that changed. The `HelloMessage` prefix and the whole `RefuseMessage` never change, so that
  peers of every version can still refuse each other readably.

## Steps

Follow them in this order: every file that ships (the version, the changelog, the Paradox Mods configuration) is
committed before the build, so the tag, the DLL's commit hash and the uploads all name the same commit.

1. Make sure `main` is green on CI and `tools/build.sh` passes locally with the game.
2. Prepare the release commit:
   - [CHANGELOG.md](../../CHANGELOG.md): move the entries under *Unreleased* to a new version section with the date;
   - `<Version>` in `Directory.Build.props`;
   - `ModVersion` (equal to `<Version>`) and `ChangeLog` in `src/SkylineTogether/Properties/PublishConfiguration.xml`;
   - if the wire format changed since the last release, check that `ProtocolVersion.Current` was incremented (it
     should have been, with each change; see the policy above).

   Commit them ("Release <version>"), push, and wait until CI is green for that commit.
3. Build the packages from the clean, pushed `HEAD`:

   ```sh
   git status                              # nothing uncommitted
   git log origin/main..HEAD               # nothing unpushed
   rm -rf dist
   tools/build.sh --package                # dist/SkylineTogether-<version>.zip; prints the .NET SDK version
   python tools/installer/make_installer.py # dist/SkylineTogether-Setup.zip, prints the build ID
   python tools/paradox/publish_paradox.py check   # the rules of Release; prints the same build ID
   ```

   `make_installer.py` and `publish_paradox.py` take the zip of the `<Version>` in `Directory.Build.props` (never an
   older zip left in `dist/`), and refuse it unless its `SkylineTogether.dll` was built from the current `HEAD` (the
   DLL's informational version ends in `+<commit hash>`), nothing tracked is modified, no untracked file lies among the
   inputs of the build (`src`, `ui`, `Directory.Build.*`, `global.json` and the packaging tools; the projects compile
   every `.cs` file below their folder) and, for `make_installer.py` and every upload, `origin/main` contains `HEAD`
   (as last fetched; `publish_paradox.py check` only warns). `tools/build.sh` warns about the same files. `publish_paradox.py` uploads only versions without a pre-release suffix
   (`--allow-prerelease` overrides it), and CI checks that `ModVersion` equals `<Version>` and that the changelog has
   the version's section (`publish_paradox.py check --config-only`). `make_installer.py` also
   refuses to build when any packaged file contains personal data (home-folder paths, e-mail addresses, Steam IDs;
   the patterns of `tools/check_personal_data.py`). `Directory.Build.props` maps the checkout path to `/_/`
   (`DeterministicSourcePaths`), so the PDBs, the DLLs and stack traces in players' logs never contain the builder's
   paths; Source Link points stack traces at the commit on GitHub, which is why the commit must be pushed.

   `publish_paradox.py check` applies the rules of the command the upload will use: `Release` (`NewVersion`, then
   `Update`) while `ModId` is set (`--for <command>` to check another one).

   `make_installer.py` ends with the SHA-256 of both zips; copy them for the release notes, with the SDK version
   `tools/build.sh` printed.

4. Test the setup zip itself (not a `--deploy` build): install it with `Install.cmd` on two PCs, check that both show
   the build ID printed in step 3, and run the [two-player checklist](../guide/testing.md#two-player-checklist) and a
   solo test. For a patch release, the solo test and the checklist steps that cover the fixed areas are enough.
5. Tag the release commit (`git tag v<version>`, e.g. `v0.2.0`; it is `HEAD`, the commit the DLL names) and push
   the tag.
6. Create a GitHub release for the tag, titled "Skyline Together <version>". Mark it as a pre-release only for a
   version with a pre-release suffix, so that regular releases show as "Latest". Attach `SkylineTogether-Setup.zip`
   (for players) and `SkylineTogether-<version>.zip` (manual install). Write the notes from the
   [release notes template](release-notes-template.md): the changelog section, the build ID, the game version it was
   tested with, the install routes, the SHA-256 checksums of both zips and how to report bugs.
7. Paradox Mods: with the game running and logged in to Paradox, run
   `python tools/paradox/publish_paradox.py Release` from the same clean `HEAD`. `Release` runs `NewVersion` (the
   files, `ModVersion` and `ChangeLog`) and then `Update` (page texts, thumbnail and screenshots), so the page matches
   the new version. It uploads the files of the same release zip, so players from Paradox Mods and from GitHub have the
   same build ID. Check the page afterwards. (`Publish` was the very first upload; it writes the new `ModId`, which is
   then committed.) Details: [publishing-paradox-mods.md](publishing-paradox-mods.md).

Do not rebuild after testing: upload exactly the files you tested, to GitHub and to Paradox Mods. (A rebuild of the
same commit with the same SDK and game version gives the same bytes, see
[Reproducible builds](building.md#reproducible-builds), but only the tested files are known to work.)
