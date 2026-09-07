using Microsoft.Data.SqlClient;
using SyncJob.Core.Publication;

namespace SyncJob.IntegrationTests;

/// <summary>
/// The staging table against a real catalog.
/// <para>
/// The shape has to come from the destination every time. SyncJob's deployed staging
/// table is hand-created and hand-maintained, and the day it stops matching its
/// destination the publication that follows moves the data across shifted - so these
/// tests are about the two agreeing, column for column, and about nothing else being
/// carried over.
/// </para>
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class StagingLiveTests(SqlServerFixture fixture)
{
    [LiveFact]
    public async Task StagingHasTheDestinationsColumns_ColumnForColumn()
    {
        var connectionString = await fixture.CreateDatabaseAsync();
        await PublicationFixture.CreateLedgerAsync(connectionString, "dbo.Ledger");

        var staging = await new StagingTableFactory().CreateAsync(connectionString, "dbo.Ledger", null, default);

        Assert.Equal(
            await PublicationFixture.ColumnsAsync(connectionString, "dbo.Ledger"),
            await PublicationFixture.ColumnsAsync(connectionString, staging));
    }

    /// <summary>
    /// The identity, the computed column and the rowversion are the three the
    /// destination's own DDL carries and a hand-written staging table gets wrong.
    /// </summary>
    [LiveFact]
    public async Task StagingCarriesTheIdentity_TheComputedColumn_AndTheRowversion()
    {
        var connectionString = await fixture.CreateDatabaseAsync();
        await PublicationFixture.CreateLedgerAsync(connectionString, "dbo.Ledger");

        var staging = await new StagingTableFactory().CreateAsync(connectionString, "dbo.Ledger", null, default);

        var columns = await PublicationFixture.ColumnsAsync(connectionString, staging);
        Assert.Contains(columns, x => x.StartsWith("Id|int|", StringComparison.Ordinal) && x.Contains("|identity|", StringComparison.Ordinal));
        Assert.Contains(columns, x => x.StartsWith("Total|", StringComparison.Ordinal) && x.Contains("|computed|", StringComparison.Ordinal));
        Assert.Contains(columns, x => x.StartsWith("Version|timestamp|", StringComparison.Ordinal));
    }

    /// <summary>
    /// Bare on purpose: rows load faster without indexes, and it is the publisher that
    /// decides what the destination ends up carrying.
    /// </summary>
    [LiveFact]
    public async Task StagingHasNoIndexes_NoKeys_AndNoChecks()
    {
        var connectionString = await fixture.CreateDatabaseAsync();
        await PublicationFixture.CreateLedgerAsync(connectionString, "dbo.Ledger");

        var staging = await new StagingTableFactory().CreateAsync(connectionString, "dbo.Ledger", null, default);

        Assert.NotEmpty(await PublicationFixture.IndexesAsync(connectionString, "dbo.Ledger"));
        Assert.Empty(await PublicationFixture.IndexesAsync(connectionString, staging));
        Assert.Equal(0, Convert.ToInt32(await SqlServerFixture.ScalarAsync(
            connectionString,
            $"SELECT COUNT(*) FROM sys.objects WHERE parent_object_id = OBJECT_ID(N'{staging}') AND type IN ('PK','UQ','C','F');")));
    }

    /// <summary>
    /// The drift this whole package exists to end: a staging table left over from an
    /// older version of the destination is not reused, it is rebuilt.
    /// </summary>
    [LiveFact]
    public async Task AStagingTableLeftOverFromAnOlderShape_IsRebuiltFromTheDestination()
    {
        var connectionString = await fixture.CreateDatabaseAsync();
        await PublicationFixture.CreateLedgerAsync(connectionString, "dbo.Ledger");

        // What a hand-maintained staging table looks like a year after the column was
        // added to the destination and not to it.
        await SqlServerFixture.ExecuteAsync(
            connectionString, "CREATE TABLE dbo.Ledger_stage (Id int NOT NULL, Name nvarchar(120) NOT NULL);");

        var staging = await new StagingTableFactory()
            .CreateAsync(connectionString, "dbo.Ledger", "dbo.Ledger_stage", default);

        Assert.Equal(
            await PublicationFixture.ColumnsAsync(connectionString, "dbo.Ledger"),
            await PublicationFixture.ColumnsAsync(connectionString, staging));
    }

    [LiveFact]
    public async Task AGeneratedStagingTableIsDropped_AndACallerNamedOneIsNot()
    {
        var connectionString = await fixture.CreateDatabaseAsync();
        await PublicationFixture.CreateLedgerAsync(connectionString, "dbo.Ledger");
        var factory = new StagingTableFactory();

        var generated = await factory.CreateAsync(connectionString, "dbo.Ledger", null, default);
        var named = await factory.CreateAsync(connectionString, "dbo.Ledger", "dbo.KeepThisOne", default);

        await factory.DropAsync(connectionString, generated, default);
        await factory.DropAsync(connectionString, named, default);

        Assert.Equal(-1, await SqlServerFixture.CountAsync(connectionString, generated));
        Assert.Equal(0, await SqlServerFixture.CountAsync(connectionString, named));
    }

    [LiveFact]
    public async Task AGeneratedNameIsInTheDestinationsSchemaAndSaysWhatItIs()
    {
        var connectionString = await fixture.CreateDatabaseAsync();
        await SqlServerFixture.ExecuteAsync(connectionString, "CREATE SCHEMA warehouse;");
        await PublicationFixture.CreateLedgerAsync(connectionString, "warehouse.Ledger");

        var staging = await new StagingTableFactory().CreateAsync(connectionString, "warehouse.Ledger", null, default);

        Assert.StartsWith("[warehouse].[Ledger_stg_", staging, StringComparison.Ordinal);
    }

    /// <summary>
    /// Staging takes its shape from the destination, so a destination that is not there
    /// is refused in words rather than as a syntax error two statements later.
    /// </summary>
    [LiveFact]
    public async Task ADestinationThatIsNotThereIsRefused()
    {
        var connectionString = await fixture.CreateDatabaseAsync();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new StagingTableFactory().CreateAsync(connectionString, "dbo.NoSuchTable", null, default));

        Assert.Contains("does not exist", error.Message, StringComparison.Ordinal);
    }
}

