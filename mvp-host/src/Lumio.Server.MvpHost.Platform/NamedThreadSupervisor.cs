using System;
using System.Collections.Generic;
using System.Threading;

namespace Lumio.Server.MvpHost.Platform;

/// <summary>
/// 具名受监督线程。全仓所有线程都必须经这里创建——不得任何模块自己 <c>new Thread</c> 或
/// <c>Task.Run</c> 起后台循环，否则崩溃会静默消失、名字在 dump 里是 "Thread-17"。
///
/// 线程体抛出时线程终止并产出一条 <see cref="SupervisionEvent"/>（<c>Faulted = true</c>），
/// 由调用方经 <see cref="TryDrainEvent"/> 取走并按自己的故障域裁决——
/// 监督器<b>不代替任何人决定</b>故障是否升级。
/// </summary>
internal sealed class NamedThreadSupervisor : INamedThreadSupervisor
{
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Thread> _threads = [];
    private readonly Queue<SupervisionEvent> _events = new();
    private readonly Lock _gate = new();
    private bool _disposed;

    public ThreadHandle Start(string name, IThreadBody body)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(body);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var thread = new Thread(() => Run(name, body, _cts.Token))
        {
            Name = name,
            IsBackground = true,
        };

        lock (_gate)
        {
            _threads.Add(thread);
        }

        thread.Start();
        return new ThreadHandle(name, thread.ManagedThreadId);
    }

    public bool TryDrainEvent(out SupervisionEvent evt)
    {
        lock (_gate)
        {
            if (_events.Count > 0)
            {
                evt = _events.Dequeue();
                return true;
            }
        }

        evt = default;
        return false;
    }

    private void Run(string name, IThreadBody body, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var result = body.Step(ct);
                if (!result.Continue)
                {
                    Publish(new SupervisionEvent(name, false, result.StableErrorId));
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 取消是正常收敛，不产出故障事件。
        }
#pragma warning disable CA1031 // 监督边界必须捕获一切：线程体的任何异常都要变成一条可裁决的事件，
        catch (Exception)      // 而不是让线程静默消失。故障域裁决归调用方，不在这里推断。
#pragma warning restore CA1031
        {
            Publish(new SupervisionEvent(name, true, "PanicBoundary"));
        }
    }

    private void Publish(in SupervisionEvent evt)
    {
        lock (_gate)
        {
            // Dispose 之后不再**产出**新事件（卡面判据）。已产出的存量刻意保留：
            // 宿主收敛时常见的顺序就是先 Dispose 再 drain 故障做退出诊断，清空会把诊断吃掉。
            if (!_disposed)
            {
                _events.Enqueue(evt);
            }
        }
    }

    public void Dispose()
    {
        List<Thread> pending;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            pending = [.. _threads];
        }

        _cts.Cancel();

        foreach (var thread in pending)
        {
            // join 失败 = 线程挂死。不能静默走人：留一条可 drain 的事件，
            // 否则「宿主关不干净」这件事在任何地方都看不到。
            if (!thread.Join(TimeSpan.FromSeconds(5)))
            {
                lock (_gate)
                {
                    _events.Enqueue(new SupervisionEvent(thread.Name ?? "<unnamed>", true, "TimedOut"));
                }
            }
        }

        _cts.Dispose();
    }
}
