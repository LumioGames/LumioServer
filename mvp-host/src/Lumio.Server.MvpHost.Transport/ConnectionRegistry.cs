using System;
using System.Collections.Generic;
using Lumio.Server.MvpHost.HostContracts;
using Lumio.Server.MvpHost.Platform;

namespace Lumio.Server.MvpHost.Transport;

/// <summary>
/// 连接注册表。**全部写入 API 都是 <c>internal</c>**——本程序集之外无法写入，
/// 因此「transport 是注册表的唯一写入者」是编译期事实，不是纪律。
///
/// Session 想影响它只有一条路：<see cref="ITransportControlPort.TrySend"/> 发类型化命令，
/// 由本程序集应用并回显式 ack。
/// </summary>
internal sealed class ConnectionRegistry
{
    private readonly Dictionary<ulong, ConnectionEntry> entries = new();

    internal IReadOnlyCollection<ulong> ConnectionIds => this.entries.Keys;

    internal bool TryGet(TransportConnectionId id, out ConnectionEntry entry)
        => this.entries.TryGetValue(id.Value, out entry!);

    internal ConnectionEntry Add(TransportConnectionId id, QueueBudget ingressBudget, QueueBudget egressBudget)
    {
        var entry = new ConnectionEntry(id, ingressBudget, egressBudget);
        this.entries[id.Value] = entry;
        return entry;
    }

    internal void Remove(TransportConnectionId id) => this.entries.Remove(id.Value);
}

/// <summary>
/// 一条连接的全部状态。<see cref="Epoch"/> 每次 Bind/Unbind 递增；
/// 携旧 epoch 的命令一律拒绝并回 <c>StaleConnectionGeneration</c>。
/// </summary>
internal sealed class ConnectionEntry
{
    internal ConnectionEntry(TransportConnectionId id, QueueBudget ingressBudget, QueueBudget egressBudget)
    {
        this.Id = id;
        this.State = TransportConnectionState.Accepted;
        this.Ingress = PlatformModule.CreateInbox<ValidatedEnvelopeBytes>(in ingressBudget);
        this.Egress = PlatformModule.CreateInbox<OutboundEnvelopeBytes>(in egressBudget);
    }

    internal TransportConnectionId Id { get; }

    internal TransportConnectionState State { get; private set; }

    internal ConnectionEpoch Epoch { get; private set; }

    internal ServerSessionId? BoundSession { get; private set; }

    internal PermissionGrantRef Grant { get; private set; }

    /// <summary>
    /// Principal evidence established by the carrier during channel upgrade.
    /// It is metadata only; credentials and nonce values are never retained.
    /// </summary>
    internal TransportAuthenticationEvidence? AuthenticationEvidence { get; private set; }

    /// <summary>per-connection ingress。Reliable 满载断连，Unreliable 丢弃并计数。</summary>
    internal IBoundedInbox<ValidatedEnvelopeBytes> Ingress { get; }

    // Drain may stop at a byte-budget boundary. Keep one item outside the
    // inbox so a frame that did not fit is still the next FIFO item.
    private bool hasDeferredIngress;
    private ValidatedEnvelopeBytes deferredIngress;

    internal int IngressCount
        => this.Ingress.Count + (this.hasDeferredIngress ? 1 : 0);

    internal bool TryTakeIngress(out ValidatedEnvelopeBytes item)
    {
        if (this.hasDeferredIngress)
        {
            item = this.deferredIngress;
            this.deferredIngress = default;
            this.hasDeferredIngress = false;
            return true;
        }

        return this.Ingress.TryDequeue(out item);
    }

    internal void DeferIngress(in ValidatedEnvelopeBytes item)
    {
        if (this.hasDeferredIngress)
        {
            throw new InvalidOperationException("Only one ingress item may be deferred");
        }

        this.deferredIngress = item;
        this.hasDeferredIngress = true;
    }

    internal EnqueueResult TryEnqueueIngress(in ValidatedEnvelopeBytes item)
    {
        if (this.IngressCount >= this.Ingress.Budget.MaxItems)
        {
            return new EnqueueResult(EnqueueStatus.Full, "QueueFull");
        }

        return this.Ingress.TryEnqueue(in item);
    }

