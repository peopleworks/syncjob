using SqlSchemaDiff.Models;
using SqlSchemaDiff.Services;

namespace SyncJob.Core.Publication;

/// <summary>
/// Gives a table the destination's keys, indexes, checks and foreign keys.
/// <para>
/// <c>ALTER TABLE ... SWITCH</c> moves a table's storage into another table's
/// metadata, and it refuses unless the two agree: SQL Server answers a missing index
/// with 4947, a missing check with 4971 and a missing foreign key with 4968. So the
/// staging table is loaded bare - which is the point of loading it bare - and is
/// brought up to the destination's shape afterwards, outside the publishing
/// transaction, where the sorting and the validation cost nothing but time.
/// </para>
/// <para>
/// Constraints are created unnamed. A key, check or foreign-key constraint is an
/// object in the schema and the destination already holds its name; letting SQL Server
/// generate one is what allows staging to live beside its destination. Index names are
/// scoped to their table, so those are carried across as they are - and after the
/// switch the destination is still the object it always was, still carrying its own
/// names.
/// </para>
/// </summary>
internal static class SwapAlignment
{
    /// <summary>
    /// The statements that bring <paramref name="target"/> into line with
    /// <paramref name="destination"/>, clustered structure first: an index built
    /// before the clustered index is one SQL Server rebuilds when the clustered index
    /// arrives.
    /// </summary>
    /// <remarks>
    /// The constraint models are altered in place. The snapshot they came from was
    /// extracted for this publication and is read by nothing else, and the alternative
    /// - copying ten properties by hand - is a copy that silently stops carrying
    /// whatever property the package adds next.
    /// </remarks>
    public static IEnumerable<string> Statements(TableModel destination, SqlObjectName target)
    {
        // Only the identifier is read out of this: every renderer below takes the
        // shape from the constraint or index model it is given.
        var table = new TableModel { Schema = target.Schema, Name = target.Name };

        foreach(var key in destination.KeyConstraints.OrderByDescending(x => SqlRender.IsClustered(x.IndexTypeDesc)))
        {
            key.IsSystemNamed = true;
            yield return SqlRender.BuildKeyConstraintAdd(table, key);
        }

        // A disabled index has no storage behind it, so it is not part of what a
        // switch has to match - and building an enabled copy of one on staging would
        // make the two disagree rather than agree.
        foreach(var index in destination.Indexes
                    .Where(x => !x.IsDisabled)
                    .OrderByDescending(x => SqlRender.IsClustered(x.TypeDesc)))
        {
            yield return SqlRender.BuildIndexCreate(table, index);
        }

        foreach(var check in destination.CheckConstraints)
        {
            check.IsSystemNamed = true;

            // Always validated, even where the destination's own copy is untrusted:
            // the staged rows are new, and finding out here that they break a
            // constraint costs a scan, while finding out during the switch costs the
            // publication.
            check.IsNotTrusted = false;
            yield return SqlRender.BuildCheckConstraintAdd(table, check);
        }

        foreach(var foreignKey in destination.ForeignKeys)
        {
            foreignKey.IsSystemNamed = true;
            foreignKey.IsNotTrusted = false;
            yield return SqlRender.BuildForeignKeyAdd(table, foreignKey);
        }
    }
}
