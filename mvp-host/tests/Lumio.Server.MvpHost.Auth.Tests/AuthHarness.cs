using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Lumio.Server.MvpHost.HostContracts;
using Lumio.Server.MvpHost.Observability;
using Lumio.Server.MvpHost.Platform;
using Lumio.Server.MvpHost.TestKit;

namespace Lumio.Server.MvpHost.Auth.Tests;

/// <summary>
/// 测试装置。凭据材料落一个**进程私有的临时文件**，随装置一起删除——
/// 凭据不进仓库、不进 fixture、不进日志，这条纪律在测试侧同样成立。
/// </summary>
internal sealed class AuthHarness : IDisposable
{
    internal const string ProductId = "A";
    internal const string GameReleaseId = "A-1.1.0";
    internal const string ReleasePoolId = "pool-a-1.1";
    internal const string ProducerId = "server-auth";
    internal const string FixedTimestamp = "2026-08-27T00:10:00Z";

    /// <summary>
    /// 已知凭据字节串。<c>NoCredentialInLogsTest</c> 用它做泄漏探针——
    /// 它必须是一串在任何合法日志里都不可能自然出现的字节。
    /// </summary>
    internal static readonly byte[] SharedSecret =
        Encoding.UTF8.GetBytes("lumio-mvp-shared-secret-canary-9d41f0");

    /// <summary>
    /// 与 <see cref="SharedSecret"/> **等长**、内容完全不同的探针凭据：
    /// 等长才走得到比对本身，内容独特才让「它有没有出现在日志里」是个可判问题。
    /// </summary>
    internal static readonly byte[] CanaryCredential =
        Encoding.UTF8.GetBytes("CANARY-must-never-reach-any-log-7b2e04");

    private readonly DirectoryInfo tempDir;

    internal AuthHarness(int auditCapacity = 64, int diagnosticCapacity = 64)
    {
        this.tempDir = Directory.CreateTempSubdirectory("lumio-auth-tests-");
        this.SecretPath = Path.Combine(this.tempDir.FullName, "shared-secret.bin");
        File.WriteAllBytes(this.SecretPath, SharedSecret);

        this.Clock = new FakeMonotonicClock();
        this.Trace = new RecordingHostTraceSink();
        this.AuditInbox = PlatformModule.CreateInbox<AuditRecord>(new QueueBudget(auditCapacity, 65536));
        this.DiagnosticInbox = PlatformModule.CreateInbox<DiagnosticRecord>(new QueueBudget(diagnosticCapacity, 65536));

        this.Observability = ObservabilityModule.Create(
            this.AuditInbox,
            this.DiagnosticInbox,
            new FakeWallClock(FixedTimestamp),
            this.Trace,
            new HostIdentity(ProductId, GameReleaseId, ProducerId));

        this.Verifier = InjectedExactByteCredentialVerifier.FromSecretFile(this.SecretPath);
        this.Window = MvpAntiReplayWindow.Create(
            this.Clock,
            AuthProvisionalDefaults.AntiReplayWindowSeconds,
            AuthProvisionalDefaults.ReplayStormThreshold);

        this.Service = MvpAuthorizationService.Create(
            this.Verifier, this.Window, this.Clock, this.Observability, ReleasePoolId);
    }

    internal string SecretPath { get; }

    internal FakeMonotonicClock Clock { get; }

    internal RecordingHostTraceSink Trace { get; }

    internal IBoundedInbox<AuditRecord> AuditInbox { get; }

    internal IBoundedInbox<DiagnosticRecord> DiagnosticInbox { get; }

    internal ObservabilityServices Observability { get; }

    internal InjectedExactByteCredentialVerifier Verifier { get; }

    internal MvpAntiReplayWindow Window { get; }

    internal MvpAuthorizationService Service { get; }

    /// <summary>合法凭据的一次认证命令。<paramref name="nonce"/> 是防重放键的后半。</summary>
    internal AuthenticateCommand ValidCommand(string nonce = "nonce-0001", ulong requestId = 1)
        => this.Command(SharedSecret, nonce, requestId);

    /// <summary>凭据错误但**等长**的一次认证命令——长度相同才测得到比对本身。</summary>
    internal AuthenticateCommand WrongCredentialCommand(string nonce = "nonce-0001", ulong requestId = 1)
        => this.Command(FlipLastByte(SharedSecret), nonce, requestId);

    /// <summary>用探针凭据走一次注定失败的认证。</summary>
    internal AuthenticateCommand CanaryCommand(string nonce = "nonce-canary", ulong requestId = 1)
        => this.Command(CanaryCredential, nonce, requestId);

    internal AuthenticateCommand Command(byte[] credentialBytes, string nonce, ulong requestId)
        => new(
            RequestId: new AuthRequestId(requestId),
            ConnectionId: new TransportConnectionId(requestId),
            ConnectionEpoch: new ConnectionEpoch(1),
            Credential: new OpaqueCredentialInput((byte[])credentialBytes.Clone()),
            Context: new VerificationContext(ProductId, GameReleaseId, nonce, this.Clock.Now));

    /// <summary>等长、**首字节**不同。</summary>
    internal static byte[] FlipFirstByte(byte[] source)
    {
        var copy = (byte[])source.Clone();
        copy[0] ^= 0xFF;
        return copy;
    }

    /// <summary>等长、**尾字节**不同。</summary>
    internal static byte[] FlipLastByte(byte[] source)
    {
        var copy = (byte[])source.Clone();
        copy[^1] ^= 0xFF;
        return copy;
    }

    /// <summary>排空 audit 队列并把每条序列化成线形态，供泄漏与形状断言使用。</summary>
    internal List<string> DrainAuditJson()
    {
        var texts = new List<string>();
        while (this.AuditInbox.TryDequeue(out var record))
        {
            texts.Add(LoggingEventJson.From(record).ToJsonString());
        }

        return texts;
    }

    internal List<AuditRecord> DrainAuditRecords()
    {
        var records = new List<AuditRecord>();
        while (this.AuditInbox.TryDequeue(out var record))
        {
            records.Add(record);
        }

        return records;
    }

    internal List<string> DrainDiagnosticJson()
    {
        var texts = new List<string>();
        while (this.DiagnosticInbox.TryDequeue(out var record))
        {
            texts.Add(LoggingEventJson.From(record).ToJsonString());
        }

        return texts;
    }

    public void Dispose()
    {
        this.Service.CloseQueues();
        this.tempDir.Delete(recursive: true);
    }
}
