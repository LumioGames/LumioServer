using System;
using System.Security.Cryptography;
using System.Text;
using Isopoh.Cryptography.Argon2;

namespace Lumio.Server.Account;

internal sealed class Argon2idPasswordHasher
{
    public static string Hash(string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        var salt = new byte[AccountPort.Argon2SaltLength];
        RandomNumberGenerator.Fill(salt);
        var passwordBytes = Encoding.UTF8.GetBytes(password);
        try
        {
            var config = CreateConfig(passwordBytes, salt);
            return Argon2.Hash(config);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
            CryptographicOperations.ZeroMemory(salt);
        }
    }

    public static bool Verify(string encodedHash, string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(encodedHash);
        ArgumentNullException.ThrowIfNull(password);
        return Argon2.Verify(encodedHash, password, threads: AccountPort.Argon2Parallelism);
    }

    private static Argon2Config CreateConfig(byte[] passwordBytes, byte[] salt)
    {
        return new Argon2Config
        {
            Type = Argon2Type.HybridAddressing,
            Version = Argon2Version.Nineteen,
            TimeCost = AccountPort.Argon2Iterations,
            MemoryCost = AccountPort.Argon2MemoryKib,
            Lanes = AccountPort.Argon2Parallelism,
            Threads = AccountPort.Argon2Parallelism,
            Password = passwordBytes,
            Salt = salt,
            HashLength = AccountPort.Argon2HashLength,
        };
    }
}
