using System;
using System.IO;
using System.Security.Cryptography;
using Lumio.Server.MvpHost.HostContracts;

namespace Lumio.Server.MvpHost.Auth;

/// <summary>
/// D-011 的合法落点：**仅测试 / 集成用的 exact-byte verifier，不成为 wire 标准**
/// （与 Rust 侧 <c>modules/auth/src/adapters/injected.rs</c> 同一位置的 C# 实现，不新开面）。
///
/// 本类型**不定义**凭据 blob 的内部格式、算法、轮换或 nonce 派生——那些都属 D-011，
/// 冻结前任何仓库定义它就是在发明公共 wire 格式。它只实现 D-011 已冻结的那一条行为契约。
///
/// 比对走 <see cref="CryptographicOperations.FixedTimeEquals"/>：
/// 逐字节 <c>==</c> 会在第一个不等的字节上短路，比对耗时因而与「猜对了几个前缀字节」相关，
/// 那是一条可测的时序信道。<c>SequenceEqual</c>（LINQ 与 <c>MemoryExtensions</c> 两个重载）
/// 同样短路，因此本类型对它们的调用依赖数被断言为 0。
/// </summary>
public sealed class InjectedExactByteCredentialVerifier : ICredentialVerifier
{
    /// <summary>
    /// **Host 私有**的审计理由文本（设计 §6.2 把「审计事件 fields 的自定义内容」划在可自由定义一侧）。
    /// 它不是 <c>StableErrorId</c>、不跨 wire，也**绝不含任何凭据字节**。
    /// </summary>
    private const string MaterialMismatch = "channel credential material did not match";

    private readonly byte[] material;

    private InjectedExactByteCredentialVerifier(byte[] material) => this.material = material;

    /// <summary>
    /// 启动期从 <c>--shared-secret-file</c> 指向的文件载入比对材料。
    ///
    /// **材料缺失 / 不可读 / 为空一律抛出，绝不返回一个恒 Accept 的降级实现**：
    /// 「验证材料损坏或缺失」在设计 §6.2 的错误分级里是**可致命**类，处置是进程拒绝启动。
    /// 空文件被一并当作致命——零长度材料会让任何零长度凭据通过，那是降级放行更隐蔽的形态。
    /// </summary>
    public static InjectedExactByteCredentialVerifier FromSecretFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        byte[] material;
        try
        {
            material = File.ReadAllBytes(path);
        }
        catch (Exception inner) when (inner is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new InvalidOperationException(
                $"通道认证比对材料不可读：{path}。认证材料缺失属可致命类，进程拒绝启动，不降级放行。", inner);
        }

        if (material.Length == 0)
        {
            throw new InvalidOperationException(
                $"通道认证比对材料为空文件：{path}。零长度材料等价于放行，进程拒绝启动。");
        }

        return new InjectedExactByteCredentialVerifier(material);
    }

    /// <summary>
    /// 常量时间 exact-byte 比对。长度不同时同样**返回拒绝而不是抛出**——
    /// 抛出会把「长度对不对」变成另一条可观测信道，而且会让调用方需要 try/catch 才能拒绝一次连接。
    /// </summary>
    public CredentialVerification Verify(OpaqueCredentialInput credential, in VerificationContext context)
    {
        ArgumentNullException.ThrowIfNull(credential);

        return CryptographicOperations.FixedTimeEquals(credential.Span, this.material)
            ? new CredentialVerification(CredentialVerdict.Accepted, PrincipalOf(in context), null)
            : new CredentialVerification(CredentialVerdict.Rejected, default, MaterialMismatch);
    }

    /// <summary>
    /// MVP 只有一份共享比对材料，因此「持有该材料的人」就是唯一主体。
    /// 主体标识由**调用方声明的 Release 身份**派生，**绝不由凭据字节派生**——
    /// 后者会让一个本该永不出现在任何地方的值变成一个到处传递的标识符。
    /// 该标识是 Host 私有的（设计 §6.2「已验证身份缓存」一栏），不跨 wire。
    /// </summary>
    internal static PrincipalId PrincipalOf(in VerificationContext context)
        => new($"principal-{context.ProductId}-{context.GameReleaseId}");
}
