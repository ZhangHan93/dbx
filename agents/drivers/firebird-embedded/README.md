# Firebird Embedded Agent

A **Firebird Embedded** connector agent for DBX, implemented as a native agent in C#/.NET 8 on top of `FirebirdSql.Data.FirebirdClient`. No JRE, no ODBC driver manager, no Firebird *service* installation — the engine DLLs ship inside this folder.

It implements the DBX agent protocol: **JSON-RPC 2.0 over stdin/stdout** (newline-delimited), printing `{"ready":true}` on startup.

## Why a separate driver

Firebird databases are single files (`.fdb` / `.gdb`, and older `.g_b`). Opening one with the **Embedded** engine needs no server process, which makes it the only workable path for:

- inspecting a customer's database file handed to us on a disk / copied off a production server,
- a database whose Firebird **version we do not know in advance** (see ODS below),
- databases that must not be touched at all — no `ServerMode`, no configuration changes.

ODBC cannot serve this case: a Firebird ODBC driver is **not** shipped here in x86 form, and Firebird's ODBC driver has no embedded mode at all.

## Architecture: one process, one engine

**One connection = one `agent.exe`.** This is a property of the entry point the desktop app takes, and it was **measured
end to end** (2026-09-18), not inferred:

```
DBX GUI   connect_db                       src-tauri/src/commands/connection.rs:1669
   └─ db_type if is_agent_type(&db_type)                                (:2034)
        └─ connect_agent_pool                                            (:170)
             ├─ agent_manager.spawn(db_type, profile)   ← legacy, unconditional
             │    └─ spawn_connection_client → spawn_first_available_client
             │         → spawn_client_for_key       agent_runtime.rs:300
             │              → AgentDriverClient::spawn(launch)   ← new process, no cache lookup
             └─ AgentMethod::Connect                    ← legacy connect, no agentSessionId
```

`spawn_client_for_key` starts a process **unconditionally**: it consults no cache and never inspects the agent's
`capabilities`. Every connection therefore gets its own `agent.exe`, whatever the handshake advertises.

Verified by swapping `odbc32/agent.exe` for a **probe agent** that advertises `multi_session` and logs every request it
receives. Driven through the real GUI, each connection produced exactly:

```
handshake        {appVersion:"0.6.15", supportedProtocolVersions:[2]}
connect          ← not open_session; no agentSessionId injected
connection_info
```

with **one process per connection and no kill/respawn**. An agent that advertises `multi_session` is treated exactly like
one that does not, because this path never asks. The same held on 2026-09-17 with the real ODBC agent: two `odbc32`
connections → two independent processes.

The **shared-runtime** path — `AgentRuntimeClient::spawn` + `open_session`, keyed by
`agent_key|program|args|working_dir` (`agent_runtime.rs::shared_runtime_key`; **connection id is not in the key**) —
belongs to a *different* entry point, `ConnectionManager::get_or_create_pool_for_session_inner` (`connection.rs:2183`),
which the desktop GUI does not use to establish a connection. That is where "one process per `db_type`" and the
"musical chairs" eviction actually live.

### What `DeclareMultiSession` is actually for

```csharp
const bool DeclareMultiSession = false;   // defensive tripwire, not load-bearing
```

On the desktop path this switch **changes nothing** — the entry point never inspects capabilities. It is held at `false`
as a **tripwire against an upstream change**: if a future DBX routes GUI connections through the shared path, omitting
`multi_session` still yields one process per connection, so "one process, one engine" survives the upgrade. Holding it at
`false` costs **nothing on the desktop path**; it only changes how a shared-path entry point (MCP / Web) would behave,
where it trades memory for isolation.

> **Corrected 2026-09-18.** An earlier revision of this section asserted the opposite — that `false` *causes*
> per-connection processes, at the cost of "2 processes per connection" and a dependence on an upstream legacy branch.
> Those claims came from reading the shared-path code as if it were the desktop path. The probe measurement above
> supersedes them. The "musical chairs" hazard is real, but it lives on the **shared-path** entry point, which the desktop
> app does not use.

The two entry points, side by side:

| | Desktop GUI — `connect_agent_pool` | Shared path — `get_or_create_pool_for_session_inner` |
|---|---|---|
| Agent API | `handshake` → **`connect`** (no session id) | `handshake` → **`open_session`** (+ `agentSessionId`) |
| Processes per connection | **1**, always | 1 per `db_type` — all connections share it |
| Consults `capabilities` | **no** | yes — rejects an agent without `multi_session` |
| Two connections, different ODS | ✅ independent processes | ❌ they evict each other |
| `replace_runtime` recovery | not reachable (a rejection is just a connect failure) | ✅ DBX kills the runtime and respawns |
| Used by | the desktop app | MCP / Web / lazily-created session pools |

The agent also enforces "one engine per process" in code, in `EnginePinning.cs`, as a **second line of defence**. It is no
longer the primary mechanism — one process per connection is — but it is what keeps the design safe if the upstream
process model ever changes underneath us:

| # | Rule | Why |
|---|---|---|
| 1 | The first successful attach **pins** the engine for the lifetime of the process. | The native plugin layer has no fix: Windows resolves `engine1x.dll`'s `fbclient.dll` dependency **by base name**. |
| 2 | A request for a *different* engine **fails loudly**; it never loads a second one. | A second engine silently binds to the first `fbclient.dll` and produces a **mixed-version engine**. Its query results are not trustworthy and the module list does not reveal it. |
| 3 | A failed load marks the engine **unusable** for the whole process. | Closes the "fail → retry → load a different engine" path, which is the most dangerous one. |

The guard is **directly measured** — one process is handed an ODS 10 database and then an ODS 13 database:

```
open_session A (ODS 10, fb25)  -> ok
open_session B (ODS 13, fb50)  -> ERROR ... sessionDisposition = replace_runtime   ✔
list_tables  A (after the rejection) -> ok    (the guard never breaks live sessions)
```

> Scope of that measurement, stated precisely: those sessions were driven **straight against the agent** (via the probe),
> i.e. through `open_session` — the shared-path API. On the desktop path the agent is driven with legacy `connect`, where a
> rejection surfaces to the user as a **connect failure** and the `replace_runtime` respawn is **not** reachable
> (`connect_agent_pool` only knows how to retry Oracle descriptors). That is not a practical gap, because the desktop path
> gives every connection its own process — the guard cannot trip there in the first place. It exists for the case where
> that stops being true.

Reaching that error requires two *different* ODS versions inside one process. One-process-per-connection makes that
impossible in normal use, so the guard only trips if the upstream process model changes underneath us, or if somebody
flips `DeclareMultiSession` back to `true`. That is exactly what a second line of defence is for.

> ⚠️ On the **shared path**, `replace_runtime` makes DBX drop **every connection sharing that runtime**, not just the
> failing one — so a rejected second connection would also disconnect the first. Disconnecting one connection must never
> disturb another. This is the concrete reason to keep this driver off the shared path, and the reason
> `DeclareMultiSession` stays `false`.

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
