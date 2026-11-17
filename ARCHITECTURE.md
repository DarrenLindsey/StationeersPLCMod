# Architecture notes (version 0.7.0)

Read this first if you are new to the code. It explains what each file does and the one rule that matters most: **only the Unity main thread may touch game objects.**

## Files

| File | Job |
| --- | --- |
| `Plugin.cs` | BepInEx entry point. Reads settings, loads the I/O list, starts the servers, creates the driver object. |
| `PlcRuntime.cs` | The per-frame loop (`PlcRuntime.Tick`) and the hidden `PlcDriver` object that calls it. |
| `GameAdapter.cs` | The only code that touches game devices. Finds devices, copies values into `PointStore`, applies queued writes. |
| `PointStore.cs` | The four Modbus tables (DI, COIL, IR, HR) as plain arrays, plus the "assigned output" flags. |
| `IoMap.cs` | Permanent address allocation and the CSV I/O list. |
| `SignalCatalog.cs` | Which signals are on/off, which are excluded, default scales, name cleaning. |
| `ModbusServer.cs` | Modbus TCP slave (function codes 1, 2, 3, 4, 5, 6, 15, 16). |
| `StRuntime.cs` | Structured Text lexer, parser, interpreter and the PLC run loop. |
| `WebServer.cs` | Local web server for the workstation page and its small JSON API. |
| `WebUi/index.html` | The whole browser page (embedded in the DLL). |
| `Stats.cs` | Counters and the status line in the log. |

## Data flow

```
Game devices --(GameAdapter.Refresh, main thread, every 0.25 s)--> PointStore --(Modbus threads)--> outside PLC
                                                                       ^  |
In-game ST program --(StRuntime.Scan, main thread, every 0.1 s)--------+  +--> web page (cached JSON)

Outside PLC / ST program output --> PendingWrites queue --(PlcRuntime.Tick, main thread)--> GameAdapter.Apply --> game device
```

## Threads

| Thread | May do | Must not do |
| --- | --- | --- |
| Unity main | Everything, including game objects and the ST program | Block for long |
| Modbus accept / client | Read and write `PointStore`, enqueue to `PendingWrites`, update `Stats` with `Interlocked` | Touch game objects or the ST program |
| Web (`PLC-web`) | Serve cached strings, call `StRuntime.Submit / RequestMode / Force / Release / Check` | Touch game objects or build state itself |

`PointStore` elements are single `bool` / `short` values, which are read and written atomically, so it needs no lock. Anything bigger (dictionaries, lists, the ST program) belongs to the main thread.

## Addresses

An address is handed out the first time a (device, signal, table) combination is seen and is stored in `StationeersPlc_iolist.csv`. It is never reused, even after the device is gone, so programs keep working. Scaled registers store `round(game value x Scale)` as a signed 16-bit number.

## ST program life cycle

1. The web page posts source to `/api/program`. `StRuntime.Submit` stores it. The page can also call `/api/check` for live error checking.
2. On the next tick the main thread compiles and installs it, then saves it to `StationeersPlc_program.st`. A newly installed program is always STOP.
3. `/api/mode?run=1` asks for RUN. `Validate` refuses it if a bound output address is not assigned.
4. While RUN, each scan: read bound inputs, apply forces, run the body, write changed outputs.
5. Any exception during a scan sets `Fault` and stops the program.

## Adding things

* **New signal rule:** `SignalCatalog` (booleans, exclusions, scales).
* **New ST function:** add a case in `StFn.Eval` and allow the name in the parser.
* **New API route:** add it in `Web.Handle`. POST routes automatically require the `X-Plc: 1` header.
* **New setting:** add `Config.Bind` in `Plugin.Awake`, then document it in `README.md`.
* **Version bump:** change `Plugin.PluginVersion` and the top of `README.md`.
