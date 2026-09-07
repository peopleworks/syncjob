using System.Security.Cryptography;
using System.Text;
using SyncJob.Database;

namespace SyncJob.IntegrationTests;

/// <summary>
/// How the SQLite catalog stores a connection's password.
/// <para>
/// It stored it XOR'd against a sixteen-byte key that was a string literal in the same
/// file, in a public repository. Anyone holding a <c>syncjob.db</c> could recover every
/// password in it. The original comment said so - "replace with proper AES in
/// production" - and it shipped anyway, which is the ordinary way this happens.
/// </para>
/// <para>
/// There was a second half, which is why nobody noticed. The write was XOR and every
/// read was <c>ProtectedData.Unprotect</c>, so a SQL-auth connection created through
/// <c>connection add</c> had never been runnable: it died on "no se pudo desencriptar la
/// contraseña". Only integrated-security connections worked, and those store no
/// password at all.
/// </para>
/// </summary>
public sealed class ConnectionSecretTests
{
    private static readonly byte[] LegacyKey = Encoding.UTF8.GetBytes("SyncJob2025Key16");

    [WindowsFact]
    public void APasswordIsNotRecoverableFromTheBytesAlone()
    {
        const string password = "correct horse battery staple";
        var stored = ConnectionRepository.EncryptString(password);

        // The old form was the plaintext with a repeating key over it, so its length came
        // back unchanged and its structure survived. DPAPI's does not.
        Assert.NotEqual(Encoding.UTF8.GetBytes(password).Length, stored.Length);
        Assert.DoesNotContain(password, Encoding.UTF8.GetString(stored), StringComparison.Ordinal);

        // And the XOR that used to open it opens nothing now.
        Assert.DoesNotContain(password, Encoding.UTF8.GetString(Xor(stored)), StringComparison.Ordinal);

        Assert.Equal(password, ConnectionRepository.DecryptString(stored));
    }

    /// <summary>
    /// A row written by the previous version still opens. Refusing it would leave
    /// someone's job unable to run over a change they did not make.
    /// </summary>
    [WindowsFact]
    public void ASecretWrittenByThePreviousVersionStillOpens()
    {
        var legacy = Xor(Encoding.UTF8.GetBytes("s3cr3t"));

        Assert.Equal("s3cr3t", ConnectionRepository.DecryptString(legacy));

        // And it can be told apart, so an operator can be pointed at the ones worth
        // re-entering without the secret ever being shown.
        Assert.True(ConnectionRepository.IsLegacyFormat(legacy));
        Assert.False(ConnectionRepository.IsLegacyFormat(ConnectionRepository.EncryptString("s3cr3t")));
    }

    /// <summary>
    /// Reading the same bytes twice gives the same answer twice.
    /// <para>
    /// The previous implementation XOR'd the caller's own array in place, so the first
    /// read decrypted it and the second re-encrypted it: one entity read twice in one
    /// process returned the password and then returned rubbish.
    /// </para>
    /// </summary>
    [WindowsFact]
    public void ReadingTheSameBytesTwiceGivesTheSameAnswer()
    {
        var stored = ConnectionRepository.EncryptString("repeatable");
        Assert.Equal("repeatable", ConnectionRepository.DecryptString(stored));
        Assert.Equal("repeatable", ConnectionRepository.DecryptString(stored));

        var legacy = Xor(Encoding.UTF8.GetBytes("repeatable"));
        Assert.Equal("repeatable", ConnectionRepository.DecryptString(legacy));
        Assert.Equal("repeatable", ConnectionRepository.DecryptString(legacy));
    }

    /// <summary>
    /// The service reads these at night under an account that is not the one that typed
    /// them, which is why the scope is the machine and not the user.
    /// </summary>
    [WindowsFact]
    public void TheSecretIsProtectedForTheMachineSoAServiceAccountCanReadIt()
    {
        var stored = ConnectionRepository.EncryptString("readable-by-the-service");

        Assert.Equal(
            "readable-by-the-service",
            Encoding.UTF8.GetString(ProtectedData.Unprotect(stored, null, DataProtectionScope.LocalMachine)));
    }

    [WindowsFact]
    public void AnEmptySecretStaysEmptyInBothDirections()
    {
        Assert.Empty(ConnectionRepository.EncryptString(string.Empty));
        Assert.Equal(string.Empty, ConnectionRepository.DecryptString([]));
        Assert.False(ConnectionRepository.IsLegacyFormat([]));
    }

    private static byte[] Xor(byte[] bytes)
    {
        var copy = new byte[bytes.Length];
        for(var i = 0; i < bytes.Length; i++)
            copy[i] = (byte)(bytes[i] ^ LegacyKey[i % LegacyKey.Length]);

        return copy;
    }
}
