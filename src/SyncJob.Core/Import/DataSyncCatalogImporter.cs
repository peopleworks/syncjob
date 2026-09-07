using System.Globalization;
using SyncJob.Core.Model;

namespace SyncJob.Core.Import;

/// <summary>
/// Reads the job catalog of DataSync, the DevExpress XAF console this engine also
/// replaces: <c>SyncProcess</c>, <c>SyncProcessSource</c>, <c>SyncStep</c>,
/// <c>SyncStepFieldMap</c>, <c>SyncStepVariable</c> and <c>RemoteServerConfig</c>.
/// <para>
/// Like the SQLite importer it takes rows, not a connection. The catalog lives in SQL
/// Server, so the Core could open it - and that is exactly why it does not: an importer
/// that reads its own source decides on its own when to read it, and four consumers with
/// four different ideas of when configuration is loaded would each get a surprise.
/// </para>
/// </summary>
public sealed class DataSyncCatalogImporter : IJobImporter
{
    private readonly DataSyncCatalogRows _rows;

    public DataSyncCatalogImporter(DataSyncCatalogRows rows, string sourceDescription = "DataSync catalog")
    {
        ArgumentNullException.ThrowIfNull(rows);

        _rows = rows;
        SourceDescription = sourceDescription;
    }

    public string SourceDescription { get; }

    public Task<ImportResult> ImportAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var result = new ImportResult();

        // XPO deletes by writing GCRecord rather than by removing the row, and the
        // deployed procedure filters on it everywhere. An importer that forgot would
        // resurrect every job anyone ever deleted.
        var deleted = _rows.Processes.Count(x => x.GCRecord is not null);
        if(deleted > 0)
        {
            result.Losses.Add(
                $"{deleted} process(es) in the catalog are soft-deleted - GCRecord is not null - and were skipped. " +
                "XPO deletes by marking rather than removing, so they are still in the table and still visible to " +
                "anything that forgets to filter.");
        }

