# Publishing on Paradox Mods

Cities: Skylines II mods are published on [Paradox Mods](https://mods.paradoxplaza.com/games/cities_skylines_2) with
**ModPublisher**, a command-line tool that ships with the game. The official mod template runs it through Visual
Studio publish profiles. Skyline Together builds without the template's MSBuild targets and without NuGet
([decision 0001](../decisions/0001-build-without-nuget.md)), so `tools/paradox/publish_paradox.py` runs ModPublisher
with the same arguments the template's `Mod.targets` passes. Uploading needs the maintainer's Paradox account. Nothing
in the build or in CI uploads anything.

Skyline Together is published as [mod 163578](https://mods.paradoxplaza.com/mods/163578/Windows); `ModId` in
`PublishConfiguration.xml` holds that id, so every further upload is a `NewVersion` or an `Update`; a release runs
both (`publish_paradox.py Release`).

- [How publishing works](#how-publishing-works)
- [Files](#files)
- [Before every upload](#before-every-upload)
- [First upload (done)](#first-upload-done)
- [A new version](#a-new-version)
- [Changing only the page](#changing-only-the-page)
- [How a Paradox Mods install differs from a local one](#how-a-paradox-mods-install-differs-from-a-local-one)
- [Installed twice](#installed-twice)
- [Without the script](#without-the-script)

## How publishing works

- **ModPublisher** is part of the game install:
  `<game folder>\Cities2_Data\Content\Game\.ModdingToolchain\ModPublisher\ModPublisher.exe`. Older toolchain setups
  keep it under `Cities2_Data\StreamingAssets\~Tooling~\ModPublisher` and point the user environment variable
  `CSII_MODPUBLISHERPATH` at it. You don't need the in-game "Modding toolchain" setup (the `CSII_*` environment
  variables, the Unity project, the post-processor) for publishing: the script only needs `ModPublisher.exe`.
- It has three commands. They match the template's three publish profiles:

  | Command | Template profile | What it does |
  |---|---|---|
  | `Publish` | `PublishNewMod` | Uploads a new mod. `ModId` must be empty; the output says `Mod published with Id=<number>`. |
  | `NewVersion` | `PublishNewVersion` | Uploads a new version of the mod `ModId`. Needs a new `ModVersion` and a `ChangeLog`. |
  | `Update` | `UpdatePublishedConfiguration` | Updates only the page (texts, images, tags, links, game version, access level); no new files. |

  `NewVersion` uploads the files with their `ModVersion` and `ChangeLog`; the page texts and images are written by
  `Update`. The script's `Release` command therefore runs `NewVersion` and then `Update`, so the page always describes
  the version that was just uploaded.

- Usage: `ModPublisher <command> <PublishConfiguration.xml> -c <content folder> -v`. The content folder is uploaded as
  it is and lands in the players'
  `%USERPROFILE%\AppData\LocalLow\Colossal Order\Cities Skylines II\.cache\Mods\pdx_mods\<id>_<version>` folder.
- **Login**: ModPublisher logs in with the Paradox account of the **running game** ("Trying to auto log in using
  in-game login"). Start Cities: Skylines II and log in to Paradox (main menu, Paradox account) before you run it.
  It can also take `-e`/`-p` or `-l <file>` with `--noAutoLogin`. Never put those credentials in the repository or in
  a script.
- **.NET**: ModPublisher is built for .NET 6. If only a newer .NET runtime is installed, the script sets
  `DOTNET_ROLL_FORWARD=Major` so ModPublisher runs on it. Installing the .NET 6 runtime works too.
- Paradox Mods treats `ModVersion` as a free text (other mods use versions like `1.5.2+build.2`), so
  `0.2.0` or `0.2.0-beta.1` is fine. The platform also counts uploads (`1`, `2`, ...). That count is the `<version>` in the
  players' `pdx_mods\<id>_<version>` folder name.

## Files

| File | Purpose |
|---|---|
| `src/SkylineTogether/Properties/PublishConfiguration.xml` | The page: `ModId`, `DisplayName`, `ShortDescription`, `LongDescription` (Paradox Mods markdown: headings, bullet lists, bold), `Thumbnail`, `Screenshot` (repeatable), `Tag` (`Code Mod`), `ModVersion`, `GameVersion`, `ChangeLog`, `ExternalLink` (GitHub), `AccessLevel` (Public, Unlisted or Private). Image paths are relative to `src/SkylineTogether`. Multi-line texts must not be indented. |
| `src/SkylineTogether/Properties/Thumbnail.png` | 1024 x 1024. Paradox Mods shows thumbnails square (the installed mods' covers are 256 to 1024 px squares). Drawn with shapes from `tools/paradox/thumbnail.html`, without any game assets. |
| `src/SkylineTogether/Properties/Screenshots/*.png` | 1920 x 1080. The UI module rendered by `tools/ui_smoke.mjs --html` in its showcase states (an invented session and players, no build ID), over an original top-down city and with glyphs drawn by `tools/paradox/backdrop.py`; no game assets. |
| (no `ForumLink`) | Paradox Mods only accepts a Paradox forum URL there ("Must be of acceptable format for a Paradox forum url"); the GitHub link is an `ExternalLink`. |
| `tools/paradox/make_images.py` | Renders the thumbnail and the screenshots with a headless Edge or Chrome. Run it after UI changes and look at the images before you commit them. |
| `tools/paradox/publish_paradox.py` | Checks everything and runs ModPublisher (see below). |

The tag `Code Mod` is the only tag code mods on Paradox Mods carry; there is no multiplayer tag. `GameVersion` is
`1.6.2*`, the game version the build is compiled and tested against. Update it when a new game version is tested (the
platform accepts patterns such as `1.6.*` and `1.6.2*`).

## Before every upload

1. **Version and changelog.** `<Version>` in `Directory.Build.props`, `ModVersion` in `PublishConfiguration.xml` and a
   `## [<version>]` section in `CHANGELOG.md` must agree; the script refuses otherwise. Put a short version of that
   section into `ChangeLog` (the page shows it with the version).
2. **Build once, from a pushed commit, and test that build.** Follow [releasing.md](releasing.md) up to the test:
   `tools/build.sh --package` and `python tools/installer/make_installer.py`. The script uploads the files of
   `dist/SkylineTogether-<version>.zip`, the same files the GitHub setup installs. A Paradox Mods player and a
   GitHub player then have the same **build ID** and can play together. Do not rebuild after testing.
3. **Check.** `python tools/paradox/publish_paradox.py check`. It validates the config and the images with the rules
   of the upload command (`Release` while `ModId` is set, `Publish` while it is empty; `--for <command>` to check
   another), checks that the zip of `<Version>` was built from the clean `HEAD`, extracts the
   content folder `dist/paradox/SkylineTogether` from the zip, prints the **build ID** (it must equal the one
   `make_installer.py` printed and the one in the GitHub release notes), scans the content and the config for
   personal data, writes `dist/paradox/PublishConfiguration.xml` (absolute image paths, multi-line texts without
   indentation) and prints the ModPublisher command.
4. **Look at the page text.** Read `LongDescription` once more. Is it still true? Check what is shared, the
   requirements, the known limitations and the links.

## First upload (done)

The first upload created mod 163578 with version 0.1.0-alpha.4. The steps are kept for reference.

1. Do the steps of [Before every upload](#before-every-upload). Leave `ModId` empty.
2. Optional: set `AccessLevel` to `Unlisted` for the first upload, check the page, and switch to `Public` later with
   `Update`.
3. Start the game and log in to your Paradox account in the main menu. Leave the game running.
4. Run:

   ```sh
   python tools/paradox/publish_paradox.py Publish
   ```

   It shows the command and asks for `yes` before uploading. ModPublisher prints `Mod published with Id=<number>`.
   The script writes that number into `ModId` of `PublishConfiguration.xml`.
5. Commit the `ModId` change (`git commit -m "Paradox Mods id"`) and push it. Without it, the next upload would create
   a second mod.
6. Open the mod page (`https://mods.paradoxplaza.com/mods/<id>/Windows`), check texts, images and the version, and
   add the link to the GitHub release notes and to the README's install section.
7. Subscribe on a test PC (without a local `Mods\SkylineTogether` folder) and check: the mod loads, the panel's footer
   shows the build ID from step 3 without "(mvid)", and `Logs\SkylineTogether.log` says `loaded from Paradox Mods (id
   <id>, version 1), build <build id>, loaded`.

## A new version

1. Release the version as in [releasing.md](releasing.md), steps 1-6. The release commit already sets `ModVersion`
   (equal to `<Version>`) and replaces `ChangeLog` with the short version of the new changelog section.
2. Check that `ChangeLog` still matches the CHANGELOG section; change nothing after the build.
3. Do the steps of [Before every upload](#before-every-upload).
4. With the game running and logged in:

   ```sh
   python tools/paradox/publish_paradox.py Release
   ```

   It asks once, then runs `NewVersion` (files, version, changelog) and, when that succeeded, `Update` (page texts,
   thumbnail, screenshots). If `Update` fails, the new version is already up: run
   `python tools/paradox/publish_paradox.py Update` once the problem is fixed.
5. Open the mod page and check the version, the changelog, the texts and the images.
6. Subscribed players get the update when they start the game. Players of a session must all be on the same build:
   tell players who use the GitHub setup to install the matching release.

## Changing only the page

Edit `PublishConfiguration.xml` (texts, images, tags, links, `GameVersion`, `AccessLevel`), then:

```sh
python tools/paradox/publish_paradox.py Update
```

`Update` also needs the release zip for the version in `Directory.Build.props`, because ModPublisher gets a content
folder; it does not upload new mod files.

## How a Paradox Mods install differs from a local one

The game loads subscribed mods from
`%USERPROFILE%\AppData\LocalLow\Colossal Order\Cities Skylines II\.cache\Mods\pdx_mods\<id>_<version>`. It adds a
`.metadata\thumbnail.png` and a `.cpatch` folder there. The mod's own files keep the layout of the content folder. The
code makes no assumptions about where it is installed:

- **DLL and build ID.** The game loads mod DLLs from their bytes (`Assembly.Load(byte[])`), so
  `Assembly.Location` is empty in both kinds of install. The build ID hashes the file that the game's mod manager
  loaded (`ModManager.TryGetExecutableAsset` → `ExecutableAsset.path`, else the asset's read stream;
  `Diagnostics/InstallInfo.cs`). That is the same SHA-256 the installer prints. Only if that fails does the id fall
  back to the assembly's MVID, shown as "(mvid)" and explained in the log.
- **UI module.** The game registers every `*.mjs` it finds next to a mod and serves it as
  `coui://ui-mods/SkylineTogether.mjs` from whichever folder it is in. The module loads nothing from its folder. Icons
  come from the game's own `Media/` paths.
- **Texts.** The locale tables are embedded resources of the DLL (`Data/*.json`). No files are read at runtime.
- **Settings** are stored by the game (`ModsSettings`), **logs** go to the game's `Logs` folder, solo test recordings to
  `ModsData\SkylineTogether`, and snapshots to the system temp folder. None of these depend on the install folder.
- The log line `loaded from ...` says which copy runs: `local Mods folder` or `Paradox Mods (id, version)`. It never
  contains a path.

## Installed twice

A local copy (from the GitHub setup, `tools/build.sh --deploy` or a manual install) and a Paradox Mods subscription can
be installed at the same time. The game then sees two `SkylineTogether.dll` and loads only one: the loaded or local one
first (`ExecutableAsset.GetUniqueVersionAsset`). It marks the other as "not unique", but it still registers **both** UI
modules. With two different builds this means a panel that does not match the loaded DLL, "different build"
refusals, or crashes.

Skyline Together detects this at start (`InstallInfo.CheckAtStartup`). It counts the DLLs and the UI modules named
`SkylineTogether`, writes every copy with its source and build ID into the log and the diagnostics dump, and shows a
notification that stays until it is clicked. The fix is to keep one copy: delete
`%USERPROFILE%\AppData\LocalLow\Colossal Order\Cities Skylines II\Mods\SkylineTogether` (or run the installer with
`-Uninstall`), or unsubscribe on Paradox Mods. Then restart the game. The installer warns about this too when it finds
a subscription.

Developers who use `tools/build.sh --deploy` while subscribed should unsubscribe or disable the mod in their playset.

## Without the script

`publish_paradox.py` only automates and checks the steps below. To do them by hand (PowerShell, repository root):

```powershell
# the content folder: the files of the tested release zip, without README.txt
Expand-Archive dist\SkylineTogether-<version>.zip -DestinationPath dist\paradox-manual
Remove-Item dist\paradox-manual\SkylineTogether\README.txt
# the template runs ModPublisher in the project folder, so the relative image paths assume it as the current folder
Set-Location src\SkylineTogether
$env:DOTNET_ROLL_FORWARD = 'Major'   # only without a .NET 6 runtime
& 'C:\Program Files (x86)\Steam\steamapps\common\Cities Skylines II\Cities2_Data\Content\Game\.ModdingToolchain\ModPublisher\ModPublisher.exe' `
    Publish Properties\PublishConfiguration.xml -c ..\..\dist\paradox-manual\SkylineTogether -v
```

Replace `Publish` with `NewVersion` or `Update` as above (a release: `NewVersion`, then `Update`). Run `python tools/check_personal_data.py` and compare the
build ID before you upload, as the script does.

The game's editor has its own upload for assets and maps. Code mods use ModPublisher, the toolchain's route.
