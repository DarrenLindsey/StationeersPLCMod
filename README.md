# Stationeers PLC 0.7.0

Exposes every device with a logic interface as Modbus TCP points, so a real or virtual PLC
(OpenPLC, CODESYS, Python) can monitor and control the game. The mod is the Modbus **slave**; the PLC connects as client.

## Build
Open `StationeersPlc.sln` in Visual Studio, set `GameDir` in the csproj if the game is not in the default Steam path, build Release.
Close the game, then copy `StationeersPlc.dll` to `Stationeers\BepInEx\plugins\StationeersPlc\`.

## Tables
| Table | Modbus functions | Meaning |
|---|---|---|
| DI | 2 | read-only bits: device status |
| COIL | 1, 5, 15 | read/write bits: commands (On, Open, Lock...) |
| IR | 4 | read-only 16-bit numbers: measurements |
| HR | 3, 6, 16 | read/write 16-bit numbers: setpoints |

Addresses start at 0. The CSV also lists the 6-digit style (`000001`, `100001`, `300001`, `400001`).
Numbers are scaled signed 16-bit: raw = game value x Scale. Gas ratios use 1000, everything else 1. Out-of-range values clamp.

## The I/O list
`BepInEx/config/StationeersPlc_iolist.csv` is the permanent address map and the student handout.
Addresses are never reused. Edit the Scale column by hand to change scaling. Delete the file to start over.
Writes to unassigned coil/HR addresses return exception 2.

## Settings
`BepInEx/config/com.stationeersplc.iolist.cfg`: port (5020), bind address (127.0.0.1), refresh rate, AllowWrites, excluded signals, IncludeItems, debug logging.
The server has no authentication: keep it on 127.0.0.1 unless you trust your network.

## New in 0.7.0
- **Name filter:** set `OnlyNamePrefix` (Devices section of the config) to `PLC` and only devices whose name starts with PLC are exposed
  (PLC-Light, PLC-Pump...). Name devices in the game, and they appear within a second. Empty = every device, as before.
- **Force switches:** in "Program variables (live)", press 0 or 1 beside a variable (or type a number and press Enter) to set an input from the page; press release to stop forcing.
  Works on plain variables and on inputs (%IX / %IW). Outputs cannot be forced. In the ladder, select a contact to use Force ON / OFF.
- **Lessons tab** with five guided exercises and starter ladders, plus more examples in the dropdown.
- **Undo / Redo** in the ladder editor (Ctrl+Z / Ctrl+Y).
- **Seven themes:** Dark, Light, LCARS, Terminal, Blueprint, Solar, Borg (picker at the top right).
- **Configurable table size:** `[Modbus] TableSize` (default 65536, range 64 to 65536) sets the number of addresses in each of the four tables. Changing it needs a game restart. If the saved I/O list already uses higher addresses, the tables are enlarged automatically so no point is lost.
- **Remove offline points:** a **Remove N offline points** button in the I/O list clears rows for devices that are no longer in the game. It is refused while the program is running or before all devices are listed. Removed addresses are not handed out again, even after a restart (the CSV ends with a `#NextFree` line that remembers this; do not delete it).
- **Renames are followed:** renaming a device in the game updates its tag in the I/O list within about a second. Its addresses do not change.
- The log now names any signal that does not fit in a 16-bit register, with the row to fix in the I/O list CSV.

## Ladder editor, friendly I/O list, theme (0.6.0)
The workstation page opens on the **Ladder** tab. Click a **+** to add a contact, compare block, timer or coil; click a part to edit it
(variable, normally open/closed, "Add OR branch" for parallel paths). Wires turn on-colour while power flows, live from the running program.
**Compile** converts the ladder to Structured Text (see **Show ST**) and installs it; Run/Stop work as before.
Compiling from the Ladder tab replaces the ST program; the ladder itself is saved next to it as `StationeersPlc_program.ladder.json`.
Examples: a timer blinker and the classic Start/Stop seal-in.
**Devices & I/O** groups points by device with plain-language names and ON/OFF pills. Filter by kind, search, and press **+ use** on a
signal to add it to your ladder variables (or your ST VAR block) with the right address already filled in.
The **LCARS theme** button restyles the page (remembered in the browser).

## In-game Structured Text PLC (0.5.0)
Open **http://127.0.0.1:15882/** in a browser (a second monitor works well). The page has an ST editor, Compile / Run / Stop,
a variable watch and the live I/O list. The program is saved to `BepInEx/config/StationeersPlc_program.st`. The mod always starts in STOP.

Editor help (0.5.1): suggestions pop up as you type (keywords, your variables, timer members `.Q` `.ET`, snippets such as `if` and `timer`,
and real addresses after `%` with the device name beside them). Ctrl+Space opens the list, Up/Down/Enter/Tab choose, Esc closes.
Errors and unassigned-output warnings appear under the editor as you type; click an error to jump to its line.

Variables bind to the same tables a Modbus PLC uses, by the **Address** column of the I/O list:

| Declaration | Table |
|---|---|
| `X AT %IX5 : BOOL;` (or `%IX0.5`) | DI 5 (byte*8+bit) |
| `X AT %QX5 : BOOL;` | COIL 5 |
| `X AT %IW5 : INT;` | IR 5 (raw scaled value) |
| `X AT %QW5 : INT;` | HR 5 |

Supported: PROGRAM, VAR, BOOL INT UINT DINT REAL TIME, `TON` timers (`T1(IN := a, PT := T#5s); T1.Q`), IF/ELSIF/ELSE, WHILE,
`:=`, AND OR XOR NOT, + - * / MOD, comparisons, ABS MIN MAX LIMIT, `(* *)` and `//` comments.
Not yet: FOR, CASE, functions/function blocks you write yourself, REAL scaling (values are the raw register numbers).

Safety: RUN refuses to start if an output address is not an assigned coil/HR. While RUN, Modbus writes are refused
(`BlockModbusWritesWhileRunning`, default true) so two controllers never fight over an output. STOP leaves devices as they are.
A fault (for example a runaway WHILE) stops the program and shows the reason. The page is loopback only; POSTs need an `X-Plc` header.

## Test
`python modbus_test.py list` / `python modbus_test.py coil 0 8` / `python modbus_test.py setcoil 3 1`
