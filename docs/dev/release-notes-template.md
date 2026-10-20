# Release notes template

The notes of every GitHub release follow this structure ([releasing.md](releasing.md), step 7). Copy the block
below, replace the `<...>` placeholders and delete sections that do not apply. Keep the wording plain: what changed for
players first, technical detail only where a player needs it.

- **Title:** `Skyline Together <version>` (no suffix such as "test build").
- **Pre-release:** only for a version with a pre-release suffix (`-beta.1`); regular releases are not pre-releases.
- **Assets:** `SkylineTogether-Setup.zip` and `SkylineTogether-<version>.zip`.

````markdown
<One or two sentences: what this release is about. For a patch release: "Bug-fix release: <the main fixes>.">

Everyone in a session needs the same version. <Only with a protocol change: "This release changes the network
protocol, so it cannot play with earlier versions.">

## What's new

<The CHANGELOG.md section of this version, shortened: one plain sentence per entry, grouped as Added / Changed /
Fixed. Link the full section: https://github.com/ljkx/SkylineTogether/blob/main/CHANGELOG.md>

## Works with your mods

Dedicated support for Traffic, Move It, Anarchy, Better Bulldozer, Tree Controller, Water Features, Plop the
Growables, 529 Tiles, Historical Start, Zoning Toolkit, Line Tool and Find It; every other code mod is analysed
automatically. <Mention changes to mod support in this release, if any.> Details:
[MOD-COMPATIBILITY.md](https://github.com/ljkx/SkylineTogether/blob/main/docs/design/MOD-COMPATIBILITY.md)

## Install

**From Paradox Mods (recommended):** subscribe to
[Skyline Together](https://mods.paradoxplaza.com/mods/163578/Windows) and restart the game. Subscriptions update when
the game starts.

**With the GitHub setup:** close the game, download `SkylineTogether-Setup.zip` below, unzip it and double-click
`Install.cmd`. Windows SmartScreen may warn about a downloaded script: check the checksum below, then choose
**More info → Run anyway**. Use one of the two routes, not both.

`SkylineTogether-<version>.zip` is the same mod for a manual install into the `Mods` folder.

Back up your saves before you host or join.

## Build id

`<build id>`: shown at the bottom of the Skyline Together panel. The Paradox Mods version and this setup have the
same build id, so players of both routes can play together. Built and tested with game version `<game version>`.

## Checksums (SHA-256)

```
<sha256>  SkylineTogether-Setup.zip
<sha256>  SkylineTogether-<version>.zip
```

To check a download, run in PowerShell:

```
Get-FileHash .\SkylineTogether-Setup.zip -Algorithm SHA256
```

The hash must match the line above (letter case does not matter).

## Reporting bugs

Open an [issue](https://github.com/ljkx/SkylineTogether/issues/new/choose) with the logs of every player:
[Reporting a bug](https://github.com/ljkx/SkylineTogether/blob/main/docs/guide/troubleshooting.md#reporting-a-bug)
explains where they are for both install routes and what they contain (player names, Steam IDs and the session name;
Skyline Together writes your user folder as `%USERPROFILE%`). Never attach crash dumps (`.dmp`) to a public issue.
````

The checksums are printed at the end of `python tools/installer/make_installer.py`. Compute them again after any
rebuild; a rebuilt zip has a different checksum and a different build id.