/// <summary>
/// What the three live test classes in this package share: the destination they all
/// publish into, and the three catalog fingerprints they compare it against.
/// <para>
/// It is here rather than in <see cref="SqlServerFixture"/> because the fixture is
/// shared with the other work packages and is not this one's to change.
/// </para>
/// </summary>
internal static class PublicationFixture
{
    /// <summary>
    /// A destination with everything a staging table gets wrong when it is maintained
    /// by hand: an identity, a computed column, a rowversion, a default, a check
    /// constraint, a clustered key and a filtered index.
    /// </summary>
    public static async Task CreateLedgerAsync(string connectionString, string table)
    {
        await SqlServerFixture.ExecuteAsync(connectionString, $"""
            CREATE TABLE {table} (
                Id        int IDENTITY(1,1) NOT NULL,
                Name      nvarchar(120)     NOT NULL,
                Amount    decimal(19,4)     NOT NULL CONSTRAINT DF_{Bare(table)}_Amount DEFAULT (0),
                Total     AS (Amount * 2),
                ChangedAt datetime2(3)          NULL,
                Version   rowversion        NOT NULL,
                CONSTRAINT PK_{Bare(table)} PRIMARY KEY CLUSTERED (Id),
                CONSTRAINT CK_{Bare(table)}_Amount CHECK (Amount >= 0)
            );
            """);

        await SqlServerFixture.ExecuteAsync(
            connectionString,
            $"CREATE INDEX IX_{Bare(table)}_Name ON {table} (Name) INCLUDE (Amount) WHERE ChangedAt IS NOT NULL;");
    }

    /// <summary>Fills a staging table the way a copier would, identity values and all.</summary>
    public static Task LoadAsync(string connectionString, string table, int rows, int firstId) =>
        SqlServerFixture.ExecuteAsync(connectionString, $"""
            SET IDENTITY_INSERT {table} ON;
            WITH n(i) AS (
                SELECT TOP ({rows}) ROW_NUMBER() OVER (ORDER BY (SELECT NULL))
                FROM sys.all_columns a CROSS JOIN sys.all_columns b
            )
            INSERT INTO {table} (Id, Name, Amount, ChangedAt)
            SELECT {firstId} + i - 1, CONCAT(N'row ', {firstId} + i - 1), i * 1.5,
                   CASE WHEN i % 2 = 0 THEN DATEADD(minute, -i, SYSUTCDATETIME()) END
            FROM n;
            SET IDENTITY_INSERT {table} OFF;
            """);

