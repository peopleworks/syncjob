# Changelog

All notable changes to SyncJob are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project uses
[semantic versioning](https://semver.org/spec/v2.0.0.html).

The CLI (`PeopleWorks.SyncJob.Cli`) and the engine (`PeopleWorks.SyncJob.Core`) carry
separate version numbers on purpose: the engine is referenced by other tools and moves
at its own pace. A tag names the CLI's version.

---

## [3.0.0] — 2026-09-07

The engine comes out of the CLI and becomes a package. `PeopleWorks.SyncJob.Core` 1.0.0
is published alongside this release, and SqlArchive will be built on it.

**Read the three migration notes below before upgrading.** Each is a change of
behaviour, not of API, and each has a straightforward answer.

### Migration

#### 1. The watermark moved

Incremental state was kept in `dbo.SyncJobTracking`, one row per job, because the engine
had no steps. It has steps now, so the mark is kept per step and the table is
`dbo.SyncJobWatermark`:

```
JobId | StepId | Value | PreviousValue | UpdatedAt
```

**Your old table is not touched.** It stays exactly where it is, with the history of the
runs before the upgrade, and the import says so. What changes is that the first run
after upgrading starts from `InitialValue` — for `Timestamp`, 1900-01-01 — and therefore
reads everything once.

If that is not acceptable on a large table, carry the value across by hand first:

```sql
INSERT INTO dbo.SyncJobWatermark (JobId, StepId, [Value], PreviousValue, UpdatedAt)
SELECT JobIdentifier, JobIdentifier,
       CONVERT(nvarchar(400), LastSyncTime, 126), NULL, SYSDATETIMEOFFSET()
FROM dbo.SyncJobTracking;
```

`StepId` is the section's name; with `JobIdentifier` set, the two match.

Pointing `TrackingTable` at the old table does **not** work, and does not fail
mysteriously either: the engine checks the shape before reading it and refuses with a
message naming the table, what is missing, and what to do instead.

#### 2. `--direct` no longer skips staging

It used to write the destination without a stage table. Every load stages now, and the
flag reports that it did nothing rather than being silently ignored.

This is not overhead. With nothing staged there is nothing for the row guard to compare
against, so a source that comes back empty is only discovered **after** the destination
has been emptied. The rows end up in the same table either way; only the order changed.

#### 3. Connection passwords are re-protected

Passwords in the SQLite catalog were stored XOR'd against a key that was a literal in the
source. They are now protected with DPAPI, in **machine** scope — the person who types a
password is at a console and the service that reads it at night runs under another
account.

Existing rows are still readable, so nothing stops working. They stay in the old form
until re-saved, so re-enter the ones that matter:

```bash
syncjob connection update <name> --password
```

If a SQL-auth connection has never worked for you, this release is why it will now: the
write was XOR and every read was DPAPI, so those two paths could never agree. Only
integrated-security connections worked, and those store no password at all.

### Fixed — silent wrong answers

Every one of these produced a wrong result and a green log.

- **The Windows service could empty a production table and report success.** It published
  with `TRUNCATE TABLE final; INSERT INTO final SELECT * FROM stage;` and its only check
  was `stageCount != rowCount` — which asks whether the bulk copy lost rows, not whether
  the load is plausible. A source returning nothing gives `0 == 0`, passes, and truncates.
  It now goes through the same guard as every other path.
- **The service published without a column list.** The day the stage and destination
  tables stopped agreeing, the rows went in shifted, silently, for as long as the types
  lined up. 2.3.0's notes said this duplication was gone; there were three copies of the
  pipeline and only one had been fixed. All three are gone now.
- **`"Mode": "Timestamp"` could not be loaded at all.** The config reader registered no
  string-enum converter, so the form documented in `INCREMENTAL_SYNC.md` threw before
  anything was validated.
- **`run --all` dropped seven flags.** It rebuilt its settings field by field, and the
  copy omitted `--min-commit`, `--top`, `--batch-size`, `--maxdop`, `--sp`, `--probe-top`
  and the certificate options.
- **`--init-tracking` could start a full production load.** It was honoured only when
  `Incremental.Enabled` was true; on any other section it was ignored and the command
  carried straight on into the load.
- **The service dropped a procedure's parameters.** `SourceParameters` was never parsed
  (`// TODO: Parse from config.SourceParameters`), so a procedure configured with
  parameters was called with none.
- **Row counts were the source's count, not the publisher's.** `RowsInserted` was set to
  the number of rows read, with a `// TODO: Obtener metrics reales del MERGE` beside it.
  The numbers now come from the work.
- **A tagged release published nothing.** The NuGet push was guarded by
  `if: env.NUGET_API_KEY != ''` and skipped in silence, so the workflow went green while
  the README told people to install a package that did not exist.

### Added

- **`PeopleWorks.SyncJob.Core`** — the engine as a package: streaming copy, staging from
  the destination's own catalog, the row guard, publication by swap, append, merge,
  watermarks, tombstones and a job lease. Referenced rather than copied, so the CLI, the
  service and SqlArchive share one implementation.
- **Streaming copy.** The three old paths read the whole source into memory before
  writing a row, which is why no batch size made a table larger than RAM copyable.
  Measured on one million rows: **0.0 MB** of live heap against 182.7 MB for the list it
  replaces and 497.2 MB once it reached a `DataTable` — and faster, 3.9 s against 5.0 s.
- **Publication by `ALTER TABLE ... SWITCH`.** The destination keeps its `object_id`, so
  indexes and GRANTs are never lost. Measured: a reader blocked **31 ms** against
  **1,137 ms** for truncate-and-insert.
- **A guard that can refuse a load proportionally**, not just against a fixed floor. A
  table that grew to four million rows and stages nine hundred passes any floor set when
  it had a thousand.
- **A job lease** replacing the "in progress" status flag, which had no owner, no expiry
  and no way back: a run killed mid-flight left it set for ever, and because the check
  was an `AND` of two conditions, a job whose flag was stuck while no step was flagged
  started anyway. A lease says who holds it and until when, so a dead holder's claim
  lapses on its own.
- **Tombstone deletions that actually apply.** The deployed implementation split the key
  with `PARSENAME`, which counts from the right and gives up past four parts: a
  three-part key matched nothing, deleted nothing and reported nothing.
- **`${Watermark}`** — the engine supplies the boundary to SQL you wrote, so an
  incremental step no longer needs a hand-written variable that queries the state table.
- **A dry run that means something.** It stages the real source and runs the guard's
  arithmetic against real counts, then stops: *"1,240 rows were staged and the guard
  would have allowed it. Nothing was written."*
- **Tests.** This repository had none. It has **245 unit and 122 live**, the live ones
  against a real SQL Server in CI.

### Changed

- The repository is a four-project solution: `src/SyncJob.Core`, `src/SyncJob.Cli`,
  `tests/SyncJob.Core.Tests`, `tests/SyncJob.IntegrationTests`.
- `dbo.SyncJobWatermark` keeps the previous value beside the current one, because what an
  operator does when a load goes wrong is re-run from where it was before, and without it
  that means guessing.

### Known limits

- A step with an explicitly named staging table keeps that table after a run. Only
  engine-generated `_stg_<hex>` tables are dropped.
- `MaxDegreeOfParallelism` is carried through the model but not honoured: the engine
  copies a step in a single stream, which is what lets it copy a table larger than
  memory. The validator warns rather than leaving it silent.
- The SQLite catalog has no `Replace` strategy, so a `Full` configuration imports as a
  merge and stops deleting rows the source dropped. Reported as a loss on every import.

---

## [2.3.0] — 2026-08-21

- `MinRowThresholdToCommit` applied to the `run-db` path, which had gone straight to
  commit without evaluating it.
- Full refresh swapped tables by name instead of `TRUNCATE`: **~15 s → ~370 ms** on
  122,590 rows.
- An explicit column list in the stage→final insert instead of `SELECT *`.
- `secrets protect` / `secrets status` — DPAPI for the passwords in `appsettings.json`.
- `run --all` — every section of a config file in one command.
- Automatic column mappings when source and destination names match.
