# SyncJob

**A production-grade SQL Server data synchronization CLI and Windows Service built on .NET**

SyncJob moves data between SQL Server databases reliably, safely, and fast. It supports full refresh and incremental sync, runs as a CLI command or a Windows Service, and keeps a full audit trail of every execution. Configuration can be stored in simple JSON files or in a persistent SQLite database.

---

## Features

- **Two execution modes** — CLI for on-demand runs and scheduled tasks; Windows Service for agent-based execution driven by a central server
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

## Production Checklist

- [ ] Run `validate` on new configs before the first `run`
- [ ] Set `MinRowThresholdToCommit` to a meaningful value (not 0) as a safety guard
- [ ] Test with `--top 100` on large tables before the first full load
- [ ] Enable `--json-log` and direct `--log-file` to a monitored path
- [ ] Back up the destination before the first full load
- [ ] For incremental sync, initialize tracking first: `--init-tracking`
- [ ] Confirm the destination account has `BULK INSERT` and table-level write permissions

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

*Built with [Spectre.Console](https://spectreconsole.net/) · [Microsoft.Data.SqlClient](https://github.com/dotnet/SqlClient) · .NET 9*
