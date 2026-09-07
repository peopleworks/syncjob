# PeopleWorks.SyncJob.Core

The data-movement engine behind [SyncJob](https://github.com/peopleworks/SyncJob), packaged so
that the CLI, the Windows service, SqlArchive and the DataSync console share one implementation
instead of each carrying its own.

It moves rows between SQL Server databases the way a production job needs them moved: streamed
rather than held in memory, into a staging table, past a guard that refuses a load that looks
wrong, and published by an operation that takes milliseconds rather than one that holds a schema
lock while a dashboard waits.

```bash
dotnet add package PeopleWorks.SyncJob.Core
```

## What it is for

| | |
|---|---|
| **Streaming copy** | A reader straight into `SqlBulkCopy`, so a table larger than available memory copies without materialising |
| **Staging and guard** | The destination is never truncated on the strength of a query that returned nothing; a row floor, a relative-variation limit and an optional hash decide whether the load is publishable |
| **Publication** | Replace by swap, append, merge by key, deletes applied from a tombstone ledger |
| **Watermark** | Last and previous value per destination table, so a run can be replayed on purpose |
| **A job model** | Sources, ordered steps, field maps with their key roles, SQL variables, per-step statistics |

## What it deliberately is not

The Core knows nothing about where a job is stored. It takes plain objects; the surfaces bring
their own importers — JSON sections for the old CLI, SQLite for the current one, a catalog for the
DataSync console, a manifest for SqlArchive. It also carries no console framework: nothing in here
writes to a terminal.

## Sibling packages

- [`PeopleWorks.SqlSchemaDiff.Core`](https://www.nuget.org/packages/PeopleWorks.SqlSchemaDiff.Core) —
  the schema engine. This package uses it to create a staging table in the shape of its destination.

## Licence

MIT © PeopleWorks
