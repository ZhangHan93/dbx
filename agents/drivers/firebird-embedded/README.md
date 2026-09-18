# Firebird Embedded Agent

A **Firebird Embedded** connector agent for DBX, implemented as a native agent in C#/.NET 8 on top of `FirebirdSql.Data.FirebirdClient`. No JRE, no ODBC driver manager, no Firebird *service* installation — the engine DLLs ship inside this folder.

It implements the DBX agent protocol: **JSON-RPC 2.0 over stdin/stdout** (newline-delimited), printing `{"ready":true}` on startup.

## Why a separate driver

Firebird databases are single files (`.fdb` / `.gdb`, and older `.g_b`). Opening one with the **Embedded** engine needs no server process, which makes it the only workable path for:

- inspecting a customer's database file handed to us on a disk / copied off a production server,
- a database whose Firebird **version we do not know in advance** (see ODS below),
- databases that must not be touched at all — no `ServerMode`, no configuration changes.

ODBC cannot serve this case: a Firebird ODBC driver is **not** shipped here in x86 form, and Firebird's ODBC driver has no embedded mode at all.

## Architecture constraint: one process, one engine

**How many connections share one `agent.exe`?** Not one — it depends on the process-wide mode switch `DeclareMultiSession` in `Program.cs`, and the answer was only settled by reading upstream (2026-09-18), because an earlier *measurement* had said the opposite:

| | `DeclareMultiSession = true` (default) | `DeclareMultiSession = false` |
|---|---|---|
| DBX path | shared runtime | legacy fallback (`spawn_client_for_key`) |
| Processes | **1 per `db_type`**, N sessions | **2 per connection** (Metadata pool + Query pool) |
| "One process, one engine" | code discipline + `replace_runtime` recovery | **architectural guarantee** |
| Two connections, different ODS | ❌ they evict each other | ✅ coexist |

Upstream runtime caching is keyed by `agent_key|program|args|working_dir` (`agent_runtime.rs::shared_runtime_key`) — **connection id is not part of the key** — so declaring `multi_session` collapses every connection of a `db_type` into one process. Dropping `multi_session` makes `AgentRuntimeClient::spawn` reject the shared runtime (`agent_driver.rs:101`), which sends the connection down the legacy per-process path.

> The earlier note "two connections with an identical `db_type` get two independent processes" was measured **before** multi-session was implemented, when every connection took the legacy fallback. It is no longer true for a multi-session agent. Do not rely on it.

Under **both** modes the agent enforces "one engine per process" in code, in `EnginePinning.cs`:

| # | Rule | Why |
|---|---|---|
| 1 | The first successful attach **pins** the engine for the lifetime of the process. | The native plugin layer has no fix: Windows resolves `engine1x.dll`'s `fbclient.dll` dependency **by base name**. |
| 2 | A request for a *different* engine **fails loudly**; it never loads a second one. | A second engine silently binds to the first `fbclient.dll` and produces a **mixed-version engine**. Its query results are not trustworthy and the module list does not reveal it. |
| 3 | A failed load marks the engine **unusable** for the whole process. | Closes the "fail → retry → load a different engine" path, which is the most dangerous one. |

Rejection is not a dead end. The error carries `sessionDisposition: "replace_runtime"` (`AgentFault.cs`), and DBX responds by **killing the agent process and starting a fresh one**, so reopening the connection just works. Verified end to end:

```
open_session A (ODS 10, fb25)  -> ok
open_session B (ODS 13, fb50)  -> ERROR ... sessionDisposition = replace_runtime   ✔
list_tables  A (after the rejection) -> ok    (the guard never breaks live sessions)
```

> ⚠️ `replace_runtime` makes DBX drop **every connection sharing that runtime**, not just the failing one. In shared mode that means a rejected second connection disconnects the first as well. This is upstream behaviour, and it is why `DeclareMultiSession` is worth revisiting (see the table above).

### Bitness

All three bundled engines are **Win32 (x86)**, and Firebird's ODBC-free embedded path is bitness-bound at the native level. This agent is therefore built **x86 only** — never AnyCPU, never x64.

## ODS ↔ engine mapping (strict, not forward-compatible)

A Firebird file's on-disk format (**ODS**, On-Disk Structure) version decides which engine can open it. The mapping is **one-to-one** — measured, not documented-by-inference:

| ODS | Engine | Directory | Entry DLL |
|---|---|---|---|
| 10.0 – 11.2 | Firebird 2.5.9 | `engines/fb25_x86` | `fbembed.dll` |
| 12.0 – 12.1 | Firebird 3.0.14 | `engines/fb30_x86` | `fbclient.dll` |
| 13.0 – 13.1 | Firebird 5.0.4 | `engines/fb50_x86` | `fbclient.dll` |

