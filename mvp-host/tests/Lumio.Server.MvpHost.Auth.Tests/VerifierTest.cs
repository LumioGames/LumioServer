using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Lumio.Server.MvpHost.HostContracts;
using Xunit;

namespace Lumio.Server.MvpHost.Auth.Tests;

/// <summary>
/// injected exact-byte verifier。断言机制按设计 §4.3 的纪律逐条选定：
/// 「谁调用了谁」用 ArchUnitNET 的方法调用依赖；能用签名表达的用签名级反射；
/// 两者都判不了的（IL 层的「有没有短路 <c>==</c> 比较」）**降级为定向单测 + 评审项**，
/// 并在本文件与交回物中写明它是降级项。**不使用 IL 字节扫描。**
/// </summary>
public sealed class VerifierTest
{
    // ── ① ArchUnitNET 方法调用依赖断言：确实调用了 CryptographicOperations.FixedTimeEquals

    [Fact]
    public void 校验器对FixedTimeEquals存在方法调用依赖()
    {
        var calls = AuthArchitecture.MethodCallTargets(typeof(InjectedExactByteCredentialVerifier)).ToList();

        Assert.Contains(
            calls,
            target => target.Contains("CryptographicOperations", StringComparison.Ordinal)
                && target.Contains("FixedTimeEquals", StringComparison.Ordinal));
    }

    // ── ② ArchUnitNET 方法调用依赖断言：对两个 SequenceEqual 的调用依赖数为 0
    //
    //    ① 是本条的「扫描器确实看得见外部调用」对照组：没有 ①，本条可能因为
    //    扫描器什么都没看见而空真通过。

    [Theory]
    [InlineData("System.Linq.Enumerable")]
    [InlineData("System.MemoryExtensions")]
    public void 校验器不对任何SequenceEqual存在调用依赖(string declaringType)
    {
        var offenders = AuthArchitecture.MethodCallTargets(typeof(InjectedExactByteCredentialVerifier))
            .Where(target => target.Contains(declaringType, StringComparison.Ordinal)
                && target.Contains("SequenceEqual", StringComparison.Ordinal))
            .ToList();

        Assert.Empty(offenders);
    }

    // ── ③ **降级项**（IL 层不可判，见类型注释）：时序无关性的定向单测。

    /// <summary>
    /// **降级项**。等长不同前缀与等长不同尾缀两组输入，判定结果必须**完全一致**——
    /// 结果一致意味着两者走的是同一条返回路径；若实现里存在首字节短路的 <c>==</c> 比较，
    /// 前缀组会在第一字节就返回、而尾缀组要走完整个缓冲，那条差异本身就是可测的时序信道。
    /// 「不存在短路比较」在 IL 层不可判，本条只把**结果同一性**钉死，其余归评审项。
    /// </summary>
    [Fact]
    public void 等长不同前缀与不同尾缀的判定结果完全一致()
    {
        using var harness = new AuthHarness();
        var context = new VerificationContext(
            AuthHarness.ProductId, AuthHarness.GameReleaseId, "nonce-0001", harness.Clock.Now);

        using var differentPrefix = new OpaqueCredentialInput(AuthHarness.FlipFirstByte(AuthHarness.SharedSecret));
        using var differentSuffix = new OpaqueCredentialInput(AuthHarness.FlipLastByte(AuthHarness.SharedSecret));

        var prefixVerdict = harness.Verifier.Verify(differentPrefix, in context);
        var suffixVerdict = harness.Verifier.Verify(differentSuffix, in context);

        Assert.Equal(CredentialVerdict.Rejected, prefixVerdict.Verdict);
        Assert.Equal(CredentialVerdict.Rejected, suffixVerdict.Verdict);
        Assert.Equal(prefixVerdict, suffixVerdict);
    }

    [Fact]
    public void 完全相同的凭据被接受并产出主体身份()
    {
        using var harness = new AuthHarness();
        var context = new VerificationContext(
            AuthHarness.ProductId, AuthHarness.GameReleaseId, "nonce-0001", harness.Clock.Now);

        using var credential = new OpaqueCredentialInput((byte[])AuthHarness.SharedSecret.Clone());
        var verification = harness.Verifier.Verify(credential, in context);

        Assert.Equal(CredentialVerdict.Accepted, verification.Verdict);
        Assert.False(string.IsNullOrEmpty(verification.Principal.Value));
        Assert.Null(verification.AuditReason);
    }

    /// <summary>长度不同也必须是拒绝，且**不抛异常**——异常会把长度差变成另一条信道。</summary>
    [Fact]
    public void 长度不同的凭据被拒绝而不是抛出()
    {
        using var harness = new AuthHarness();
        var context = new VerificationContext(
            AuthHarness.ProductId, AuthHarness.GameReleaseId, "nonce-0001", harness.Clock.Now);

        using var shorter = new OpaqueCredentialInput(AuthHarness.SharedSecret[..4]);
        using var longer = new OpaqueCredentialInput([.. AuthHarness.SharedSecret, (byte)0x41]);

        Assert.Equal(CredentialVerdict.Rejected, harness.Verifier.Verify(shorter, in context).Verdict);
        Assert.Equal(CredentialVerdict.Rejected, harness.Verifier.Verify(longer, in context).Verdict);
    }

