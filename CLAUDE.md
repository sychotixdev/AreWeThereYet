# AreWeThereYet

## What this is

AutoPilot plugin for **Path of Exile**, built on **ExileCore** (a third-party PoE overlay/plugin
framework). It's a "follow bot": it reads the leader's party UI element and the local game state
via ExileCore's UI/entity trees, then drives the mouse/keyboard to follow the party leader between
zones — pathfinding around terrain, using portals/area transitions when available, and falling
back to the party "Teleport to player" button + confirmation popup when no transition is found.

Main logic lives in `AutoPilot.cs` (~large file, the whole follow/transition/teleport state
machine). Other files: `Mouse.cs` / `Keyboard.cs` (input helpers), `PartyElements.cs` (party UI
element wrappers), `TaskNode.cs` (movement/transition task queue), `PathFinder/` (terrain
pathfinding), `Utils/`, `AreWeThereYetSettings.cs` (ExileCore settings UI bindings).

## Build constraints — I cannot build or run this project

- Targets `net10.0-windows7.0` with `UseWindowsForms=true` and links against `ExileCore.dll` /
  `GameOffsets.dll` via the `$(exapiPackage)` env var (an external ExileCore/PoE HUD install path
  that only exists on the user's Windows machine).
- This is Windows-only and depends on binaries not present in the sandbox — `dotnet build` will
  never work here. **Do not attempt to build, run `dotnet build/run/test`, or verify compilation
  in the shell.** Rely on careful reading, `grep`/pattern-matching against existing conventions in
  the file, and asking the user to build/test in their own environment (Visual Studio / ExileCore
  dev loop) after edits.
- There is no automated test suite. Verification = code review + user in-game testing.

## Established conventions worth knowing (saves re-deriving each time)

- **Screen coordinates**: `Element.GetClientRect()` / `GetClientRectCache` return
  **window-relative** coordinates. `Mouse.SetCursorPos` / `SetCursorPosHuman` need **absolute**
  screen coordinates. Every click-position helper must add
  `GameController.Window.GetWindowRectangle().TopLeft` before moving the mouse, or clicks land
  correctly only when the game happens to run at (0,0) (borderless fullscreen) and miss in
  windowed mode. See `GetTpButton`, `GetLabelClickPosition` for the reference pattern.
- **Zone name comparisons**: the party UI's `ZoneName` always carries a trailing area-level
  suffix like `" (9)"`; `GameController.Area.CurrentArea.DisplayName` never does. Always compare
  through `StripZoneLevelSuffix(...)` on both sides, never compare raw.
- **Popup/confirmation detection**: `GetTpConfirmation()` checks a hardcoded UI index path
  (`PopUpWindow.GetChildFromIndices(0,0,0)`) against the exact "Are you sure you want to
  teleport..." string. `FindButtonByText(root, text)` is a more robust recursive fallback used for
  generic single-button ("OK") popups. `GetOpenPopupClickPosition()` combines both.
- Coroutine-heavy (`IEnumerator` + `yield return new WaitTime(...)`) — this is an ExileCore
  plugin, not a normal async C# app; long-running logic is written as coroutines pumped by the
  ExileCore plugin loop.