> FB 4.0 is **not** needed: ODS 13.x is covered by the 5.0 engine.
> ⚠️ FB 2.5's entry point is `fbembed.dll` — **do not rename it** to `fbclient.dll`. The dependency is by base name, so renaming makes things strictly worse (error 127 "entry point not found" degrades into error 126 "cannot open at all").
> ⚠️ FB 3.0+ ships **no separate embed package**; use `fbclient.dll` from the standard package plus `plugins/engine1x.dll`.

## How the file type is identified

Before any engine is loaded, the agent reads 0x42 bytes of the file header (`FirebirdHeader.cs`). Four checks, in order — the order matters, because reading only the ODS byte yields false positives on *any* file:

1. page type `u16 @ +0x00 == 1` (the strongest discriminator)
2. page size `u16 @ +0x10` ∈ {1024, 2048, 4096, 8192, 16384}
3. ODS major `u8 @ +0x12`, ODS minor `u16 @ +0x40`
4. SQL dialect: `u8 @ +0x2A` bit 4 set ⇒ dialect 3, else 1

> ⚠️ `+0x13` is **not** the ODS minor — on ODS 11+ it holds a `0x80` flag. The minor lives at `+0x40`.
> ⚠️ Do **not** use `MON$DATABASE` to read ODS/dialect — old ODS 10 databases have no `MON$` tables at all.

The header is re-read **after** `Open()`, and the agent refuses the database if the ODS changed. That turns "we never upgrade the customer's file" from a claim into an observable invariant.

`Dialect` is written into the connection string explicitly: on a dialect-1 database, omitting it makes `SELECT 7/2` return `3` instead of `3.5` — **no error, wrong number**.

## Connection input

The `.fdb` path is carried in the **`host`** field (same convention as `access` / `sqlite` / `duckdb`), so the desktop's local-file form, the Rust parameter plumbing, and `agent_connect_params` all work unchanged. `database` is accepted as an optional display alias only and never overrides the file path.

- `host` — absolute path to the database file (**required**)
- `username` — defaults to `SYSDBA`
- `password` — empty falls back to the factory `masterkey`, because the bundled `security3.fdb` / `SECURITY5.FDB` are brand-new files

## Character sets

Firebird databases store text **per column**, using the column's character set. On a `CHARACTER SET NONE` database (typical for older Chinese installations, e.g. `病例.g_b`), Firebird performs **no** conversion and the provider hands back the stored bytes **1:1 as characters** — so GBK text arrives as mojibake such as `²¡Àý`. It is byte-for-byte recoverable, and `FirebirdText.cs` does exactly that (`Latin1` → strict UTF-8 → GBK).

It runs **only when `RDB$DATABASE.RDB$CHARACTER_SET_NAME` is NULL** (i.e. NONE). Databases with a real charset are already decoded correctly by the provider, and a round trip would only corrupt them.

## Build

Requires the .NET 8 SDK.

```powershell
.\build.ps1
```

Produces `publish/agent.exe` plus `publish/engines/`, which is exactly the content of `data/agents/drivers/firebird-embedded/`. Roughly 90 MB unpacked.

`engines/` is **not in git** (~89 MB). CI has no `engines/`, so `build.ps1` guards the copy with `Test-Path` and CI produces only `agent.exe`; the engines are copied in locally at packaging/deployment time.

## Offline package layout

```
base/data/agents/drivers/firebird-embedded/agent.exe
base/data/agents/drivers/firebird-embedded/engines/fb25_x86/...
base/data/agents/drivers/firebird-embedded/engines/fb30_x86/...
base/data/agents/drivers/firebird-embedded/engines/fb50_x86/...
```

`firebird-embedded.yaml`'s `agentKey: firebird-embedded` resolves to `data/agents/drivers/firebird-embedded/agent.exe`.

## Operational notes

- **The engine always writes the original file** — even a pure `SELECT` changes the mtime and the content hash. On a sync folder (OneDrive, Baidu Syncdisk) that triggers an upload on every open.
- **While we hold the file open, a customer Superserver cannot open it.** Whether this conflicts depends only on each side's `ServerMode`, not on who opened first; any side using `Super` means only one side wins. `fb30` / `fb50` ship with `ServerMode = SuperClassic` in `firebird.conf`.
- **Never set the `FIREBIRD` environment variable** — it is process-wide and overrides config resolution for every engine in the process.
- `ClientLibrary` is always an **absolute path**. A bare name resolves to `%WINDIR%\System32\fbclient.dll` on any machine that has Firebird installed and then fails with a `xnet://Global\FIREBIRD` error that points nowhere near the real cause.
- FB 2.5's embedded engine additionally needs a writable `C:\ProgramData\Firebird` for its shared lock files.

Reference: `agents/docs/firebird-embedded-plan.zh-CN.md` (the implementation plan) and `DBX-自编驱动避坑手册.md` (pitfalls collected while building the ODBC agent).
