using Microsoft.Data.SqlClient;

namespace SyncJob.Core.Import;

/// <summary>
/// Takes the credential out of a connection string on the way in, and names where the
/// secret has to live instead.
/// <para>
/// All three formats this package reads carry a password somewhere the model refuses to
/// hold one. DataSync keeps <c>RemoteServerConfig.UserPassword</c> in clear text in a
/// settings table. SyncJob's SQLite store keeps a DPAPI blob whose key belongs to the
/// machine that wrote it, not to the engine. SyncJob's JSON sections put
/// <c>Password=</c> straight into the connection string, next to the query, in a file
/// that gets copied between environments by hand.
/// </para>
/// <para>
/// An importer that copied any of them would put the secret into an object that is
/// logged, exported, diffed and mailed around. So the value is dropped and a reference
/// takes its place: a name the surface resolves, never a value the Core holds.
/// </para>
/// </summary>
internal static class EndpointSecrets
{
    /// <summary>
    /// What became of a connection string that was handed in.
    /// </summary>
    internal enum Redaction
    {
        /// <summary>There was no credential in it; it is carried through unchanged.</summary>
        NoSecret = 0,

        /// <summary>There was one and it is gone. The endpoint needs its secret re-entered.</summary>
        SecretRemoved = 1,

        /// <summary>
        /// It could not be parsed, and it names a password keyword, so no part of it can
        /// be carried without risking carrying half a secret. The whole string is dropped.
        /// </summary>
        Unparseable = 2
    }

    /// <summary>
    /// Returns the connection string without its password.
    /// <para>
    /// Parsed rather than split on semicolons, because a password is allowed to contain
    /// one when it is quoted - <c>Password="a;b"</c> - and a textual split would leave
    /// the tail of the secret behind as a stray fragment. Where the parser refuses the
    /// string altogether, nothing is carried rather than something half-cleaned.
    /// </para>
    /// </summary>
    internal static (string ConnectionString, Redaction Outcome) Redact(string? connectionString)
    {
        if(string.IsNullOrWhiteSpace(connectionString))
            return (string.Empty, Redaction.NoSecret);

        try
        {
            var builder = new SqlConnectionStringBuilder(connectionString);
            var hadPassword = !string.IsNullOrEmpty(builder.Password);

            // Remove() knows the synonyms, so this covers Password, PWD and the spaced
            // spellings the drivers also accept.
            builder.Remove("Password");

            return (builder.ConnectionString, hadPassword ? Redaction.SecretRemoved : Redaction.NoSecret);
        }
        catch(ArgumentException)
        {
            return MentionsAPassword(connectionString)
                ? (string.Empty, Redaction.Unparseable)
                : (connectionString, Redaction.NoSecret);
        }
        catch(FormatException)
        {
            return MentionsAPassword(connectionString)
                ? (string.Empty, Redaction.Unparseable)
                : (connectionString, Redaction.NoSecret);
        }
    }

    /// <summary>
    /// Builds a connection string from the parts a legacy store keeps in separate
    /// columns. The password is not a parameter, which is the point: there is no way to
    /// call this and accidentally include one.
    /// </summary>
    internal static string Compose(
        string? server,
        string? database,
        string? userId,
        bool trustServerCertificate,
        bool encrypt)
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = server ?? string.Empty,
            InitialCatalog = database ?? string.Empty,
            TrustServerCertificate = trustServerCertificate,
            Encrypt = encrypt
        };

        if(string.IsNullOrWhiteSpace(userId))
            builder.IntegratedSecurity = true;
        else
            builder.UserID = userId;

        return builder.ConnectionString;
    }

    /// <summary>
    /// Where the surface should now look for this endpoint's credential. It is a name,
    /// not a value - the Core never resolves it and never logs what it resolves to.
    /// </summary>
    internal static string Reference(string jobId, string endpointId) =>
        $"syncjob/{Slug(jobId)}/{Slug(endpointId)}/password";

    /// <summary>The sentence an operator has to act on before the job can run.</summary>
    internal static string CredentialLoss(string what, string secretRef) =>
        $"{what} carried a credential in clear text. It has not been imported: the model holds no passwords. " +
        $"Re-enter it as the secret named '{secretRef}' before the job runs.";

    private static bool MentionsAPassword(string connectionString) =>
        connectionString.Contains("password", StringComparison.OrdinalIgnoreCase) ||
        connectionString.Contains("pwd", StringComparison.OrdinalIgnoreCase);

    private static string Slug(string value)
    {
        var trimmed = value.Trim();
        if(trimmed.Length == 0)
            return "unnamed";

        var slug = new string(trimmed.Select(x => char.IsLetterOrDigit(x) || x is '-' or '_' or '.' ? x : '-').ToArray());
        return slug.Trim('-');
    }
}
