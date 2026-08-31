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

    internal int Count => this.entries.Count;

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
    internal (PrincipalId PrincipalId, string ProductId, string GameReleaseId)? AuthenticationMetadata { get; private set; }

    private readonly object authenticationGate = new();

    /// <summary>per-connection ingress。Reliable 满载断连，Unreliable 丢弃并计数。</summary>
    internal IBoundedInbox<ValidatedEnvelopeBytes> Ingress { get; }

    private readonly object ingressGate = new();
    private long ingressQueuedBytes;
    private bool hasInFlightIngress;
    private int inFlightIngressBytes;

    // Drain may stop at a byte-budget boundary. Keep one item outside the
    // inbox so a frame that did not fit is still the next FIFO item.
    private bool hasDeferredIngress;
    private ValidatedEnvelopeBytes deferredIngress;

    internal int IngressCount
    {
        get
        {
            lock (this.ingressGate)
            {
                return this.Ingress.Count
                    + (this.hasDeferredIngress ? 1 : 0)
                    + (this.hasInFlightIngress ? 1 : 0);
            }
        }
    }

    internal bool TryTakeIngress(out ValidatedEnvelopeBytes item)
    {
        lock (this.ingressGate)
        {
            if (this.hasInFlightIngress)
            {
                throw new InvalidOperationException("Complete or defer the in-flight ingress item first");
            }

            if (this.hasDeferredIngress)
            {
                item = this.deferredIngress;
                this.deferredIngress = default;
                this.hasDeferredIngress = false;
                this.hasInFlightIngress = true;
                this.inFlightIngressBytes = item.Bytes.Length;
                return true;
            }

            if (!this.Ingress.TryDequeue(out item))
            {
                return false;
            }

            this.hasInFlightIngress = true;
            this.inFlightIngressBytes = item.Bytes.Length;
            return true;
        }
    }

    internal void DeferIngress(in ValidatedEnvelopeBytes item)
    {
        lock (this.ingressGate)
        {
            if (!this.hasInFlightIngress || this.inFlightIngressBytes != item.Bytes.Length)
            {
                throw new InvalidOperationException("No matching ingress item is in flight");
            }

            if (this.hasDeferredIngress)
            {
                throw new InvalidOperationException("Only one ingress item may be deferred");
            }

            this.deferredIngress = item;
            this.hasDeferredIngress = true;
            this.hasInFlightIngress = false;
            this.inFlightIngressBytes = 0;
        }
    }

    internal void CommitIngressTake()
    {
        lock (this.ingressGate)
        {
            if (!this.hasInFlightIngress)
            {
                return;
            }

            this.ingressQueuedBytes -= this.inFlightIngressBytes;
            this.hasInFlightIngress = false;
            this.inFlightIngressBytes = 0;
        }
    }

    internal EnqueueResult TryEnqueueIngress(in ValidatedEnvelopeBytes item)
    {
        lock (this.ingressGate)
        {
            if (this.Ingress.Count
                    + (this.hasDeferredIngress ? 1 : 0)
                    + (this.hasInFlightIngress ? 1 : 0) >= this.Ingress.Budget.MaxItems
                || item.Bytes.Length > this.Ingress.Budget.MaxBytes - this.ingressQueuedBytes)
            {
                return new EnqueueResult(EnqueueStatus.Full, "QueueFull");
            }

            var result = this.Ingress.TryEnqueue(in item);
            if (result.Status == EnqueueStatus.Accepted)
            {
                this.ingressQueuedBytes += item.Bytes.Length;
            }

            return result;
        }
    }

    internal void ClearDeferredIngress()
    {
        lock (this.ingressGate)
        {
            while (this.Ingress.TryDequeue(out _))
            {
            }

            if (this.hasDeferredIngress)
            {
                this.deferredIngress = default;
                this.hasDeferredIngress = false;
            }

            if (this.hasInFlightIngress)
            {
                this.hasInFlightIngress = false;
                this.inFlightIngressBytes = 0;
            }

            this.ingressQueuedBytes = 0;
        }
    }

    /// <summary>per-connection egress。</summary>
    internal IBoundedInbox<OutboundEnvelopeBytes> Egress { get; }

    private readonly object egressGate = new();
    private long egressQueuedBytes;
    private bool hasInFlightEgress;
    private int inFlightEgressBytes;

    private bool hasDeferredEgress;
    private OutboundEnvelopeBytes deferredEgress;

    internal int EgressCount
    {
        get
        {
            lock (this.egressGate)
            {
                return this.Egress.Count
                    + (this.hasDeferredEgress ? 1 : 0)
                    + (this.hasInFlightEgress ? 1 : 0);
            }
        }
    }

    internal bool TryTakeEgress(out OutboundEnvelopeBytes item)
    {
        lock (this.egressGate)
        {
            if (this.hasInFlightEgress)
            {
                throw new InvalidOperationException("Complete or defer the in-flight egress item first");
            }

            if (this.hasDeferredEgress)
            {
                item = this.deferredEgress;
                this.deferredEgress = default;
                this.hasDeferredEgress = false;
                this.hasInFlightEgress = true;
                this.inFlightEgressBytes = item.Bytes.Length;
                return true;
            }

            if (!this.Egress.TryDequeue(out item))
            {
                return false;
            }

            this.hasInFlightEgress = true;
            this.inFlightEgressBytes = item.Bytes.Length;
            return true;
        }
    }

    internal void DeferEgress(in OutboundEnvelopeBytes item)
    {
        lock (this.egressGate)
        {
            if (!this.hasInFlightEgress || this.inFlightEgressBytes != item.Bytes.Length)
            {
                throw new InvalidOperationException("No matching egress item is in flight");
            }

            if (this.hasDeferredEgress)
            {
                throw new InvalidOperationException("Only one egress item may be deferred");
            }

            this.deferredEgress = item;
            this.hasDeferredEgress = true;
            this.hasInFlightEgress = false;
            this.inFlightEgressBytes = 0;
        }
    }

    internal void CommitEgressTake()
    {
        lock (this.egressGate)
        {
            if (!this.hasInFlightEgress)
            {
                return;
            }

            this.egressQueuedBytes -= this.inFlightEgressBytes;
            this.hasInFlightEgress = false;
            this.inFlightEgressBytes = 0;
        }
    }

    internal EnqueueResult TryEnqueueEgress(in OutboundEnvelopeBytes item)
    {
        lock (this.egressGate)
        {
            if (this.Egress.Count
                    + (this.hasDeferredEgress ? 1 : 0)
                    + (this.hasInFlightEgress ? 1 : 0) >= this.Egress.Budget.MaxItems
                || item.Bytes.Length > this.Egress.Budget.MaxBytes - this.egressQueuedBytes)
            {
                return new EnqueueResult(EnqueueStatus.Full, "QueueFull");
            }

            var result = this.Egress.TryEnqueue(in item);
            if (result.Status == EnqueueStatus.Accepted)
            {
                this.egressQueuedBytes += item.Bytes.Length;
            }

            return result;
        }
    }

    internal void ClearDeferredEgress()
    {
        lock (this.egressGate)
        {
            while (this.Egress.TryDequeue(out _))
            {
            }

            if (this.hasDeferredEgress)
            {
                this.deferredEgress = default;
                this.hasDeferredEgress = false;
            }
            if (this.hasInFlightEgress)
            {
                this.hasInFlightEgress = false;
                this.inFlightEgressBytes = 0;
            }

            this.egressQueuedBytes = 0;
        }
    }

    internal ConnectionCloseReason? PendingCloseReason { get; private set; }

    internal string? PendingCloseStableErrorId { get; private set; }

    internal MonotonicInstant? PendingCloseDeadline { get; private set; }

    internal void SetPendingClose(
        ConnectionCloseReason reason,
        string? stableErrorId,
        MonotonicInstant deadline)
    {
        this.PendingCloseReason = reason;
        this.PendingCloseStableErrorId = stableErrorId;
        this.PendingCloseDeadline ??= deadline;
    }

    internal void ClearPendingClose()
    {
        this.PendingCloseReason = null;
        this.PendingCloseStableErrorId = null;
        this.PendingCloseDeadline = null;
    }

    internal int UnreliableDropCount { get; private set; }

    internal long InboundBytesThisMessage { get; private set; }

    private readonly object rateGate = new();
    private long inboundRateCredit =
        (long)TransportProvisionalLimits.InboundBurst * TimeSpan.TicksPerSecond;
    private long inboundRateLastRefillTicks;

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

    internal void SetAuthenticationMetadata(
        PrincipalId principalId,
        string productId,
        string gameReleaseId)
    {
        lock (this.authenticationGate)
        {
            this.AuthenticationMetadata = (principalId, productId, gameReleaseId);
        }
    }

    internal bool TryTakeAuthenticationMetadata(
        out PrincipalId principalId,
        out string productId,
        out string gameReleaseId)
    {
        lock (this.authenticationGate)
        {
            var metadata = this.AuthenticationMetadata;
            this.AuthenticationMetadata = null;
            if (metadata is not { } value)
            {
                principalId = default;
                productId = string.Empty;
                gameReleaseId = string.Empty;
                return false;
            }

            principalId = value.PrincipalId;
            productId = value.ProductId;
            gameReleaseId = value.GameReleaseId;
            return true;
        }
    }

    internal void ClearAuthenticationMetadata()
    {
        lock (this.authenticationGate)
        {
            this.AuthenticationMetadata = null;
        }
    }

    internal void CountUnreliableDrop() => this.UnreliableDropCount++;

    internal void ResetInboundMessageBytes() => this.InboundBytesThisMessage = 0;

    internal long AddInboundBytes(int count) => this.InboundBytesThisMessage += count;

    internal void NoteActivity(MonotonicInstant now) => this.LastActivity = now;

    internal void SetIdleTimer(TimerId? timer) => this.IdleTimer = timer;

    /// <summary>限流窗口：稳态速率 + 突发上限，超限按可拒绝处理（只断该连接）。</summary>
    internal bool TryAdmitInbound(MonotonicInstant now)
    {
        lock (this.rateGate)
        {
            var capacity = (long)TransportProvisionalLimits.InboundBurst * TimeSpan.TicksPerSecond;
            if (now.Ticks > this.inboundRateLastRefillTicks)
            {
                var elapsed = now.Ticks - this.inboundRateLastRefillTicks;
                var headroom = capacity - this.inboundRateCredit;
                var refillRate = TransportProvisionalLimits.InboundMessagesPerSecond;
                var saturatingElapsed = (headroom + refillRate - 1) / refillRate;
                this.inboundRateCredit = elapsed >= saturatingElapsed
                    ? capacity
                    : this.inboundRateCredit + (elapsed * refillRate);
                this.inboundRateLastRefillTicks = now.Ticks;
            }

            if (this.inboundRateCredit < TimeSpan.TicksPerSecond)
            {
                return false;
            }

            this.inboundRateCredit -= TimeSpan.TicksPerSecond;
            return true;
        }
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
