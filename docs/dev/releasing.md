# Releasing

There is no automated release pipeline yet; releases are built on a PC with the game installed.

## Versions and identities

| What | Where | Notes |
|---|---|---|
| Release version | `<Version>` in `Directory.Build.props` (currently 0.1.0) | Used for the zip names and the file version of both DLLs. |
| Assembly version | `<AssemblyVersion>1.0.0.0</AssemblyVersion>` in both projects | **Never change it.** The network id system is saved in cities by assembly-qualified name. |
| Protocol version | `ProtocolVersion.Current` in `src/SkylineTogether.Core/Protocol/ProtocolVersion.cs` | Increment it for every change of the wire format (see [Protocol version policy](#protocol-version-policy)); update the golden byte vectors of the tests (`Protocol/*GoldenTests.cs`). Peers with different versions refuse each other, and solo test recordings cannot be replayed by another protocol version. |
| Build id | First 12 hex digits of the SHA-256 of `SkylineTogether.dll` | Printed by the installer, shown in the panel footer and compared by the host on join (the game hashes the loaded DLL the same way; only if the file cannot be read does it fall back to the assembly's MVID, shown as "(mvid)"). |

Every compile produces a different build that refuses to play with any other build, even at the same version number.
Players must therefore all install the same published setup zip.

### Protocol version policy

The handshake compares the build id anyway, so two different builds never play together whatever their protocol
versions. The protocol version is still bumped on **every** wire change, because it is the first thing the host
checks: a joiner with another version is refused with "Skyline Together versions differ" before the password
challenge and before any build, game or mod detail is compared or revealed, and solo recordings of another wire
format are refused by version instead of failing half-way through a replay.

- **What counts as a wire change**: a new, removed or reordered field of an envelope, message, control payload, sync
  record, global-channel payload or recording; a new record kind, message kind, control kind or global channel; a
  changed limit that a peer of the old version would refuse (or accept beyond what the new one reads); a different
  meaning of an existing value. Adding a new record kind counts even though older readers would refuse the batch as
  malformed: that refusal is exactly what the version check replaces with a readable message.
- **What does not**: host-side validation, rate limits, budgets and anything that changes only what a peer decides
  to send, as long as every byte keeps its layout and meaning.
- **When**: in the change itself, or at the latest in the commit that merges it to `main` (one increment per merge,
  however many layout changes the branch made). Say "wire change, protocol N" in the commit message.
- **With it**: update `ProtocolVersion.Current`, the pinned value in `Protocol/LimitsAndTextTests.cs`, and the golden
  vectors of the layouts that changed. The `HelloMessage` prefix and the whole `RefuseMessage` never change, so that
  peers of every version can still refuse each other readably.

## Steps

1. Make sure `main` is green on CI and `tools/build.sh` passes locally with the game.
2. Update [CHANGELOG.md](../../CHANGELOG.md): move the entries under *Unreleased* to a new version section with the
   date.
3. Bump `<Version>` in `Directory.Build.props`. If the wire format changed since the last release, check that
   `ProtocolVersion.Current` was incremented (it should have been, with each change; see the policy above).
4. Build the packages from a clean tree:

   ```sh
   git status                              # nothing uncommitted
   rm -rf dist
   tools/build.sh --package                # dist/SkylineTogether-<version>.zip
   python tools/installer/make_installer.py # dist/SkylineTogether-Setup.zip, prints the build id
   ```

   `make_installer.py` refuses to build when any packaged file contains personal data (home-folder paths, e-mail
   addresses, Steam IDs; the patterns of `tools/check_personal_data.py`). `Directory.Build.props` maps the checkout
   path to `/_/` (`DeterministicSourcePaths`), so the PDBs, the DLLs and stack traces in players' logs never contain the
   builder's paths; Source Link points stack traces at the commit on GitHub, so build releases from a pushed commit.

5. Test the setup zip itself (not a `--deploy` build): install it with `Install.cmd` on two PCs, check that both show
   the build id printed in step 4, and run the [two-player checklist](../guide/testing.md#two-player-test) and a solo
   test.
6. Tag the commit (`git tag v<version>`, e.g. `v0.1.0-alpha.1`) and push the tag.
7. Create a GitHub release for the tag (mark versions with a `-alpha`/`-beta` suffix as pre-releases). Attach `SkylineTogether-Setup.zip` (for players) and
   `SkylineTogether-<version>.zip` (manual install). In the notes, give the build id, the game version it was tested
   with and the changelog section.

Do not rebuild after testing: a rebuild is a different build. Upload exactly the files you tested.
