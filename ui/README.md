# ui/

Home of the hand-written UI module (no bundler, no Node dependency at runtime; ARCH §11.2,
IMPLEMENTATION-PLAN §2.6), owned by the game-ui module:

- `SkylineTogether.mjs`: the ES module the game loads from `coui://ui-mods/SkylineTogether.mjs`. Its first block
  comment must contain the exact line `Cities: Skylines II UI Module`, followed by `Id:`, `Author:`, `Version:` and
  `Dependencies:`. `register(moduleRegistry)` appends the menu entry, the HUD strip and the panels, reads only the
  binding names listed in `bindings.generated.json`, and renders nothing when the UI globals are missing.
- `bindings.generated.json`: binding and trigger names generated from the `const string` names of `SessionUISystem`
  (used by the UI smoke check).

The game project's `DeployMod` target copies `SkylineTogether.mjs` next to the DLLs when the file exists.
