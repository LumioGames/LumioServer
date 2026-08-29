using System;
using Lumio.Server.MvpHost.Wire;

namespace Lumio.Server.MvpHost.HostContracts;

// ── transport 身份与代次

public readonly record struct TransportConnectionId(ulong Value);

/// <summary>每次 Bind/Unbind 递增。携旧 epoch 的命令一律拒绝并回 <c>StaleConnectionGeneration</c>。</summary>
/// <remarks>
/// 与客户端的 <c>ConnectionGeneration</c> 是**两个独立计数器**（前者服务端绑定计数、
/// 后者客户端重连计数），MVP 不做映射。
/// </remarks>
public readonly record struct ConnectionEpoch(ulong Value);

/// <summary>transport 侧完全不透明的授权引用——transport 绝不依赖 Auth，只搬运这个值。</summary>
public readonly record struct PermissionGrantRef(ulong Value);

/// <summary>已过结构层校验的入站信封。<see cref="Header"/> 来自 Wire（Layer 1），此处只消费不重复定义。</summary>
public readonly record struct ValidatedEnvelopeBytes(ReadOnlyMemory<byte> Bytes, EnvelopeHeaderView Header);

public readonly record struct OutboundEnvelopeBytes(ReadOnlyMemory<byte> Bytes);

// ── session 身份

public readonly record struct ServerSessionId(string Value);

public readonly record struct SessionEpoch(ulong Value);

public readonly record struct AdmissionAttemptId(ulong Value);

/// <summary>不透明句柄。MVP 只存句柄本身，不复制任何 Runtime 内部状态。</summary>
public readonly record struct ReplicationContextHandle(ulong Value);

// ── auth 身份

public readonly record struct AuthRequestId(ulong Value);

public readonly record struct PrincipalId(string Value);

public readonly record struct GrantEpoch(ulong Value);

// ── world-slot 身份

public readonly record struct WorldSlotId(ulong Value);

public readonly record struct SlotEpoch(ulong Value);

public readonly record struct SlotReservationId(ulong Value);

/// <summary>
/// MVP 期只在内存记录，**不进入 <c>Snapshotting</c> 状态**——那需要缺席的 persistence-host
/// （<c>absences.json</c> 的 <c>ABS-PERSISTENCE-SNAPSHOT</c>）。
/// </summary>
public readonly record struct SnapshotCutRef(ulong Value);

// ── 宿主 ↔ Runtime 端口身份

public readonly record struct HostSessionId(string Value);

public readonly record struct HostWorldSlotId(ulong Value);

public readonly record struct LogicalTickToken(ulong Value);

/// <summary>跨端口搬运的不透明字节帧。Host 不解释其内容。</summary>
public readonly record struct WireFrame(ReadOnlyMemory<byte> Bytes);
