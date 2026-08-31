using Lumio.Server.MvpHost.HostContracts;

namespace Lumio.Server.MvpHost.WorldSlot;

/// <summary>
/// Stateless fault-class truth table. Classification consumes only the explicit
/// runtime witness; it never infers a domain from an exception or catch boundary.
/// </summary>
public sealed class MvpFaultAdjudicator : IFaultAdjudicator
{
    public FaultAdjudication Classify(HostFaultClass? witness) => witness switch
    {
        null => new FaultAdjudication(HostFaultClass.SlotStateUnproven, true, false),
        HostFaultClass.SessionLocalProven => new FaultAdjudication(HostFaultClass.SessionLocalProven, false, true),
        HostFaultClass.SlotStateUnproven => new FaultAdjudication(HostFaultClass.SlotStateUnproven, true, false),
        HostFaultClass.ProcessFault => new FaultAdjudication(HostFaultClass.ProcessFault, true, false),
        HostFaultClass.None => new FaultAdjudication(HostFaultClass.None, false, false),
        _ => new FaultAdjudication(HostFaultClass.SlotStateUnproven, true, false),
    };
}