    public static Task<List<string>> ColumnsAsync(string connectionString, string table) =>
        StringsAsync(connectionString, $"""
            SELECT CONCAT(
                       c.name COLLATE DATABASE_DEFAULT, '|', TYPE_NAME(c.user_type_id) COLLATE DATABASE_DEFAULT, '|', c.max_length, '|', c.precision, '|', c.scale, '|',
                       CASE WHEN c.is_nullable = 1 THEN 'null' ELSE 'not null' END, '|',
                       CASE WHEN c.is_identity = 1 THEN 'identity' ELSE '' END, '|',
                       CASE WHEN c.is_computed = 1 THEN 'computed' ELSE '' END, '|',
                       ISNULL(c.collation_name COLLATE DATABASE_DEFAULT, ''), '|', ISNULL(cc.definition COLLATE DATABASE_DEFAULT, ''))
            FROM sys.columns c
            LEFT JOIN sys.computed_columns cc ON cc.object_id = c.object_id AND cc.column_id = c.column_id
            WHERE c.object_id = OBJECT_ID(N'{table}')
            ORDER BY c.column_id;
            """);

    /// <summary>
    /// Every index on the object, by its shape rather than by its name alone: the
    /// point of the acceptance test is that a filtered index with an included column
    /// is still exactly that afterwards.
    /// </summary>
    public static Task<List<string>> IndexesAsync(string connectionString, string table) =>
        StringsAsync(connectionString, $"""
            SELECT CONCAT(
                       i.name COLLATE DATABASE_DEFAULT, '|', i.type_desc COLLATE DATABASE_DEFAULT, '|', i.is_unique, '|', i.is_primary_key, '|',
                       ISNULL(i.filter_definition COLLATE DATABASE_DEFAULT, ''), '|',
                       (SELECT STRING_AGG(CONVERT(nvarchar(MAX), CONCAT(
                                   col.name COLLATE DATABASE_DEFAULT,
                                   CASE WHEN ic.is_descending_key = 1 THEN ' desc' ELSE '' END,
                                   CASE WHEN ic.is_included_column = 1 THEN ' included' ELSE '' END)), ',')
                            WITHIN GROUP (ORDER BY ic.is_included_column, ic.key_ordinal, ic.index_column_id)
                        FROM sys.index_columns ic
                        JOIN sys.columns col ON col.object_id = ic.object_id AND col.column_id = ic.column_id
                        WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id))
            FROM sys.indexes i
            WHERE i.object_id = OBJECT_ID(N'{table}') AND i.name IS NOT NULL
            ORDER BY i.name;
            """);

    /// <summary>
    /// Every object-level permission on the destination, column-level grants included.
    /// A re-granting publisher is judged on this list and nothing else.
    /// </summary>
    public static Task<List<string>> PermissionsAsync(string connectionString, string table) =>
        StringsAsync(connectionString, $"""
            SELECT CONCAT(
                       USER_NAME(p.grantee_principal_id) COLLATE DATABASE_DEFAULT, '|', p.permission_name COLLATE DATABASE_DEFAULT, '|', p.state_desc COLLATE DATABASE_DEFAULT, '|',
                       ISNULL(COL_NAME(p.major_id, p.minor_id) COLLATE DATABASE_DEFAULT, ''))
            FROM sys.database_permissions p
            WHERE p.class = 1 AND p.major_id = OBJECT_ID(N'{table}')
            ORDER BY 1;
            """);

    public static async Task<List<string>> StringsAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();

        var values = new List<string>();
        while(await reader.ReadAsync())
            values.Add(reader.IsDBNull(0) ? string.Empty : reader.GetString(0));

        return values;
    }

    /// <summary>The table name without its schema, for building constraint names from.</summary>
    private static string Bare(string table) => table.Split('.')[^1].Trim('[', ']');
}
