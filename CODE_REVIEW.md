# Code review (version 0.7.0)

Reviewed against common practice for BepInEx/Unity mods, network services and web pages. Nothing here is known to break the mod today. Items under "Recommended next" are not changed yet. This pass added comments, two web-page fixes and two safety fixes (the High items below, now done).

## Already good

* Game objects are touched only on the main thread; other threads use queues and cached strings.
* The Modbus server and the web server listen on loopback by default.
* Web POSTs need a custom header, and no CORS headers are sent.
* The ST interpreter caps WHILE loops and turns a runtime exception into a stopped program with a message.
* Errors are logged once or rate-limited, so a bad device cannot flood the log.
* The I/O list is written through a temporary file and addresses are never reused.

## Fixed in this pass

| Item | Change |
| --- | --- |
| `esc()` in `index.html` did not escape quotes, and one message put the name prefix into HTML unescaped | `esc()` now also escapes `"` and `'`, and the prefix is escaped. Today names are cleaned so it was not exploitable, but the helper was unsafe to reuse |
| Comments | Added file, class and method notes plus `ARCHITECTURE.md` |
| Parser had no nesting or length limit, so a huge program could overflow the stack and crash the game | `StCompiler` now refuses programs over 5,000 tokens or 64 levels of nesting with a normal compile error (`MaxTokens`, `MaxDepth` in `StRuntime.cs`) |
| A Scale of 0, negative or NaN in the CSV would divide by zero on a write | `IoMap.Load` replaces it with 1 and reports how many rows it fixed in the log |

## Recommended next (Not changed)

| Priority | Where | Issue | Suggested fix |
| --- | --- | --- | --- |
| Medium | `IoMap.Save` | Deletes the old file, then moves the new one. A crash between the two loses the list | Use `File.Replace(tmp, path, null)` when the file exists |
| Medium | `Web.Handle` | The body-size check runs after `ReadToEnd`, so a huge upload is read first | Check `ContentLength64` before reading, and read with a size cap |
| Medium | `ModbusServer` | One thread per client with no read timeout or client limit | Set `ReceiveTimeout`, cap concurrent clients (for example 8) |
| Medium | `StRuntime.Save` | `File.WriteAllText` directly onto the program file | Write to a temporary file and replace it |
| Low | `StRuntime.Source`, `_pendingMode` | Read from the web thread without `volatile` or a lock | Make `_pendingMode` volatile, and read `Source` under `Gate` |
| Low | `IoMap.Save` | Tags starting with `-` could be read as a formula if the CSV is opened in a spreadsheet | Prefix such cells with an apostrophe or strip a leading `-`, `=`, `+`, `@` |
| Low | `RegisterOutput` | Seeding a register uses `ToRaw` without reporting a clamp | Call `ReportClamp` there too |
| Low | Project | No automated tests | Add a test project for the lexer/parser/interpreter and the Modbus frame handling. Both are pure logic and need no game |
| Low | Project | Hand-formatted JSON strings in `StateJson` and `BuildPoints` | Fine at this size; switch to a small writer if more fields are added |

## How to check a change

1. Build. 2. Restart the game fully. 3. Confirm the first line of `com.stationeersplc.iolist.cfg` shows the new version. 4. Open `http://127.0.0.1:15882/`, load a lesson, press Compile and Run. 5. Run `python modbus_test.py list`.
