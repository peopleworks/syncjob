using System.Collections;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Data.SqlClient;

namespace SyncJob.Core.Copy;

/// <summary>
/// A reader that holds <b>one</b> row - never the result set - so that a copy still
/// works in the two cases where handing <c>SqlBulkCopy</c> the raw reader does not.
/// <para>
/// The first is column order. <c>SqlBulkCopy</c> pulls values out of the source in the
/// destination's column order, and a reader opened with
/// <see cref="System.Data.CommandBehavior.SequentialAccess"/> throws the moment it is
/// asked to go backwards: <i>"Invalid attempt to read from column ordinal 1. With
/// CommandBehavior.SequentialAccess, you may only read from column ordinal 3 or
/// greater."</i> A copy whose columns all match by name has no business failing because
/// somebody created the two tables with their columns in a different order, so when the
/// orders disagree the row is read once, forwards, into this buffer and served from
/// there in whatever order is asked for.
/// </para>
/// <para>
/// The second is a user-defined type - <c>geography</c>, <c>geometry</c>,
/// <c>hierarchyid</c>. <c>SqlBulkCopy</c> reads those through
/// <c>SqlDataReader.GetValue</c>, which tries to load
/// <c>Microsoft.SqlServer.Types</c> to rebuild the CLR object and throws
/// <see cref="System.IO.FileNotFoundException"/> when it is not deployed. The server
/// never needs the object: it takes the same serialised bytes it sent, which
/// <c>GetSqlBytes</c> hands over without resolving anything. So a UDT column is read as
/// its bytes and declared as <c>varbinary</c>, and a spatial column travels through the
/// ordinary bulk path on a machine with no spatial assembly on it.
/// </para>
/// <para>
/// Both cases cost one row of memory, which does not grow with the row count - the
/// buffer is a single array, reused. The plain case pays nothing, because the copier
/// only reaches for this when it has to.
/// </para>
/// </summary>
internal sealed class RowBufferingReader : DbDataReader
{
    private readonly SqlDataReader _inner;
    private readonly bool[] _isUserDefinedType;
    private readonly object[] _values;

    internal RowBufferingReader(SqlDataReader inner, bool[] isUserDefinedType)
    {
        _inner = inner;
        _isUserDefinedType = isUserDefinedType;
        _values = new object[inner.FieldCount];
    }

    public override int FieldCount => _inner.FieldCount;

    public override int Depth => _inner.Depth;

    public override bool HasRows => _inner.HasRows;

    public override bool IsClosed => _inner.IsClosed;

    public override int RecordsAffected => _inner.RecordsAffected;

    public override object this[int ordinal] => _values[ordinal];

    public override object this[string name] => _values[GetOrdinal(name)];

    public override string GetName(int ordinal) => _inner.GetName(ordinal);

    public override int GetOrdinal(string name) => _inner.GetOrdinal(name);

    public override string GetDataTypeName(int ordinal) =>
        _isUserDefinedType[ordinal] ? "varbinary" : _inner.GetDataTypeName(ordinal);

    [return: DynamicallyAccessedMembers(
        DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.PublicProperties)]
    public override Type GetFieldType(int ordinal) =>
        _isUserDefinedType[ordinal] ? typeof(byte[]) : _inner.GetFieldType(ordinal);

    public override bool Read()
    {
        if(!_inner.Read())
            return false;

        for(var ordinal = 0; ordinal < _values.Length; ordinal++)
            _values[ordinal] = ReadValue(ordinal);

        return true;
    }

    public override async Task<bool> ReadAsync(CancellationToken cancellationToken)
    {
        if(!await _inner.ReadAsync(cancellationToken))
            return false;

        // Forwards, one column at a time: this is the only place the inner reader is
        // touched, so it never sees an out-of-order request.
        for(var ordinal = 0; ordinal < _values.Length; ordinal++)
        {
            _values[ordinal] = await _inner.IsDBNullAsync(ordinal, cancellationToken)
                ? DBNull.Value
                : ReadPresentValue(ordinal);
        }

        return true;
    }

    public override bool NextResult() => _inner.NextResult();

    public override object GetValue(int ordinal) => _values[ordinal];

    public override int GetValues(object[] values)
    {
        var count = Math.Min(values.Length, _values.Length);
        Array.Copy(_values, values, count);
        return count;
    }

    public override bool IsDBNull(int ordinal) => _values[ordinal] is DBNull;

    public override Task<bool> IsDBNullAsync(int ordinal, CancellationToken cancellationToken) =>
        Task.FromResult(IsDBNull(ordinal));

    public override bool GetBoolean(int ordinal) => (bool)_values[ordinal];

    public override byte GetByte(int ordinal) => (byte)_values[ordinal];

    public override char GetChar(int ordinal) => (char)_values[ordinal];

    public override DateTime GetDateTime(int ordinal) => (DateTime)_values[ordinal];

    public override decimal GetDecimal(int ordinal) => (decimal)_values[ordinal];

    public override double GetDouble(int ordinal) => (double)_values[ordinal];

    public override float GetFloat(int ordinal) => (float)_values[ordinal];

    public override Guid GetGuid(int ordinal) => (Guid)_values[ordinal];

    public override short GetInt16(int ordinal) => (short)_values[ordinal];

    public override int GetInt32(int ordinal) => (int)_values[ordinal];

    public override long GetInt64(int ordinal) => (long)_values[ordinal];

    public override string GetString(int ordinal) => (string)_values[ordinal];

    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length)
    {
        var bytes = (byte[])_values[ordinal];
        if(buffer is null)
            return bytes.Length;

        var count = (int)Math.Min(length, bytes.Length - dataOffset);
        Array.Copy(bytes, dataOffset, buffer, bufferOffset, count);
        return count;
    }

    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length)
    {
        var text = (string)_values[ordinal];
        if(buffer is null)
            return text.Length;

        var count = (int)Math.Min(length, text.Length - dataOffset);
        text.CopyTo((int)dataOffset, buffer, bufferOffset, count);
        return count;
    }

    /// <summary>
    /// Served from the buffer rather than the inner reader: with streaming on,
    /// <c>SqlBulkCopy</c> asks for a stream over a large value after the row has been
    /// read, and by then the inner reader has moved on.
    /// </summary>
    public override Stream GetStream(int ordinal) =>
        _values[ordinal] is byte[] bytes ? new MemoryStream(bytes, writable: false) : Stream.Null;

    public override TextReader GetTextReader(int ordinal) =>
        new StringReader(_values[ordinal] as string ?? string.Empty);

    /// <summary>
    /// Refused rather than forwarded: an enumerator over the inner reader would read
    /// around the buffer, which is the one thing this class exists to prevent.
    /// </summary>
    public override IEnumerator GetEnumerator() =>
        throw new NotSupportedException("a copy reads this reader by ordinal, never by enumerating it");

    public override void Close() => _inner.Close();

    private object ReadValue(int ordinal) =>
        _inner.IsDBNull(ordinal) ? DBNull.Value : ReadPresentValue(ordinal);

    private object ReadPresentValue(int ordinal) =>
        _isUserDefinedType[ordinal] ? _inner.GetSqlBytes(ordinal).Value : _inner.GetValue(ordinal);
}