        foreach(var process in _rows.Processes.Where(x => x.GCRecord is null))
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.Jobs.Add(ReadJob(process, result.Losses));
        }

        return Task.FromResult(result);
    }

    private SyncJobDefinition ReadJob(DataSyncProcessRow process, List<string> losses)
    {
        // DataSync keys a process by its Description - the procedure looks it up by name
        // and its own history table keys by SyncProcessID. Identity here follows the
        // surrogate, so renaming the process no longer orphans its watermarks, and the
        // description is kept because it is what the deployed procedure is called with.
        var id = "datasync-process-" + process.SyncProcessID.ToString(CultureInfo.InvariantCulture);
        var name = string.IsNullOrWhiteSpace(process.Description) ? id : process.Description!.Trim();

        var job = new SyncJobDefinition
        {
            Id = id,
            Name = name,
            IsActive = process.IsActive,
            Extensions =
            {
                ["datasync.syncProcessId"] = process.SyncProcessID.ToString(CultureInfo.InvariantCulture),
                ["datasync.description"] = name,
                ["datasync.lastStatus"] = process.LastStatus.ToString(CultureInfo.InvariantCulture)
            }
        };

        var endpoints = new Dictionary<int, Endpoint>();

        if(process.TargetServerID is { } targetId)
        {
            job.Destination = Endpoint(targetId, id, endpoints, losses);
        }
        else
        {
            losses.Add(
                $"The process '{name}' names no TargetServer, so the job has no destination and will not validate.");
        }

        // SyncProcessSource is a scoping list: it decides what the XAF editor offers as a
        // step's source server and nothing else. The deployed procedure never reads it.
        foreach(var source in _rows.ProcessSources
                    .Where(x => x.GCRecord is null && x.SyncProcessID == process.SyncProcessID))
        {
            var endpoint = Endpoint(source.RemoteServerConfigID, id, endpoints, losses);
            endpoint.Extensions["datasync.isCreatedLinkedServer"] =
                source.IsCreatedLinkedServer ? "true" : "false";

            if(job.Sources.All(x => !string.Equals(x.Id, endpoint.Id, StringComparison.Ordinal)))
                job.Sources.Add(endpoint);
        }

        var steps = _rows.Steps
            .Where(x => x.GCRecord is null && x.SyncProcessID == process.SyncProcessID)
            .OrderBy(x => x.OrderNumber)
            .ToList();

        foreach(var row in steps)
            job.Steps.Add(ReadStep(row, job, id, name, endpoints, losses));

        if(job.Steps.Count > 0)
        {
            // DataSync counts the rows it moved and prints them. Nothing in the catalog
            // says how few is too few, so there is no guard to carry and the imported
            // steps publish with none.
            losses.Add(
                $"The process '{name}' has no minimum-row guard to import, because DataSync has no field for one - " +
                "it counts the rows it moved and only prints the number. Set a floor on each step, or a source that " +
                "comes back empty will be published as an empty load.");
        }

        return job;
    }

    private Endpoint Endpoint(int serverId, string jobId, Dictionary<int, Endpoint> endpoints, List<string> losses)
    {
        if(endpoints.TryGetValue(serverId, out var existing))
            return existing;

        var endpointId = "server-" + serverId.ToString(CultureInfo.InvariantCulture);
        var row = _rows.Servers.FirstOrDefault(x => x.ServerID == serverId && x.GCRecord is null);

        if(row is null)
        {
            losses.Add(
                $"The catalog refers to RemoteServerConfig {serverId}, and no such row was handed to the importer " +
                "(or it is soft-deleted). The endpoint is imported without a connection string.");

            var orphan = new Endpoint { Id = endpointId, Name = endpointId };
            endpoints[serverId] = orphan;

            return orphan;
        }

        var endpoint = new Endpoint
        {
            Id = endpointId,
            Name = string.IsNullOrWhiteSpace(row.ServerName) ? endpointId : row.ServerName.Trim(),
            IsActive = row.IsActive,
            ConnectionString = EndpointSecrets.Compose(
                row.DataSource, row.InitialCatalog, row.UserID, trustServerCertificate: false, encrypt: true),
            SpeedProbeTable = Blank(row.TableForSpeedTest)
        };

        // Not optional and not negotiable: RemoteServerConfig.UserPassword is a plain
        // nvarchar column, and the value in it is handed to dbo.ManageLinkedServer on
        // every save. It does not travel into the model at any strength, under any key.
        if(!string.IsNullOrEmpty(row.UserPassword))
        {
            endpoint.SecretRef = EndpointSecrets.Reference(jobId, endpointId);

            losses.Add(EndpointSecrets.CredentialLoss(
                $"The DataSync server '{endpoint.Name}' stores its password in clear text in " +
                "RemoteServerConfig.UserPassword, and this endpoint",
                endpoint.SecretRef));
        }
        else if(!string.IsNullOrWhiteSpace(row.UserID))
        {
            endpoint.SecretRef = EndpointSecrets.Reference(jobId, endpointId);

            losses.Add(
                $"The DataSync server '{endpoint.Name}' names the login '{row.UserID}' and has no password stored " +
                $"beside it, so the secret named '{endpoint.SecretRef}' has to be supplied before it can connect.");
        }

        var linkedServer = row.ServerName + "_LinkedServer";
        endpoint.Extensions["datasync.linkedServerName"] = linkedServer;
        endpoint.Extensions["datasync.serverId"] = serverId.ToString(CultureInfo.InvariantCulture);

        if(!string.IsNullOrWhiteSpace(row.LinkedServerName) &&
           !string.Equals(row.LinkedServerName, linkedServer, StringComparison.OrdinalIgnoreCase))
        {
            // The XAF property ignores its own stored column and always returns
            // ServerName + "_LinkedServer", so a stored value that says otherwise is
            // stale and the step SQL may name either.
            endpoint.Extensions["datasync.linkedServerName.stored"] = row.LinkedServerName!;

            losses.Add(
                $"The server '{endpoint.Name}' has '{row.LinkedServerName}' stored as its linked-server name while " +
                $"DataSync's own property computes '{linkedServer}' and ignores the column. The step SQL may name " +
                "either; check it before the first run.");
        }

        losses.Add(
            $"The connection string for '{endpoint.Name}' was composed from DataSource and InitialCatalog and has " +
            "never been used: DataSync reached this server through the linked server " +
            $"'{linkedServer}', created by dbo.ManageLinkedServer, and its step SQL says so with OPENQUERY. Test " +
            "the direct connection, or keep the linked server and leave the SQL as it is.");

        if(row.DatabaseSystem != 0)
        {
            endpoint.Extensions["datasync.databaseSystem"] = row.DatabaseSystem.ToString(CultureInfo.InvariantCulture);

            losses.Add(
                $"The server '{endpoint.Name}' is configured as database system {row.DatabaseSystem} " +
                "(0 is SQL Server, 1 is PostgreSQL). This engine speaks to SQL Server; the connection string was " +
                "composed anyway and will have to be replaced.");
        }

        if(!string.IsNullOrWhiteSpace(row.ErrorMailList))
        {
            endpoint.Extensions["datasync.errorMailList"] = row.ErrorMailList!;

            losses.Add(
                $"The server '{endpoint.Name}' carries an error mail list ({row.ErrorMailList}). This model has no " +
                "field for notification, so it is kept under the endpoint's 'datasync.errorMailList' and nothing " +
                "will send to it - wire the run's outcome up to whatever sends mail now.");
        }

        endpoints[serverId] = endpoint;

        return endpoint;
    }

    private SyncStep ReadStep(
        DataSyncStepRow row,
        SyncJobDefinition job,
        string jobId,
        string jobName,
        Dictionary<int, Endpoint> endpoints,
        List<string> losses)
    {
        var stepId = "datasync-step-" + row.SyncStepID.ToString(CultureInfo.InvariantCulture);
        var name = string.IsNullOrWhiteSpace(row.DestinationTable) ? stepId : row.DestinationTable.Trim();

        var step = new SyncStep
        {
            Id = stepId,
            Name = name,
            Order = row.OrderNumber,
            IsActive = row.IsActive,
            DestinationTable = row.DestinationTable ?? string.Empty,
            BatchSize = Math.Max(0, row.BatchSize),
            Source = new SourceQuery { Sql = Blank(row.CustomSQL) },

            // The SQL was written by hand against textual substitution of a bare @Name,
            // so it stays on that syntax until someone rewrites it.
            VariableSyntax = VariableSyntax.Legacy,

            // LoadDataInBatches ends in a MERGE keyed on the UniqueKey columns.
            Publication = new PublicationPlan { Mode = PublicationMode.Merge },

            Extensions =
            {
                ["datasync.syncStepId"] = row.SyncStepID.ToString(CultureInfo.InvariantCulture),
                ["datasync.lastStatus"] = row.LastStatus.ToString(CultureInfo.InvariantCulture)
            }
        };

        // SourceTable is informational: the procedure reads CustomSQL and never looks at
        // it. Kept so an operator can still see what the step was meant to read.
        if(!string.IsNullOrWhiteSpace(row.SourceTable))
            step.Extensions["datasync.sourceTable"] = row.SourceTable!;

        if(row.RemoteServerConfigID is { } serverId)
        {
            var endpoint = Endpoint(serverId, jobId, endpoints, losses);
            step.SourceEndpointId = endpoint.Id;

            if(job.Sources.All(x => !string.Equals(x.Id, endpoint.Id, StringComparison.Ordinal)))
            {
                job.Sources.Add(endpoint);

                losses.Add(
                    $"The step '{name}' of '{jobName}' reads from the server '{endpoint.Name}', which is not one of " +
                    "the process's SyncProcessSource rows. It was added to the job's sources so the step still " +
                    "runs, but the two lists disagreed in the catalog.");
            }
        }

        ReadFieldMaps(row, step, name, losses);
        ReadVariables(row, step, name, losses);
        ReadTombstones(row, step, name, losses);
        ReadProvenance(row, step, name, losses);

        return step;
    }

    /// <summary>
    /// Turns the step's maps into field maps - which for this format means keeping the
    /// key roles and almost nothing else.
    /// <para>
    /// <c>TargetField</c> is inert in the deployed engine: the column list comes from
    /// <c>GetColumnList</c>, which reads <c>INFORMATION_SCHEMA.COLUMNS</c> for the
    /// destination table at run time, and the only queries that touch SyncStepFieldMap
    /// select <c>SourceField</c> where <c>UniqueKey</c>, <c>SyncKey</c> or
    /// <c>DeleteKey</c> is set. That is exactly this model's default, so a job that had
    /// forty map rows imports with the two or three that carry a role - never one per
    /// column.
    /// </para>
    /// </summary>
    private void ReadFieldMaps(DataSyncStepRow row, SyncStep step, string stepName, List<string> losses)
    {
        var maps = _rows.FieldMaps
            .Where(x => x.GCRecord is null && x.SyncStepID == row.SyncStepID)
            .ToList();

        if(maps.Count == 0)
            return;

        var renames = new List<string>();
        var syncKeys = new List<string>();

        foreach(var map in maps)
        {
            var renamed =
                !string.IsNullOrWhiteSpace(map.SourceField) &&
                !string.IsNullOrWhiteSpace(map.TargetField) &&
                !string.Equals(map.SourceField, map.TargetField, StringComparison.OrdinalIgnoreCase);

            var hasRole = map.UniqueKey || map.SyncKey || map.DeleteKey;

            if(!hasRole && !renamed)
                continue;

            step.FieldMaps.Add(new FieldMap
            {
                Source = Blank(map.SourceField),
                Target = renamed ? map.TargetField!.Trim() : null,
                IsUniqueKey = map.UniqueKey,
                IsSyncKey = map.SyncKey,
                IsDeleteKey = map.DeleteKey
            });

            if(renamed)
                renames.Add($"{map.SourceField} to {map.TargetField}");

            if(map.SyncKey)
                syncKeys.Add(map.SourceField ?? string.Empty);
        }

        var dropped = maps.Count - step.FieldMaps.Count;
        if(dropped > 0)
        {
            step.Extensions["datasync.fieldMapRowsWithNoRole"] =
                dropped.ToString(CultureInfo.InvariantCulture);
        }

        if(renames.Count > 0)
        {
            losses.Add(
                $"The step '{stepName}' renames {string.Join(", ", renames)}, and DataSync never applied that: its " +
                "column list comes from INFORMATION_SCHEMA at run time and TargetField is read by nothing. The " +
                "renames are imported because this model does honour them, which means the destination columns " +
                "written will change - confirm each one.");
        }

        if(syncKeys.Count > 1)
        {
            losses.Add(
                $"The step '{stepName}' marks {syncKeys.Count} columns as SyncKey ({string.Join(", ", syncKeys)}). " +
                "DataSync joined them into one comma-separated string and took MAX() of that, which answers no " +
                "question anyone asked; here a watermark follows one column and the job will not validate until one " +
                "of them is chosen.");
        }

        if(step.FieldMaps.Any(x => x.IsSyncKey))
        {
            step.Incremental = new IncrementalPlan { Watermark = new WatermarkPlan() };

            losses.Add(
                $"The step '{stepName}' keeps a watermark, and it now lives in the engine's own store. DataSync " +
                "kept it in a SyncHistory table in the destination, keyed by SyncProcessID and DestinationTable, " +
                "and fed it back into the step's SQL through a variable - so that variable's query still reads the " +
                "old table. Carry the value over, or the first run reads from the variable's default.");
        }
    }

    private void ReadVariables(DataSyncStepRow row, SyncStep step, string stepName, List<string> losses)
    {
        var variables = _rows.Variables
            .Where(x => x.GCRecord is null && x.SyncStepID == row.SyncStepID)
            .ToList();

        foreach(var variable in variables)
        {
            step.Variables.Add(new SqlVariable
            {
                // Stored with the leading @ by convention; the model holds the bare name
                // and the Legacy syntax puts the @ back.
                Name = (variable.VariableName ?? string.Empty).Trim().TrimStart('@'),
                Sql = variable.CustomSQL ?? string.Empty,

                // The watermark lookups read the destination, which is where DataSync
                // keeps SyncHistory.
                RunAgainstDestination = true
            });
        }

        if(variables.Count > 1)
        {
            // The deployed loader never clears the table variable it reads each value
            // into, so from the second variable on the value taken is whatever the
            // previous read left behind. Worth saying even though this engine will not
            // reproduce it: the SQL was written around whatever it actually did.
            losses.Add(
                $"The step '{stepName}' has {variables.Count} variables. In the deployed loader the table variable " +
                "that receives each value is never cleared between them, so from the second one on the value used " +
                "was whatever the previous read left there. The SQL may have been tuned around that; check what " +
                "each variable is expected to return.");
        }

        var names = step.Variables.Select(x => x.Name).Where(x => x.Length > 0).ToList();

        foreach(var name in names)
        {
            var eaten = names
                .Where(x => x.Length > name.Length && x.StartsWith(name, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if(eaten.Count == 0)
                continue;

            losses.Add(
                $"The step '{stepName}' has the variable '{name}', whose name is a prefix of " +
                $"{string.Join(", ", eaten.Select(x => $"'{x}'"))}. With the legacy syntax substitution is textual, " +
                "so replacing the short one first destroys the long one. The import keeps the legacy syntax because " +
                "the SQL was written for it - move the step to the delimited syntax to make the ambiguity go away.");
        }
    }

    private void ReadTombstones(DataSyncStepRow row, SyncStep step, string stepName, List<string> losses)
    {
        if(string.IsNullOrWhiteSpace(row.SourceDeleteTable))
            return;

        var deleteKeys = step.FieldMaps.Count(x => x.IsDeleteKey);
        if(deleteKeys == 0)
        {
            step.Extensions["datasync.sourceDeleteTable"] = row.SourceDeleteTable!;

            losses.Add(
                $"The step '{stepName}' names the delete table '{row.SourceDeleteTable}' and no field map carries " +
                "the DeleteKey role, so there is no key to apply a deletion by and no ledger was imported. In " +
                "DataSync this combination deleted nothing and reported nothing.");

            return;
        }

        step.Incremental ??= new IncrementalPlan();
        step.Incremental.Tombstones = new TombstoneLedger
        {
            Table = row.SourceDeleteTable!.Trim(),
            KeyFormat = TombstoneKeyFormat.Legacy,
            LegacySeparator = ";",
            MarkProcessed = true
        };

        // The ledger is one table for many destinations, filtered by a TableName column
        // the model has no field for; the engine has to filter by the step's destination
        // table to read the same rows.
        losses.Add(
            $"The step '{stepName}' reads deletions from '{row.SourceDeleteTable}', which DataSync filters with " +
            $"TableName = '{step.DestinationTable}' and Processed = 0, and marks with Processed, " +
            "Processed_DateTime and Processed_DateTime_UTC. This model's ledger has no field for that TableName " +
            "filter, so the engine has to derive it from the step's destination table - check that the ledger's " +
            "TableName values match it exactly.");

        if(deleteKeys < 5)
        {
            losses.Add(
                $"The step '{stepName}' has {deleteKeys} delete key column(s). DataSync split the ledger's KeyValue " +
                "with PARSENAME, which counts from the right and stops at five, so anything but a five-part key " +
                "left the leading slots null and the join matched nothing: no row was deleted and no error was " +
                "raised. The imported ledger splits from the left with no limit, so deletions that never applied " +
                "will start applying - check the ledger's backlog before the first run.");
        }
    }

    private static void ReadProvenance(DataSyncStepRow row, SyncStep step, string stepName, List<string> losses)
    {
        if(string.IsNullOrWhiteSpace(row.FieldToUpdate))
            return;

        // The integers are not this model's integers: DataSync stores Function=0,
        // Number=1, String=2 while ProvenanceValueKind is Text=0, Number=1, Expression=2.
        // Casting one to the other would turn a SQL function into a literal.
        var kind = row.FieldUpdateType switch
        {
            0 => ProvenanceValueKind.Expression,
            1 => ProvenanceValueKind.Number,
            2 => ProvenanceValueKind.Text,
            _ => ProvenanceValueKind.Text
        };

        if(row.FieldUpdateType is < 0 or > 2)
        {
            losses.Add(
                $"The step '{stepName}' has FieldUpdateType {row.FieldUpdateType}, which is not one of " +
                "Function=0, Number=1 or String=2. It was imported as a literal, which is the safe reading - " +
                "confirm what was meant.");
        }

        step.Provenance = new ProvenanceStamp
        {
            Column = row.FieldToUpdate!.Trim(),
            Kind = kind,
            Value = row.UpdateValue ?? string.Empty
        };

        losses.Add(
            $"The step '{stepName}' stamps '{row.FieldToUpdate}' with '{row.UpdateValue}', and on the evidence of " +
            "the deployed script that stamp never reached the destination. FieldUpdateType is stored as an integer " +
            "and fetched into an nvarchar, so the value compared is '0', '1' or '2' while the script tests for " +
            "'function', 'number' and 'string'; no branch matches and the UPDATE it assembles ends at 'SET " +
            "[column] = '. And the statement targets the staging copy, after the MERGE has already read from it. " +
            "The stamp is imported as real behaviour, so rows will now carry a value they never carried.");
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>
/// The catalog as the caller read it: every table, unfiltered. The importer does the
/// filtering, because knowing that <c>GCRecord IS NOT NULL</c> means deleted is exactly
/// the kind of knowledge that should live in one place.
/// </summary>
public sealed class DataSyncCatalogRows
{
    public List<DataSyncProcessRow> Processes { get; init; } = new();

    public List<DataSyncProcessSourceRow> ProcessSources { get; init; } = new();

    public List<DataSyncStepRow> Steps { get; init; } = new();

    public List<DataSyncFieldMapRow> FieldMaps { get; init; } = new();

    public List<DataSyncVariableRow> Variables { get; init; } = new();

    public List<DataSyncRemoteServerRow> Servers { get; init; } = new();
}

/// <summary>A row of <c>SyncProcess</c>.</summary>
public sealed class DataSyncProcessRow
{
    public int SyncProcessID { get; init; }

    /// <summary>The name, and the key the deployed procedure is called with.</summary>
    public string? Description { get; init; }

    public int? TargetServerID { get; init; }

    public bool IsActive { get; init; }

    public int LastStatus { get; init; }

    /// <summary>XPO's deferred deletion marker. Not null means the row is deleted.</summary>
    public int? GCRecord { get; init; }
}

/// <summary>A row of <c>SyncProcessSource</c>, the scoping list.</summary>
public sealed class DataSyncProcessSourceRow
{
    public int SyncProcessSourceID { get; init; }

    public int SyncProcessID { get; init; }

    public int RemoteServerConfigID { get; init; }

    public bool IsCreatedLinkedServer { get; init; }

    public int? GCRecord { get; init; }
}

/// <summary>A row of <c>SyncStep</c>.</summary>
public sealed class DataSyncStepRow
{
    public int SyncStepID { get; init; }

    public int SyncProcessID { get; init; }

    /// <summary>
    /// A float on purpose, so a step can be dropped between two others without
    /// renumbering the rest.
    /// </summary>
    public double OrderNumber { get; init; }

    public int? RemoteServerConfigID { get; init; }

    /// <summary>Informational only: the deployed procedure reads CustomSQL and never this.</summary>
    public string? SourceTable { get; init; }

    public string? DestinationTable { get; init; }

    public string? CustomSQL { get; init; }

    public int BatchSize { get; init; }

    public bool IsActive { get; init; }

    public string? SourceDeleteTable { get; init; }

    public string? FieldToUpdate { get; init; }

    /// <summary>Function=0, Number=1, String=2 - not this model's numbering.</summary>
    public int FieldUpdateType { get; init; }

    public string? UpdateValue { get; init; }

    public int LastStatus { get; init; }

    public int? GCRecord { get; init; }
}

/// <summary>A row of <c>SyncStepFieldMap</c>.</summary>
public sealed class DataSyncFieldMapRow
{
    public int SyncStepFieldMapID { get; init; }

    public int SyncStepID { get; init; }

    public string? SourceField { get; init; }

    /// <summary>Stored, editable, and read by nothing in the deployed engine.</summary>
    public string? TargetField { get; init; }

    public bool UniqueKey { get; init; }

    public bool SyncKey { get; init; }

    public bool DeleteKey { get; init; }

    public int? GCRecord { get; init; }
}

/// <summary>A row of <c>SyncStepVariable</c>.</summary>
public sealed class DataSyncVariableRow
{
    public int SyncStepVariableID { get; init; }

    public int SyncStepID { get; init; }

    public string? VariableName { get; init; }

    public string? CustomSQL { get; init; }

    public int? GCRecord { get; init; }
}

/// <summary>
/// A row of <c>RemoteServerConfig</c>.
/// <para>
/// <see cref="UserPassword"/> is here so the importer can notice it and refuse it. The
/// column is a plain <c>nvarchar(100)</c> holding the password as typed, and the value is
/// passed to <c>dbo.ManageLinkedServer</c> on every save, so anyone who can read the
/// settings database can read every password in it. Nothing the importer produces
/// contains this value.
/// </para>
/// </summary>
public sealed class DataSyncRemoteServerRow
{
    public int ServerID { get; init; }

    public string ServerName { get; init; } = string.Empty;

    /// <summary>0 is SQL Server, 1 is PostgreSQL.</summary>
    public int DatabaseSystem { get; init; }

    public string? DataSource { get; init; }

    public string? InitialCatalog { get; init; }

    public string? UserID { get; init; }

    /// <summary>Clear text, and the one field that must never reach the model.</summary>
    public string? UserPassword { get; init; }

    public string? TableForSpeedTest { get; init; }

    /// <summary>
    /// What the column holds. DataSync's own property ignores it and returns
    /// <c>ServerName + "_LinkedServer"</c>, so a stored value that differs is stale.
    /// </summary>
    public string? LinkedServerName { get; init; }

    public string? ErrorMailList { get; init; }

    public bool IsActive { get; init; }

    public int? GCRecord { get; init; }
}