    // ── 比对材料缺失是致命的，不得降级成恒 Accept

    [Fact]
    public void 比对材料缺失时构造抛出且带明确原因()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"lumio-auth-missing-{Guid.NewGuid():N}.bin");

        var error = Assert.ThrowsAny<Exception>(() => InjectedExactByteCredentialVerifier.FromSecretFile(missing));

        Assert.Contains(missing, error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 空文件同样是致命的：零长度比对材料会让**任何零长度凭据**通过，
    /// 而那正是「降级成恒 Accept」的一种更隐蔽的形态。
    /// </summary>
    [Fact]
    public void 比对材料为空文件时构造抛出()
    {
        var dir = Directory.CreateTempSubdirectory("lumio-auth-empty-");
        try
        {
            var path = Path.Combine(dir.FullName, "empty.bin");
            File.WriteAllBytes(path, []);

            Assert.ThrowsAny<Exception>(() => InjectedExactByteCredentialVerifier.FromSecretFile(path));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    /// <summary>
    /// 没有任何「跳过认证」开关。生产 Profile 里「跳过认证」必须**不可表达**，
    /// 而不是靠一句「别打开它」。
    /// </summary>
    [Theory]
    [InlineData("SkipAuth")]
    [InlineData("DisableAuth")]
    [InlineData("AllowAnonymous")]
    public void 程序集内不存在任何认证旁路开关(string forbidden)
    {
        var offenders = AuthArchitecture.AllNames()
            .Where(name => name.Contains(forbidden, StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.Empty(offenders);
    }

    // ── 凭据类型永不可序列化、永不泄漏

    /// <summary>
    /// <c>OpaqueCredentialInput</c> 是 <c>sealed class</c> + <c>IDisposable</c>，
    /// 不带任何序列化特性、不实现值语义相等。
    ///
    /// **与卡面的一处口径差异（如实记录）**：卡面写「重写 <c>ToString()</c> 返回固定字面量
    /// <c>"OpaqueCredentialInput"</c>」，而 R-00274 已交付并合入的实现是**直接抛出**。
    /// 抛出对「凭据字节不得进日志」这条目的严格更强（连类型名都不给出，且误用点会立刻炸），
    /// 且该文件属 R-00274 的独占文件集，本卡不改它。本测试按**实测行为**断言。
    /// </summary>
    [Fact]
    public void 凭据类型不可被序列化出内容且ToString不泄漏输入字节()
    {
        var type = typeof(OpaqueCredentialInput);

        Assert.True(type.IsSealed);
        Assert.True(typeof(IDisposable).IsAssignableFrom(type));
        Assert.DoesNotContain(
            type.GetCustomAttributes(inherit: true),
            a => a.GetType().Namespace?.Contains("Serialization", StringComparison.Ordinal) == true);

        using var credential = new OpaqueCredentialInput((byte[])AuthHarness.SharedSecret.Clone());

        var serialized = TrySerialize(credential);
        if (serialized is not null)
        {
            Assert.DoesNotContain(
                Encoding.UTF8.GetString(AuthHarness.SharedSecret), serialized, StringComparison.Ordinal);
            Assert.DoesNotContain(
                Convert.ToBase64String(AuthHarness.SharedSecret), serialized, StringComparison.Ordinal);
            Assert.DoesNotContain(
                Convert.ToHexString(AuthHarness.SharedSecret), serialized, StringComparison.OrdinalIgnoreCase);
        }

        // ToString 不得给出任何输入字节。当前实现直接抛出，因此连一个字节都不可能泄漏。
        var text = TryToString(credential);
        if (text is not null)
        {
            Assert.DoesNotContain(Encoding.UTF8.GetString(AuthHarness.SharedSecret), text, StringComparison.Ordinal);
        }
    }

    private static string? TrySerialize(OpaqueCredentialInput credential)
    {
        try
        {
            return JsonSerializer.Serialize(credential);
        }
#pragma warning disable CA1031 // 断言的是「序列化产不出内容」——任何异常都满足该结论。
        catch (Exception)
#pragma warning restore CA1031
        {
            return null;
        }
    }

    private static string? TryToString(OpaqueCredentialInput credential)
    {
        try
        {
            return credential.ToString();
        }
#pragma warning disable CA1031 // 同上：抛出即「不泄漏」，是比返回字面量更强的结论。
        catch (Exception)
#pragma warning restore CA1031
        {
            return null;
        }
    }

    /// <summary>入队一条命令后凭据仍可读——防御性拷贝纪律的回归（Platform 的 IDefensiveCopy 是 payload opt-in）。</summary>
    [Fact]
    public void 认证命令持有的凭据在入队后仍可被校验()
    {
        using var harness = new AuthHarness();
        var command = harness.ValidCommand();

        var admission = harness.Service.TryEnqueueRequest(in command, out var outward);

        Assert.Equal(AuthQueueAdmission.Accepted, admission);
        Assert.True(outward.Accepted);
        Assert.True(harness.Service.TryDequeueRequest(out var dequeued));

        var context = dequeued.Context;
        Assert.Equal(CredentialVerdict.Accepted, harness.Verifier.Verify(dequeued.Credential, in context).Verdict);
    }
}
