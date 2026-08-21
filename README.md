<div align="center">

# ⇄ SyncJob

**Move SQL Server data between databases — reliably, safely, and fast.**

[![CI](https://github.com/peopleworks/syncjob/actions/workflows/ci.yml/badge.svg)](https://github.com/peopleworks/syncjob/actions/workflows/ci.yml)
[![Latest release](https://img.shields.io/github/v/release/peopleworks/syncjob?label=release&logo=github)](https://github.com/peopleworks/syncjob/releases/latest)
[![NuGet](https://img.shields.io/nuget/v/PeopleWorks.SyncJob?logo=nuget)](https://www.nuget.org/packages/PeopleWorks.SyncJob)
[![.NET](https://img.shields.io/badge/.NET-9.0-512BD4?style=flat-square&logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![SQL Server](https://img.shields.io/badge/SQL%20Server-2016%2B-CC2927?style=flat-square&logo=microsoftsqlserver&logoColor=white)](https://www.microsoft.com/sql-server)
[![Windows Service](https://img.shields.io/badge/Windows%20Service-ready-0078D4?style=flat-square&logo=windows&logoColor=white)](#windows-service-mode)
[![License](https://img.shields.io/badge/license-MIT-green?style=flat-square)](LICENSE)
[![PeopleWorks](https://img.shields.io/badge/by-PeopleWorks-636f61?style=flat-square)](https://mvp.microsoft.com/en-US/mvp/profile/24060a02-dbc6-44ec-bca5-c213ff9835c5)

</div>

---

SyncJob moves data between SQL Server databases reliably, safely, and fast. It supports **full refresh** and **incremental sync**, runs as a **CLI command** or a **Windows Service**, and keeps a **full audit trail** of every execution. Configuration lives in simple JSON files or in a persistent SQLite database.

It was built for a real problem: getting production data out of a customer's network and into a reporting warehouse, every night, without anyone watching — and without a bad run quietly destroying the destination.

### Why it exists

> Copying a table is easy. Copying it **every night, unattended, without ever leaving the destination in a worse state than before** is not.

That distinction drives every design decision here:

| Concern | How SyncJob handles it |
|---|---|
| 🛡️ **A broken source must not wipe the destination** | `MinRowThresholdToCommit` refuses to commit when the source returns fewer rows than expected |
| ⚡ **Readers must not freeze during a load** | Data lands in a stage table, then tables are **swapped by name** — a metadata operation measured in milliseconds |
| 🔍 **You must be able to prove what happened** | Every run is logged: rows read, inserted, duration, host, error detail |
| 🔐 **Credentials must not sit in plain text** | `secrets protect` encrypts passwords with Windows DPAPI |
| 🧪 **You must be able to rehearse** | `validate` and `--dry-run` check connectivity, schema, and mappings without writing a row |

---

## Features

- **Two execution modes** — CLI for on-demand runs and scheduled tasks; Windows Service for agent-based execution driven by a central server
- **Encrypted credentials** — passwords protected with Windows DPAPI; the key never lives in the file or the binary
- **Non-blocking commit** — full refresh publishes by swapping tables by name, so readers are not locked out during the load
- **Run a whole config in one command** — `run --all` walks every section in file order
- **Full refresh or incremental sync** — sync everything every time, or only the rows that changed since the last run (Timestamp, RowVersion, Change Tracking, CDC)
- **Stage / Final two-phase load** — data lands in a staging table first, then atomically committed to the final table; or direct mode for single-step inserts
- **Parallel bulk load** — configurable `MaxDegreeOfParallelism` for high-throughput scenarios
- **Safety thresholds** — `MinRowThresholdToCommit` prevents accidental commits when the source returns fewer rows than expected
- **Dry-run mode** — validate connectivity, mappings, and query shape without writing a single row
- **Persistent SQLite configuration** — store connections, column mappings, and options in a local SQLite database with encrypted passwords (DPAPI)
- **Full execution history** — every run is logged with row counts, duration, error details, and host machine
- **Central sync hub** — optional SyncJobCentralDB aggregates execution history from multiple machines for centralized monitoring
- **JSON-based config** — simple flat JSON files for scripted and DevOps-friendly deployments
- **Rich CLI** — built on [Spectre.Console](https://spectreconsole.net/) with colors, tables, progress bars, and panels

---

## Requirements

- .NET 9 Runtime (to run) / .NET 9 SDK (to build)
- SQL Server 2016 or later (source and/or destination)
- Windows (for Windows Service mode and DPAPI password encryption)

---

## Install

**Download the binary** — a single self-contained executable, no .NET runtime to install:

> [**⬇ Latest release**](https://github.com/peopleworks/syncjob/releases/latest) → `syncjob-win-x64.zip`

```powershell
.\SyncJob.exe --version
```

**Or install it as a .NET tool:**

```bash
dotnet tool install -g PeopleWorks.SyncJob
syncjob --version
```

**Or build from source:**

```bash
git clone https://github.com/peopleworks/syncjob.git
cd syncjob
dotnet publish SyncJob.csproj -c Release -r win-x64 --self-contained true   -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o ./publish
```

> `IncludeNativeLibrariesForSelfExtract` is not optional. Without it the single file leaves
> out `Microsoft.Data.SqlClient.SNI.dll` and `e_sqlite3.dll`, and the executable throws
> `DllNotFoundException` the moment it opens a connection.

---

## Quick Start

### 1. Build

```bash
git clone https://github.com/peopleworks/syncjob.git
cd SyncJob
dotnet publish -c Release -o ./publish
```

### 2. Create a config file

```json
{
  "SalesSync": {
    "Source": {
      "ConnectionString": "Server=SOURCE;Database=SourceDB;User Id=sa;Password=...;Encrypt=True;TrustServerCertificate=True;",
      "Query": "SELECT Id, Name, Amount, UpdatedAt FROM dbo.Sales"
    },
    "Destination": {
      "ConnectionString": "Server=DEST;Database=DestDB;User Id=sa;Password=...;Encrypt=True;TrustServerCertificate=True;",
      "StageTable": "dbo.Sales_Stage",
      "FinalTable": "dbo.Sales"
    },
    "ColumnMappings": [
      { "Source": "Id",        "Dest": "Id"        },
      { "Source": "Name",      "Dest": "Name"      },
      { "Source": "Amount",    "Dest": "Amount"    },
      { "Source": "UpdatedAt", "Dest": "UpdatedAt" }
    ],
    "Options": {
      "BatchSize": 10000,
      "MaxDegreeOfParallelism": 4,
      "BulkCopyTimeoutSeconds": 0,
      "KeepIdentity": true,
      "MinRowThresholdToCommit": 1000
    }
  }
}
```

### 3. Validate and run

```bash
# Validate first (no data is written)
SyncJob.exe validate -c appsettings.json -s SalesSync

# Execute
SyncJob.exe run -c appsettings.json -s SalesSync --direct
```

---

## CLI Reference

```
SyncJob.exe --version
SyncJob.exe --help
```

### Legacy commands (JSON-based)

Ideal for scripted deployments and Windows Task Scheduler. No database setup required.

| Command | Description |
|---------|-------------|
| `run` | Execute a sync job |
| `validate` | Validate config, connectivity, and query shape |
| `config-init` | Generate a JSON config from a list of field names |
| `examples` | Show usage examples |

### Modern commands (SQLite-based)

Store configuration persistently in a local SQLite database with encrypted passwords.

| Command | Description |
|---------|-------------|
| `connection add\|list\|test\|delete` | Manage SQL Server connections |
| `config create\|list\|show\|delete` | Manage sync configurations |
| `mapping add\|list\|remove\|clear` | Manage column mappings per config |
| `history list\|show\|stats\|clear` | Browse execution history |
| `db info\|backup\|restore\|cleanup\|vacuum` | Manage local SQLite database |
| `central setup\|test\|status\|enable\|disable\|reset` | Central sync hub management |
| `run-db <CONFIG_ID>` | Execute from SQLite config |

---

## `run` Options

```bash
SyncJob.exe run -c <path> -s <section> [options]
```

| Option | Description |
|--------|-------------|
| `-c, --config <PATH>` | JSON config file (default: `appsettings.json`) |
| `-s, --section <NAME>` | Section name inside the JSON |
| `--direct` | Write directly to Final table, skip Stage |
| `--append` | Do not truncate Final table before loading |
| `--dry-run` | Validate only, do not write any data |
| `--full-refresh` | Ignore incremental tracking, sync everything |
| `--init-tracking` | Initialize the incremental tracking table |
| `--top <N>` | Read only N rows from source (testing) |
| `--batch-size <N>` | Override BatchSize from config |
| `--maxdop <N>` | Override MaxDegreeOfParallelism |
| `--min-commit <N>` | Override MinRowThresholdToCommit |
| `--force-commit` | Commit even if rows < threshold |
| `--skip-commit` | Load Stage but skip commit to Final |
| `--log-level <LEVEL>` | Trace / Debug / Info / Warn / Error / Fatal |
| `--log-file <PATH>` | Write logs to this file |
| `--log-dir <PATH>` | Log directory (auto-named daily file) |
| `--json-log` | Write logs in JSONL format |
| `--quiet` | Suppress console output |

### Running every section at once

A config file usually holds several syncs. Chaining them by hand in a script is exactly where one gets forgotten and nobody notices.

```bash
# Every section, in file order, stopping at the first failure
SyncJob.exe run -c appsettings.json --all

# Complete the run and report which sections failed at the end
SyncJob.exe run -c appsettings.json --all --continue-on-error
```

A section counts as syncable when it has both `Source` and `Destination`, so unrelated blocks (`ConnectionStrings`, `Logging`, `AIProxySettings`) are skipped automatically.

Stopping at the first failure is the default on purpose: if the fact table did not load, continuing to load its dimensions leaves the warehouse **internally inconsistent**, which is harder to detect than a clean stop.

---

### Typical Windows Task Scheduler command

```
SyncJob.exe run -c C:\SyncJob\appsettings.json -s SalesSync --direct --min-commit 0 --log-file C:\Logs\sync.sales.log --log-level Info --json-log --quiet
```

---

## Stage / Final Load Pattern

```
Source DB
    │
    ▼
Stage Table  ←── truncate + bulk insert (safe to fail here)
    │
    ▼
Final Table  ←── atomic swap (TRUNCATE + INSERT or Stored Procedure)
```

If the bulk load to Stage fails partway through, the Final table is never touched. Production reads always see a consistent snapshot.

Use `--direct` to skip Stage and write straight to Final. Faster, but without the atomic safety net.

---

### How the swap works

For a full refresh, SyncJob does **not** truncate the final table and copy rows into it. It swaps the two tables **by name**:

```
Final    →  Final_swap_a1b2c3d4
Stage    →  Final                  ← the new data, already written, now published
temporal →  Stage                  ← the old data, discarded on the next run
```

The data was written to `Stage` before the transaction opened, so the swap itself is a metadata operation.

**Why it matters:** `TRUNCATE` takes a schema-modification lock held until commit, and **every reader blocks against it — even one using `NOLOCK`**. On a 122,000-row table that measured **~15 seconds of blocked dashboards**. The rename swap measured **~370 ms**.

> #### ⚠️ Indexes travel with the physical table, not the name
>
> `sp_rename` changes which table answers to which name. Indexes, constraints and table-level permissions **follow the physical table**.
>
> If you add an index to `Final` only, after the next swap that index lives on `Stage`. **Create indexes on both tables.** Grant permissions at schema or role level rather than per table.

SyncJob falls back to `TRUNCATE` + `INSERT` — with identical results, just slower — when the swap does not apply:

- `Final` is a **view**, not a table
- `Final` and `Stage` are in **different schemas** (`sp_rename` cannot move objects across schemas)
- `--append` mode, where existing rows must be preserved

The log records which path ran: `dest.swap.rename` or `dest.swap.truncate`.

---

## Incremental Sync

Enable incremental mode in your JSON config:

```json
"Incremental": {
  "Enabled": true,
  "Mode": "RowVersion",
  "TrackingColumn": "RowVer",
  "MergeStrategy": "Upsert",
  "PrimaryKeyColumns": ["Id"],
  "DeleteDetection": "SoftDelete",
  "SoftDeleteColumn": "IsDeleted",
  "SoftDeleteValue": "1"
}
```

### Tracking modes

| Mode | How it works | Best for |
|------|-------------|----------|
| `Timestamp` | `WHERE UpdatedAt > @LastSync` | Simple tables with a reliable datetime column |
| `RowVersion` | `WHERE RowVer > 0x{last}` | Production — monotonic, no clock skew |
| `ChangeTracking` | SQL Server native Change Tracking | When you can enable it on the source DB |
| `ChangeDataCapture` | SQL Server CDC | Full audit trail including old values |

### Merge strategies

| Strategy | Behavior |
|----------|----------|
| `Insert` | New rows only |
| `Upsert` | Insert new + update existing (by primary key) |
| `Full` | Insert + update + delete |

### Delete detection

| Mode | Behavior |
|------|----------|
| `SoftDelete` | Flag column equals value (e.g. `IsDeleted = 1`) |
| `AutoDetect` | Uses Change Tracking / CDC events |
| `Comparison` | PK comparison between source and destination |

SyncJob creates and maintains `dbo.SyncJobTracking` in the destination database:

```
JobIdentifier | LastSyncTime | LastRowVersion | RowsInserted | RowsUpdated | RowsDeleted | Success
```

First run: full refresh and saves state. Subsequent runs: reads last state, builds filtered query, syncs only changes.

---

## SQLite-Based Workflow

```bash
# Add connections (passwords encrypted with DPAPI)
SyncJob.exe connection add --name source --server SQL01 --database SourceDB --user sa --password "secret"
SyncJob.exe connection add --name dest   --server SQL02 --database DestDB   --user sa --password "secret"

# Create a config
SyncJob.exe config create --name "Sales Sync" \
  --source-conn source --dest-conn dest \
  --source-query "SELECT Id, Name, Amount FROM dbo.Sales" \
  --stage-table dbo.Sales_Stage --final-table dbo.Sales

# Add column mappings
SyncJob.exe mapping add <config-id> --source Id     --dest Id     --primary-key
SyncJob.exe mapping add <config-id> --source Name   --dest Name
SyncJob.exe mapping add <config-id> --source Amount --dest Amount

# Execute
SyncJob.exe run-db <config-id> --direct

# Review history
SyncJob.exe history stats
```

---

## 🔐 Securing Credentials

Connection strings live in a file on a server. `secrets` encrypts the passwords with **Windows DPAPI** — the key is managed by the operating system, never stored in the file or the binary.

```bash
# See what is exposed
SyncJob.exe secrets status -c appsettings.json

# Encrypt every password in the file
SyncJob.exe secrets protect -c appsettings.json
```

Only the password is encrypted. Server, database and user stay readable, because during an incident you need to see where a job points without decrypting anything — and a diff of the file has to stay useful.

```jsonc
{
  "Source": {
    "ConnectionString": "Server=SRV01;Database=Sales;User Id=etl;Password=enc:u:AQAAANCMnd8BFdER...;"
  }
}
```

Encrypted values carry their own scope marker, so decryption never has to guess:

| Marker | Scope | Who can decrypt |
|---|---|---|
| `enc:u:` | `CurrentUser` | Only the account that encrypted it, on that machine |
| `enc:m:` | `LocalMachine` | Any account on that machine |
| *(none)* | plain text | Anyone — still works, for backward compatibility |

> ### ⚠️ Choosing a scope is not cosmetic
>
> If you encrypt from an interactive session with the default `user` scope and the **Windows Service runs under a different account, the service cannot read the file.**
>
> For services, either encrypt with `--scope machine`, or encrypt while signed in as the service account.

```bash
SyncJob.exe secrets protect -c appsettings.json --scope machine
```

`protect` writes a `.plain.bak` copy the first time it runs. **That backup holds the passwords in clear text — move it somewhere safe and delete it from the server.** Re-running `protect` never overwrites that backup, so the original is not lost.

**What DPAPI protects:** copying the file to another machine is useless — the key does not travel with it.
**What it does not protect:** anyone already executing code as the same user, on the same machine, can read the secret. That is the boundary of the mechanism, and it is worth knowing rather than assuming.

---

## Execution History

Every run is stored automatically:

```bash
SyncJob.exe history list                    # Recent executions
SyncJob.exe history stats                   # Aggregate per config
SyncJob.exe history show <execution-id>     # Full detail
SyncJob.exe history clear --days 90         # Remove records older than 90 days
```

Each record: start/end time, duration, rows read/inserted/updated/deleted/failed, error details, host machine, log file path.

---

## Central Sync Hub

Aggregate execution history from multiple machines into one SQL Server database:

```
Machine A ──┐
Machine B ──┼──► SyncJobCentralDB (SQL Server)
Machine C ──┘
```

Setup on each client:

```bash
SyncJob.exe central setup     # Interactive wizard
SyncJob.exe central test      # Verify connection
SyncJob.exe central enable    # Auto-push after every run
SyncJob.exe central status    # Show current config
```

After `central enable`, every successful `run-db` automatically pushes its execution record to `ExecutionHistory_Central`. If the push fails, the sync still succeeds — central sync is fire-and-forget.

In Windows Service mode, the central server can also dispatch sync tasks to connected agents via the `SyncTasks` table. Agents poll every 30 seconds.

---

## Windows Service Mode

The same binary runs in two modes:

```
SyncJob.exe                   →  Windows Service (agent mode)
SyncJob.exe run ...           →  CLI mode
SyncJob.exe run-db ...        →  CLI mode
```

Install as a Windows Service:

```powershell
New-Service -Name "PeopleWorks SyncJob" `
            -BinaryPathName "C:\SyncJob\SyncJob.exe" `
            -StartupType Automatic `
            -DisplayName "PeopleWorks SyncJob Service"

Start-Service "PeopleWorks SyncJob"
```

The service registers with Windows Event Log under source name `"PeopleWorks SyncJob"` and uses a 30-second polling loop to pick up tasks from the central database.

---

## Generating a Config from Field Names

```bash
# fields.txt — one column name per line
SyncJob.exe config-init \
  -f fields.txt \
  -o appsettings.json \
  -s SalesSync \
  --stage dbo.Sales_Stage \
  --final dbo.Sales \
  --batch-size 10000 \
  --maxdop 4 \
  --min-commit 1000
```

---

## Database Management

```bash
SyncJob.exe db info              # Path, size, schema version, record counts
SyncJob.exe db backup            # Timestamped backup copy
SyncJob.exe db restore <path>    # Restore from backup
SyncJob.exe db cleanup --days 90 # Remove old history
SyncJob.exe db vacuum            # Compact SQLite file
```

---

## Logging

```bash
# JSONL to file, quiet console — ideal for scheduled tasks
SyncJob.exe run -c config.json -s Section \
  --json-log \
  --log-file C:\Logs\sync.log \
  --quiet

# Debug level with auto-dated file in a directory
SyncJob.exe run -c config.json -s Section \
  --log-level Debug \
  --log-dir C:\Logs
```

JSONL output is compatible with log aggregators like Loki, Splunk, and Azure Monitor.

---

## Full JSON Config Reference

```json
{
  "SectionName": {
    "Source": {
      "ConnectionString": "Server=...;Database=...;User Id=...;Password=...;Encrypt=True;TrustServerCertificate=True;",
      "Query": "SELECT col1, col2 FROM dbo.SourceView",
      "StoredProcedure": null,
      "Parameters": {}
    },
    "Destination": {
      "ConnectionString": "Server=...;Database=...;User Id=...;Password=...;Encrypt=True;TrustServerCertificate=True;",
      "StageTable": "dbo.TableName_Stage",
      "FinalTable": "dbo.TableName"
    },
    "ColumnMappings": [
      { "Source": "SourceCol", "Dest": "DestCol" }
    ],
    "Options": {
      "BatchSize": 10000,
      "MaxDegreeOfParallelism": 4,
      "BulkCopyTimeoutSeconds": 0,
      "KeepIdentity": true,
      "MinRowThresholdToCommit": 1000
    },
    "Incremental": {
      "Enabled": false,
      "Mode": "Timestamp",
      "TrackingColumn": "UpdatedAt",
      "MergeStrategy": "Upsert",
      "PrimaryKeyColumns": ["Id"],
      "DeleteDetection": "None",
      "SoftDeleteColumn": null,
      "SoftDeleteValue": null
    }
  }
}
```

Multiple sections in one file are supported. Use `-s SectionName` to select which one to run.

---

## Column Mappings

Mappings are explicit by default, and **required** when source and destination names differ or when you want to move a subset of columns.

```jsonc
"ColumnMappings": [
  { "Source": "Id",        "Dest": "CustomerId" },
  { "Source": "FullName",  "Dest": "Name"       }
]
```

When the names match one-to-one, **leave the list out and SyncJob derives it from the source**:

```jsonc
"ColumnMappings": []      // or omit the property entirely
```

It reads the result metadata with `CommandBehavior.SchemaOnly`, so SQL Server returns the column list **without executing the query** — free even when the source holds millions of rows.

Hand-writing thirty column names is exactly where a typo hides until the data comes out shifted by one.

---

## 🔀 Companion tool — SQLDiff

SyncJob moves **data**. Its sibling, [**SQLDiff**](https://github.com/peopleworks/SqlSchemaDiff), moves **structure**.

| | [SQLDiff](https://github.com/peopleworks/SqlSchemaDiff) | SyncJob |
|---|---|---|
| Moves | Schema — DDL | Data — DML |
| Answers | *"Do these two databases have the same shape?"* | *"Does the destination have the same rows?"* |
| Output | A T-SQL migration script you read before running | Rows in a table, with an audit trail |

They are not just related by topic — **they hand work to each other**:

### 1. Standing up a destination

Before SyncJob can move a row, the destination tables have to exist with the right shape.
Instead of hand-writing DDL, extract it from the source and apply it:

```bash
SQLDiff.exe extract --conn "Server=SRC;Database=Prod;..."  --out src.sql --json src.json
SQLDiff.exe deploy  --source src.json --target "Server=DW;Database=Reporting;..."
```

### 2. Keeping stage and final identical

SyncJob's full refresh publishes by [swapping tables by name](#how-the-swap-works), which
requires `Stage` and `Final` to have **the same columns in the same order**. Drift between
them is exactly the failure SQLDiff is built to catch:

```bash
SQLDiff.exe drift --source "...Database=DW;" --target "...Database=DW;"                   --include Sales,Sales_Stage
```

### 3. Catching schema drift before the nightly run

When a column is added to a source view, SyncJob's automatic mappings pick it up — and the
bulk copy then fails because the destination table does not have it. `drift` exits with
code `2` when the databases diverge, so a scheduled job can check first and stop early:

```bash
SQLDiff.exe drift --source "..." --target "..." || exit 1
SyncJob.exe  run   -c appsettings.json --all
```

> **Structure first, then data.** A sync into a destination whose shape has drifted either
> fails loudly or, worse, succeeds into the wrong columns. Checking the shape costs seconds.

---

## Production Checklist

- [ ] Run `validate` on new configs before the first `run`
- [ ] **Set `MinRowThresholdToCommit` to a meaningful value (not 0)** — this is the guard that stops a broken source from wiping the destination
- [ ] **Run `secrets protect`** so no password sits in clear text on the server
- [ ] **Move the `.plain.bak` off the server** after encrypting
- [ ] If a Windows Service will read the config, encrypt with `--scope machine` *or* from the service account
- [ ] Test with `--top 100` on large tables before the first full load
- [ ] Enable `--json-log` and direct `--log-file` to a monitored path
- [ ] Back up the destination before the first full load
- [ ] For incremental sync, initialize tracking first: `--init-tracking`
- [ ] Confirm the destination account has `BULK INSERT` and table-level write permissions
- [ ] If you added indexes to a final table, **create them on the stage table too** (see [How the swap works](#how-the-swap-works))

---

## What's New in 2.3.0

| | Change |
|---|---|
| 🔴 | **`MinRowThresholdToCommit` now applies to the `run-db` path too.** It was implemented only in the JSON path — the Windows Service route went straight to commit without evaluating it. A source returning 3 rows instead of 5,435 would truncate the destination and leave 3. Root cause: the load/commit code was duplicated across two files and the copies had drifted. The duplication is gone; both paths share one implementation. |
| 🔴 | **Full refresh swaps tables by name instead of `TRUNCATE`.** Readers no longer block for the duration of the load. Measured: **~15 s → ~370 ms** on 122,590 rows. |
| 🟠 | **Explicit column list** in the stage→final insert instead of `SELECT *`. If the two tables ever diverge, it now fails loudly rather than shifting data silently. |
| 🟡 | **`secrets protect` / `secrets status`** — DPAPI encryption for the passwords in `appsettings.json`. |
| 🟡 | **`run --all`** — run every section of a config file in one command. |
| 🟢 | **Automatic column mappings** when source and destination names match. |

---

## Version

```bash
SyncJob.exe --version
```

Version is embedded at build time via the `.csproj` — update `<Version>` in one place and it propagates everywhere.

---

## License

MIT — see [LICENSE](LICENSE) for details.

---

## Contributing

Pull requests are welcome. For major changes, open an issue first to discuss the approach.

1. Fork the repository
2. Create a feature branch: `git checkout -b feature/my-feature`
3. Commit your changes
4. Open a Pull Request

---

<div align="center">

### Built by PeopleWorks

Created by **Pedro Hernández — PeopleWorks**,
[Microsoft MVP for .NET](https://mvp.microsoft.com/en-US/mvp/profile/24060a02-dbc6-44ec-bca5-c213ff9835c5)

Built with [.NET 9](https://dotnet.microsoft.com/) · [Spectre.Console](https://spectreconsole.net/) · [Microsoft.Data.SqlClient](https://github.com/dotnet/SqlClient)

**PeopleWorks SQL tools** — [SQLDiff](https://github.com/peopleworks/SqlSchemaDiff) moves the schema · **SyncJob** moves the data

**Every feature in this tool came from running it in production, not from a whiteboard.**

MIT licensed — use it, fork it, ship it.

© 2026 PeopleWorks

</div>
