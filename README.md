# Hookshot Item Panel

A small Windows window for single-player Zelda: Ocarina of Time running in [Hookshot](https://github.com/NotRexed/Hookshot).
Type a number or tick **Infinite** for rupees, bombs, bombchus, deku nuts/seeds/sticks, business scrub seeds, magic beans,
Gold Skulltula tokens, hearts, magic, carry limits and Epona's carrots. It edits the running game's memory from the outside.
Nothing is injected, and no game files or save files are changed.

**[Download the latest release](https://github.com/adrenodreams/OoT-Trainer/releases/latest)**, unzip, and run
`HookshotItemPanel.exe`. No install and no .NET needed.

## How to use

1. Start Hookshot, load your save and get into the world.
2. Run `HookshotItemPanel.exe`. Put it in your Hookshot folder next to `image.bin`, or pick `image.bin` when it asks
   (only its first 64 bytes are read, to locate the game).
3. Wait for the green "Connected" line, tick **Infinite** on what you want, and click **Activate**. **Set** writes a number once.
4. For Epona's carrots, ride her first; the panel finds her counter by itself.
5. Close the window with the X so it remembers your numbers.

## Good to know

- Windows 10/11, 64-bit. Tested with Hookshot v2.0.2 and the later updates up to v2.0.13 by the author on one save. Other
  builds may place things differently. The panel refuses to write when what it finds does not look right.
- Back up your saves first. Use at your own risk, single-player only.
- Antivirus programs may flag any tool that edits another program's memory.
- Hearts are shown in whole hearts (the game stores quarter hearts). If a count will not rise above a limit, raise that limit first.

## Support

If this saved you some grinding, you can [buy me a coffee](https://buymeacoffee.com/adrenodreams).

## License

[MIT](LICENSE)

---

# Developer notes: OoT trainer (Hookshot build)

External memory trainer for the single-player OoT build run by `hookshot.exe`. It attaches with
`ReadProcessMemory`/`WriteProcessMemory`; nothing is injected. The item panel above is the `OotTrainer.Panel` project; the
CLI (`OotTrainer.Cli`) is the scanner used to find the values.

## Run

```
dotnet run --project OotTrainer.Cli -- "<path>\image.bin" [offsets.json]
dotnet run --project OotTrainer.Cli -- --selftest      # feature logic on synthetic memory, game not needed
```

`image.bin` is only used for its first 64 bytes, to find where the guest address space sits inside the process.

## Workflow: find a value, save it, switch the feature on

1. `scan i32 <value>` while the game shows that value, change it in-game, `next <new value>`; repeat until a few candidates remain.
2. `ptr #0` finds pointers to it. `[S]` slots are static (inside the image) and make stable chain bases. If only heap slots
   show up, run `ptr` on that slot's address, and repeat until you reach an `[S]` slot.
3. `chain 0xBASE -> 0xOFF -> 0xOFF i32` to confirm it resolves, then restart the game and check it still does.
4. `save health i32 0xBASE -> 0xOFF -> 0xOFF`, then `on health`.

## Variable names the features look for (offsets.json)

| Feature | Variables | Notes |
|---|---|---|
| `health` | `health`, optional `health_max` | keeps `health` >= live `health_max`, else `target health <n>` |
| `magic` | `magic`, optional `magic_max` | same |
| `rupees` | `rupees`, optional `rupees_max` | fixed floor 999 unless `target rupees <n>` |
| `inventory` | any saved variable | `set` writes once; `lock` + `on inventory` holds a value |
| `levitate` | `pos_x`, `pos_y`, `pos_z` (f32) | Y assumed up; `up`/`down` change hover height, `tp x y z`, `savepos`/`loadpos` |

Health may be stored in quarter-hearts or another unit. Check what `health_max` reads before choosing a fixed target.

## Behavior notes

- Floor locks only write when the value is below the target; disabling a feature just stops writing.
- A feature that throws (for example a target that does not fit the variable's type) is disabled and its error shows in `features`.
- If a chain does not resolve (loading screen), the feature skips that tick and retries.
- `offsets.json` records the build signature and warns when loaded against a different build.
- The self-test proves the logic. It does not prove that any offset is right for the real game.

## One-click item holds (`infinite.bat`)

Start the game, load a save, then run `infinite.bat` (or `infinite.ps1`). It attaches the CLI, finds the game's live item
table by its marker bytes (entries 0x28 apart, count at +0x1C with a duplicate at +0x20), checks every entry it will touch
carries the marker, then holds rupees at 1000 and bombs, bombchu, nuts, seeds, sticks and scrub seeds at 99. It re-finds the
table on every run, so no address has to survive a restart. Edit the values at the top of the script
(`-ItemCount`, `-RupeeCount`) or the `$Items` index table. Progress is logged to `infinite.log`. It ends when the game closes.

- The table indices were worked out from one build and one save (item `k` = GameData Int list entry `#971 + k`). Another
  build may need new indices; the layout check skips any item whose entry has no marker instead of writing blindly.
- Nothing is written to save files. The game saves its own live values, so saves will show the held numbers.

## Item panel window (`panel.bat`, `OotTrainer.Panel`)

A normal Windows window (no overlay, works with the game in any display mode). Run `panel.bat`; it waits for the game, attaches,
finds the live item table and shows each item with its current number.

- **Activate / Deactivate** is the master switch for holds. Nothing is held while it says Activate.
- Each item has a number box, **Set** (write that number once; the game then counts it down normally) and **Infinite** (hold the item at the
  box number while Activated). Rupees default to 1000; the other items to 99; Gold Skulltula to 999.
- **Hearts and magic**: hearts are shown and edited in whole hearts (the game stores quarter hearts, so 24 raw = 6 hearts; part hearts show
  as 3.25). Infinite on "Hearts now" / "Magic now" keeps you at the live maximum instead of a box number. A bigger max may look odd.
- **Epona carrots** (0 to 6): Infinite keeps all six carrots so you can sprint without waiting. The number shown in the window is a copy
  the game refreshes (a lone int 0x14D4 bytes before the item table's first entry). Holding that copy made the game rewrite it about
  8 times a second while sprinting, and the HUD ticked each time. So the hold goes on Epona's REAL counter instead (one byte in her actor
  data), which `EponaCarrots` finds by its neighbouring bytes, only accepting a hit that matches the copy, and checks before every write.
  Epona's data moves each time she is spawned, so the search runs again by itself (a few seconds) after you re-summon her; ride her first.
  While Infinite is on and she is found, the hold thread keeps one CPU core busy. You still get a tick or two per tap of A (the game
  spends a carrot before it is refilled), but not the constant ticking. `OotTrainer.Panel.exe --epona-live <image.bin>` runs this against
  the real game with no window and logs it to `epona-live.txt`.
- **Limits** (bomb, nut, seed, stick, wallet) can be set the same way. The game may cap a count at its limit, so raise the limit first.
- **Set all items now**, **Infinite: all / none**, **Always on top**. Boxes and ticks are remembered in `panel-settings.json`.
- If the game closes or the table moves, the panel searches again by itself. A write is refused if an entry has lost its marker.
- `OotTrainer.Panel.exe --selftest` (logic) and `--uitest` (drives the real buttons) run against a synthetic table, no game needed;
  results land in `selftest.txt` / `uitest.txt` next to the exe. `--demo [--screenshot file.png]` opens the window on fake data.
- The item indices are the same ones `infinite.ps1` uses (one build, one save). Do not run both at once.

## F2 mod-menu overlay (`OotTrainer.Gui`)

An ImGui window (via `ClickableTransparentOverlay`) drawn in a transparent topmost window over the game. Like the CLI it is
external only: nothing is injected into `hookshot.exe`. (Hookshot's upstream source is at github.com/NotRexed/Hookshot;
the shipped binary contains no ImGui, and its own in-game menu is settings-only.)

```
dotnet run --project OotTrainer.Gui -- "<path>\image.bin" [offsets.json]
dotnet run --project OotTrainer.Gui -- --selftest
```

- Needs the game **windowed or borderless** (`fullscreen=0` in `hookshot_options.ini`); it will not show over exclusive fullscreen.
- **F2** shows/hides the menu (F1 and Insert open Hookshot's own settings menu). **Max Rupees** writes `rupees_max` (if saved) or 999 once; **Hold at max** enables `RupeesFeature`.
- Item buttons write one value to one saved variable. A button is disabled (hover for why) until its variable is saved in
  `offsets.json` **and** it has a value. Values are unknown for this build, so the built-in layout has none: create
  `trainer\items.json` next to `offsets.json` once you have found the IDs. It replaces the built-in layout entirely:

```json
[
  { "name": "Tools",  "items": [ { "label": "Hookshot", "variable": "item_hookshot", "value": 10 } ] },
  { "name": "Swords", "items": [ { "label": "Master",   "variable": "sword",         "value": 3 } ] }
]
```

- The menu logic was tested against synthetic memory only (`--selftest`). The overlay rendering and F1 toggle have not been
  run against the real game, and no real offsets exist yet.
