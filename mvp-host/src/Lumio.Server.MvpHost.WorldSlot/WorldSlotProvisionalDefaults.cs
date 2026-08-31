namespace Lumio.Server.MvpHost.WorldSlot;

/// <summary>
/// MVP queue and pacing defaults. Every value is provisional and remains a local
/// implementation default until the corresponding SRV-D decision is frozen.
/// </summary>
public static class WorldSlotProvisionalDefaults
{
    /// <summary>provisional SRV-D-001 aggregate command capacity.</summary>
    public const int AggregateInboxMaxItems = 64;

    /// <summary>provisional reserved command slots for Quiesce and Stop.</summary>
    public const int AggregateInboxReservedSlots = 2;

    /// <summary>provisional SRV-D-003 pacing permit capacity.</summary>
    public const int TickPermitCapacity = 1;

    /// <summary>provisional event output capacity.</summary>
    public const int SlotEventOutboxMaxItems = 256;

    /// <summary>provisional ingress item budget for one owner tick.</summary>
    public const int IngressDrainItemsPerTick = 64;

    /// <summary>provisional ingress byte budget for one owner tick.</summary>
    public const long IngressDrainBytesPerTick = 65_536;
}

/// <summary>
/// Aggregate command admission result. <see cref="AggregateBusy"/> is an internal
/// module state; outward-facing results use the registered <c>QueueFull</c> id.
/// </summary>
public enum AggregateQueueAdmission
{
    Accepted,
    AggregateBusy,
    Closed,
}
