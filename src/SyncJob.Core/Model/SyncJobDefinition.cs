namespace SyncJob.Core.Model;

/// <summary>
/// One synchronisation job: where it may read from, what it writes, and the ordered
/// steps that do the work.
/// <para>
/// This is a plain object. It carries no connection, no logger and no knowledge of
/// where it was loaded from - a JSON section, a SQLite row, a catalog of XPO business
/// objects or an archive manifest. Each surface brings its own importer, which is what
/// lets four consumers share one engine.
/// </para>
/// </summary>
public sealed class SyncJobDefinition
{
    /// <summary>
    /// Stable identity, used to correlate runs and watermarks across renames. Where a
    /// legacy format has no id of its own - DataSync keys a job by its description -
    /// the importer derives one and records it, so a job that gets renamed does not
    /// lose its history.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>What an operator calls this job.</summary>
    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    /// <summary>
    /// A job that is switched off is skipped rather than removed, at three levels: the
    /// job, the step and the endpoint. All three are honoured.
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>Where the rows end up. Every step writes here.</summary>
    public Endpoint? Destination { get; set; }

    /// <summary>
    /// The endpoints a step is allowed to read from. It is a scoping list, not an
    /// execution list: nothing runs because it appears here. DataSync's
    /// <c>SyncProcessSource</c> exists for exactly this and is never read by its engine.
    /// </summary>
    public List<Endpoint> Sources { get; set; } = new();

    /// <summary>The work, in the order it runs.</summary>
    public List<SyncStep> Steps { get; set; } = new();

    /// <summary>
    /// How long a run may hold this job before another host may assume it died. See
    /// <see cref="JobRun.Lease"/> for why a job needs one at all.
    /// </summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromHours(2);

    /// <summary>Free-form labels, carried through from whichever format supplied them.</summary>
    public List<string> Tags { get; set; } = new();

    /// <summary>
    /// Anything a source format carried that this model has no field for. Kept rather
    /// than dropped so that importing and exporting a legacy job is lossless even
    /// where the model has not caught up.
    /// </summary>
    public Dictionary<string, string> Extensions { get; set; } = new();

    /// <summary>The steps that will actually run, in order.</summary>
    public IEnumerable<SyncStep> ActiveSteps =>
        Steps.Where(x => x.IsActive).OrderBy(x => x.Order);
}

/// <summary>
/// A database this job reads from or writes to.
/// <para>
/// The password is not here. A connection string is assembled at run time from a
/// secret the surface resolves - DPAPI, an environment variable, a vault - because
/// DataSync stores its passwords in clear text in a settings table and that is the one
/// thing from it that must not survive into the new engine.
/// </para>
/// </summary>
public sealed class Endpoint
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;

    /// <summary>
    /// A connection string with no credentials in it, or the whole thing when the
    /// connection is integrated. <see cref="SecretRef"/> names what completes it.
    /// </summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// How the surface should find this endpoint's secret - a DPAPI blob's key, an
    /// environment variable's name, a vault path. The Core never resolves it and never
    /// logs it; it hands the reference back to the surface that knows.
    /// </summary>
    public string? SecretRef { get; set; }

    /// <summary>
    /// A table cheap to read, used by the pre-flight speed probe. DataSync selects the
    /// whole table here rather than a single row; the probe in this engine reads one.
    /// </summary>
    public string? SpeedProbeTable { get; set; }

    public Dictionary<string, string> Extensions { get; set; } = new();
}

/// <summary>
/// One table's worth of work: read this, publish it there, that way.
/// </summary>
public sealed class SyncStep
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Position in the job. A double rather than an integer so a step can be inserted
    /// between two others without renumbering the rest - DataSync learned this and it
    /// is worth keeping.
    /// </summary>
    public double Order { get; set; }

    public bool IsActive { get; set; } = true;

    /// <summary>Which of the job's <see cref="SyncJobDefinition.Sources"/> this step reads.</summary>
    public string? SourceEndpointId { get; set; }

    /// <summary>Where the rows come from.</summary>
    public SourceQuery Source { get; set; } = new();

    /// <summary>The table being written, two-part where the destination has schemas.</summary>
    public string DestinationTable { get; set; } = string.Empty;

    /// <summary>
    /// Column-level departures from the default, which is that the destination's own
    /// columns are discovered and matched by name. Empty is the normal case - see
    /// <see cref="FieldMap"/>.
    /// </summary>
    public List<FieldMap> FieldMaps { get; set; } = new();

    /// <summary>Values resolved before the source query runs. See <see cref="SqlVariable"/>.</summary>
    public List<SqlVariable> Variables { get; set; } = new();

    /// <summary>
    /// How <see cref="Variables"/> are written inside <see cref="SourceQuery.Sql"/>.
    /// A step imported from the deployed system keeps <see cref="VariableSyntax.Legacy"/>,
    /// because its SQL was written by hand against that form; anything new is delimited.
    /// </summary>
    public VariableSyntax VariableSyntax { get; set; } = VariableSyntax.Delimited;

    /// <summary>How the rows reach the destination, and what has to be true first.</summary>
    public PublicationPlan Publication { get; set; } = new();

    /// <summary>What makes the next run read less than everything. Null means it reads everything.</summary>
    public IncrementalPlan? Incremental { get; set; }

    /// <summary>A column stamped on every row this step writes, or null. See <see cref="ProvenanceStamp"/>.</summary>
    public ProvenanceStamp? Provenance { get; set; }

    /// <summary>
    /// Rows read from the source per batch. Zero means the engine decides.
    /// <para>
    /// DataSync's loader silently overrides a configured size once a set passes ten
    /// thousand rows, always producing exactly ten batches. That is not carried over:
    /// a number an operator typed is either honoured or reported, never quietly
    /// replaced.
    /// </para>
    /// </summary>
    public int BatchSize { get; set; }

    /// <summary>Seconds a single command may take. Zero means no limit.</summary>
    public int CommandTimeoutSeconds { get; set; }

    public Dictionary<string, string> Extensions { get; set; } = new();
}

/// <summary>Where a step's rows come from: a query, a stored procedure, or a table.</summary>
public sealed class SourceQuery
{
    /// <summary>SQL, with <see cref="SqlVariable"/> placeholders still in it.</summary>
    public string? Sql { get; set; }

    /// <summary>A stored procedure to call instead of <see cref="Sql"/>.</summary>
    public string? StoredProcedure { get; set; }

    /// <summary>Parameters for <see cref="StoredProcedure"/>.</summary>
    public Dictionary<string, string> Parameters { get; set; } = new();

    /// <summary>
    /// A table to read whole, when neither of the other two is given. The engine builds
    /// the SELECT, which is how a job can be defined without anyone writing SQL.
    /// </summary>
    public string? Table { get; set; }

    /// <summary>
    /// An optional predicate appended to a <see cref="Table"/> read. Ignored when
    /// <see cref="Sql"/> or <see cref="StoredProcedure"/> is given - there the filter
    /// belongs in the SQL the operator wrote.
    /// </summary>
    public string? Where { get; set; }
}
