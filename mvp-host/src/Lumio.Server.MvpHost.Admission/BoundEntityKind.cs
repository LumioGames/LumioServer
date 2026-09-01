using System;

namespace Lumio.Server.MvpHost.Admission;

public enum BoundEntityKind
{
    Player,
    Bot,
}

public static class BoundEntityKindExtensions
{
    public static string ToContractValue(this BoundEntityKind kind)
    {
        return kind switch
        {
            BoundEntityKind.Player => EntityBindingPort.PlayerEntityType,
            BoundEntityKind.Bot => EntityBindingPort.BotEntityType,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
    }
}
