# Changelog

## 0.8.0-beta (branch `beta`, not yet built or tested in game)
- **Practice Bench**: `standalone/StationeersPLC-Practice.html` runs the whole workstation offline with simulated devices (light and button, beacon, tank pump, airlock), goals and faults. No game needed. Rebuild it with `python3 build_practice.py`.
- Function blocks in Structured Text: R_TRIG, F_TRIG, SR, RS, TOF, CTU, BLINK, HYST, PID, SOLAR_TRACK, AIRLOCK, plus your own with FUNCTION_BLOCK. `DT()` gives ms since the last scan.
- Function block palette in the ladder editor.
- Clickable ON/OFF switches in the I/O list (while the PLC is stopped).
- Ladder rungs stretch to the window width; new lessons 6 to 9.
- The C# changes (StRuntime.cs, StLibrary.cs, WebServer.cs, Plugin.cs) have not been compiled yet.

## 0.7.0
- Remove offline points button, with persistent address high-water marks (`#NextFree` line in the CSV).
- Device renames in the game are followed in the I/O list.
- `[Modbus] TableSize` setting.
- Program size and nesting limits (5,000 tokens, 64 levels) so a huge program cannot crash the game.
- A Scale of 0, negative or NaN in the CSV is treated as 1.
- `OnlyNamePrefix` device filter.
- Force switches, five lessons, ladder undo/redo.
- Seven themes, including LCARS and Borg.
- Address dropdown no longer closes or resets its scroll while you use it.
- Names put into the page are escaped.
- Documentation: ARCHITECTURE.md, CODE_REVIEW.md, comments throughout.

## 0.6.0
- Ladder editor, friendlier I/O list, LCARS theme.

## 0.5.x
- In-game Structured Text PLC and web workstation, editor autocomplete.

## 0.4.1
- Modbus TCP bridge and permanent I/O list.
