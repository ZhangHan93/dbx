# ODBC Agent

A generic **ODBC connector** agent for DBX, implemented as a native agent in C#/.NET 8 using `System.Data.Odbc` (P/Invoke to the system ODBC Driver Manager). No JRE, no JDBC bridge.

It implements the DBX agent protocol: **JSON-RPC 2.0 over stdin/stdout** (newline-delimited), printing `{"ready":true}` on startup.

## Why ODBC?

ODBC is the universal access path to databases that DBX does not natively support — Microsoft Access, older SQL Server via legacy drivers, SAP HANA/SQL Anywhere, Progress, FileMaker, Informix, and various domestic (信创) databases that ship an ODBC driver.

## Architecture constraint: bitness

A Windows process can only load an ODBC driver DLL of the **same bitness**:

- 64-bit agent (`win-x64`) → loads 64-bit ODBC drivers
- 32-bit agent (`win-x86`) → loads 32-bit ODBC drivers

DBX itself is 64-bit, but an agent is a separate child process, so it may run as 32-bit to reach 32-bit drivers. This is why we build **two** executables, registered as two connection types: `odbc` (64-bit) and `odbc32` (32-bit).

## Build

Requires the .NET 8 SDK.

```powershell
# 64-bit agent  -> publish/agent.exe
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish
copy publish\dbx-agent-odbc.exe publish\agent.exe

# 32-bit agent  -> publish-x86/agent.exe
dotnet publish -c Release -r win-x86 --self-contained true -p:PublishSingleFile=true -o publish-x86
copy publish-x86\dbx-agent-odbc.exe publish-x86\agent.exe
```

Or run `.\build.ps1` which does both.

> Do **not** use AnyCPU. `System.Data.Odbc` is bitness-bound: an AnyCPU assembly loaded into the 64-bit DBX process would still P/Invoke the 64-bit `odbc32.dll`.

## Offline package layout

```
base/data/agents/drivers/odbc/agent.exe      # 64-bit build
base/data/agents/drivers/odbc32/agent.exe    # 32-bit build
```

DBX resolves an agent by `agentKey` at `data/agents/drivers/<agentKey>/agent.exe`, so `odbc32.yaml`'s `agentKey: odbc32` maps to the 32-bit build.

## Connection input

The connect params accept either:

- `connection_string` — a full ODBC connection string, e.g. `DSN=MyDSN` or `DRIVER={SQL Server};SERVER=localhost;DATABASE=mydb`
- `dsn` + `username` + `password` — resolved through `OdbcConnectionStringBuilder`

The `odbc` connection form in the desktop UI exposes a single "ODBC Connection String" field (plus user/password), matching the JDBC-style workflow.
