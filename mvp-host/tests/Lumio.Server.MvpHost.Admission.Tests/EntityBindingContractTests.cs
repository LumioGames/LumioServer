using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Lumio.Server.MvpHost.Admission;
using Xunit;

namespace Lumio.Server.MvpHost.Admission.Tests;

public sealed class EntityBindingContractTests
{
    private static readonly string[] TakeoverNoticePropertyNames =
    {
        "Detail", "IssuedAt", "ReasonCode", "ReconnectEligible",
    };

    [Fact]
    public void FrozenBindingContractPinMatchesOriginFile()
    {
        Assert.Equal("lumio.entity-binding-query.v1", EntityBindingPort.ContractId);
        Assert.Equal("fb3dca451aef5b392876e284ba871b05e58186bb", EntityBindingPort.FrozenArchitectureCommit);
        Assert.Equal("0cff8d3d15ff94f3e80939f72aae58eee14456a263277d4f82652eb5a17b726a", EntityBindingPort.FrozenContractSha256);

        var origin = Path.Combine(AppContext.BaseDirectory, "contract", "ORIGIN");
        Assert.True(File.Exists(origin), origin);
        var text = File.ReadAllText(origin);
        Assert.Contains(EntityBindingPort.FrozenArchitectureCommit, text, StringComparison.Ordinal);
        Assert.Contains(EntityBindingPort.FrozenContractSha256, text, StringComparison.Ordinal);
        Assert.Contains(EntityBindingPort.ContractId, text, StringComparison.Ordinal);

        var jsonPath = Path.Combine(Path.GetDirectoryName(origin)!, "entity-binding-and-query-v1.json");
        Assert.True(File.Exists(jsonPath), jsonPath);
        var sha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(jsonPath))).ToLowerInvariant();
        Assert.Equal(EntityBindingPort.FrozenContractSha256, sha);
    }

    [Fact]
    public void TakeoverNoticeShapeMatchesAccountPort()
    {
        var names = typeof(TakeoverNotice)
            .GetProperties()
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(TakeoverNoticePropertyNames, names);
        Assert.Equal("connection_superseded", EntityBindingPort.TakeoverReasonCode);
        Assert.Equal(4096, EntityBindingPort.MaxBindingsPerRoom);
    }
}
