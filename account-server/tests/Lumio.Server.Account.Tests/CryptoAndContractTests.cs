using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Lumio.Server.Account;
using Xunit;

namespace Lumio.Server.Account.Tests;

public sealed class CryptoAndContractTests
{
    [Fact]
    public void FrozenContractPinMatchesOriginFile()
    {
        Assert.Equal("lumio.account-port.v1", AccountPort.ContractId);
        Assert.Equal("lumio-account-v1", AccountPort.Subprotocol);
        Assert.Equal(14, AccountErrorCode.All.Length);
        Assert.Equal(AccountErrorCode.All.Length, AccountErrorCode.All.Distinct(StringComparer.Ordinal).Count());

        var origin = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "contract", "ORIGIN"));
        Assert.True(File.Exists(origin), origin);
        var text = File.ReadAllText(origin);
        Assert.Contains(AccountPort.FrozenArchitectureCommit, text, StringComparison.Ordinal);
        Assert.Contains(AccountPort.FrozenContractSha256, text, StringComparison.Ordinal);
        Assert.Contains(AccountPort.ContractId, text, StringComparison.Ordinal);

        var jsonPath = Path.Combine(Path.GetDirectoryName(origin)!, "account-port-v1.json");
        Assert.True(File.Exists(jsonPath), jsonPath);
        var sha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(jsonPath))).ToLowerInvariant();
        Assert.Equal(AccountPort.FrozenContractSha256, sha);
    }

    [Fact]
    public void LumioBinUsesLittleEndianAndLengthPrefixedAscii()
    {
        var writer = new LumioBinWriter();
        writer.WriteU16(1);
        writer.WriteU8(7);
        writer.WriteAscii("acct");
        writer.WriteU64(0x0102030405060708);
        writer.WriteFixedBytes([0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F, 0x10, 0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18, 0x19]);
        var bytes = writer.ToArray();

        Assert.Equal(new byte[] { 0x01, 0x00 }, bytes[..2]);
        Assert.Equal(7, bytes[2]);
        Assert.Equal(new byte[] { 0x04, 0x00, 0x00, 0x00 }, bytes.AsSpan(3, 4).ToArray());
        Assert.Equal("acct", Encoding.ASCII.GetString(bytes, 7, 4));
        Assert.Equal(0x08, bytes[11]);
        Assert.Equal(16, bytes.Length - (2 + 1 + 4 + 4 + 8));
    }

    [Fact]
    public void LumioSignaturePreimageIsDomainSeparatedAscii()
    {
        var payload = Encoding.ASCII.GetBytes("payload");
        var preimage = LumioSignature.Preimage("account-admission", "admission-credential-v1", payload);
        var text = Encoding.ASCII.GetString(preimage);
        Assert.StartsWith("LumioSignatureV1", text, StringComparison.Ordinal);
        Assert.Contains("account-admission", text, StringComparison.Ordinal);
        Assert.Contains("admission-credential-v1", text, StringComparison.Ordinal);
        Assert.Equal(0, preimage[16]);
    }

    [Fact]
    public void Ed25519RoundTripVerifiesAndTamperFails()
    {
        var (seed, publicKey) = Ed25519Keys.Generate();
        var message = Encoding.ASCII.GetBytes("hello");
        var signature = Ed25519Keys.Sign(seed, message);
        Assert.Equal(64, signature.Length);
        Assert.True(Ed25519Keys.Verify(publicKey, message, signature));
        signature[0] ^= 0x01;
        Assert.False(Ed25519Keys.Verify(publicKey, message, signature));
    }
}
