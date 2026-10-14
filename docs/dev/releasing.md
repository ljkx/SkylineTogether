# Releasing

There is no automated release pipeline yet; releases are built on a PC with the game installed.

## Versions and identities

| What | Where | Notes |
|---|---|---|
| Release version | `<Version>` in `Directory.Build.props` (currently 0.1.0) | Used for the zip names and the file version of both DLLs. |
| Assembly version | `<AssemblyVersion>1.0.0.0</AssemblyVersion>` in both projects | **Never change it.** The network id system is saved in cities by assembly-qualified name. |
| Protocol version | `ProtocolVersion.Current` in `src/SkylineTogether.Core/Protocol/ProtocolVersion.cs` | Increment it for every change of an envelope, epoch, record or message layout; update the golden byte vectors of the tests (`Protocol/*GoldenTests.cs`). Peers with different versions refuse each other, and solo test recordings cannot be replayed by another protocol version. |
| Build id | First 12 hex digits of the SHA-256 of `SkylineTogether.dll` | Printed by the installer; players compare it. |
| Mod build | The game layer assembly's MVID (`Session/GameInfoProvider.cs`) | Compared by the host on join; shown as "mod build" in diagnostics. |

Every compile produces a different build that refuses to play with any other build, even at the same version number.
Players must therefore all install the same published setup zip.

## Steps

1. Make sure `main` is green on CI and `tools/build.sh` passes locally with the game.
2. Update [CHANGELOG.md](../../CHANGELOG.md): move the entries under *Unreleased* to a new version section with the
   date.
3. Bump `<Version>` in `Directory.Build.props`. If the wire format changed since the last release, check that
   `ProtocolVersion.Current` was incremented.
4. Build the packages from a clean tree:

   ```sh
   git status                              # nothing uncommitted
   rm -rf dist
   tools/build.sh --package                # dist/SkylineTogether-<version>.zip
   python tools/installer/make_installer.py # dist/SkylineTogether-Setup.zip, prints the build id
   ```

5. Test the setup zip itself (not a `--deploy` build): install it with `Install.cmd` on two PCs, check that both show
   the build id printed in step 4, and run the [two-player checklist](../guide/testing.md#two-player-test) and a solo
   test.
6. Tag the commit (`git tag v<version>`) and push the tag.
7. Create a GitHub release for the tag. Attach `SkylineTogether-Setup.zip` (for players) and
   `SkylineTogether-<version>.zip` (manual install). In the notes, give the build id, the game version it was tested
   with and the changelog section.

Do not rebuild after testing: a rebuild is a different build. Upload exactly the files you tested.