    internal void ClearDeferredIngress()
    {
        this.deferredIngress = default;
        this.hasDeferredIngress = false;
    }

    /// <summary>per-connection egress。</summary>
    internal IBoundedInbox<OutboundEnvelopeBytes> Egress { get; }

    internal int UnreliableDropCount { get; private set; }

    internal long InboundBytesThisMessage { get; private set; }

    internal int InboundMessagesInWindow { get; private set; }

    internal MonotonicInstant RateWindowStart { get; private set; }

    internal MonotonicInstant LastActivity { get; private set; }

    internal TimerId? IdleTimer { get; private set; }

    /// <summary>
    /// 合法迁移表。**非法迁移必须被拒绝而不是被容忍**——一个能从 Closed 回到 Active
    /// 的连接会让 epoch 语义失效：对端拿着旧 epoch 的命令就能复活它。
    /// </summary>
    private static readonly Dictionary<TransportConnectionState, TransportConnectionState[]> Allowed = new()
    {
        [TransportConnectionState.Accepted] = new[] { TransportConnectionState.EnvelopeValidated, TransportConnectionState.Closed },
        [TransportConnectionState.EnvelopeValidated] = new[] { TransportConnectionState.Bound, TransportConnectionState.Closed },
        [TransportConnectionState.Bound] = new[] { TransportConnectionState.Active, TransportConnectionState.Draining, TransportConnectionState.Closed },
        [TransportConnectionState.Active] = new[] { TransportConnectionState.Draining, TransportConnectionState.Closed },
        [TransportConnectionState.Draining] = new[] { TransportConnectionState.Closed },
        [TransportConnectionState.Closed] = Array.Empty<TransportConnectionState>(),
    };

    internal bool CanTransitionTo(TransportConnectionState next) => Allowed[this.State].Contains(next);

    internal bool TryTransitionTo(TransportConnectionState next)
    {
        if (!this.CanTransitionTo(next))
        {
            return false;
        }

        this.State = next;
        return true;
    }

    /// <summary>Bind / Unbind 各递增一次 epoch。</summary>
    internal ConnectionEpoch BumpEpoch()
    {
        this.Epoch = new ConnectionEpoch(this.Epoch.Value + 1);
        return this.Epoch;
    }

    internal void ApplyBind(ServerSessionId session, PermissionGrantRef grant)
    {
        this.BoundSession = session;
        this.Grant = grant;
    }

    internal void ApplyUnbind()
    {
        this.BoundSession = null;
        this.Grant = default;
    }

    internal void SetAuthenticationEvidence(TransportAuthenticationEvidence? evidence)
        => this.AuthenticationEvidence = evidence;

    internal void CountUnreliableDrop() => this.UnreliableDropCount++;

    internal void ResetInboundMessageBytes() => this.InboundBytesThisMessage = 0;

    internal long AddInboundBytes(int count) => this.InboundBytesThisMessage += count;

    internal void NoteActivity(MonotonicInstant now) => this.LastActivity = now;

    internal void SetIdleTimer(TimerId? timer) => this.IdleTimer = timer;

    /// <summary>限流窗口：稳态速率 + 突发上限，超限按可拒绝处理（只断该连接）。</summary>
    internal bool TryAdmitInbound(MonotonicInstant now)
    {
        var windowTicks = TimeSpan.TicksPerSecond;

        if (now.Ticks - this.RateWindowStart.Ticks >= windowTicks)
        {
            this.RateWindowStart = now;
            this.InboundMessagesInWindow = 0;
        }

        this.InboundMessagesInWindow++;
        return this.InboundMessagesInWindow <= TransportProvisionalLimits.InboundBurst;
    }
}

internal static class ConnectionStateExtensions
{
    internal static bool Contains(this TransportConnectionState[] states, TransportConnectionState value)
    {
        foreach (var state in states)
        {
            if (state == value)
            {
                return true;
            }
        }

        return false;
    }
}
