using System;
using System.Collections.Generic;
using System.Linq;
using Lumio.Server.MvpHost.HostContracts;

namespace Lumio.Server.MvpHost.WorldSlot;

/// <summary>One immutable forward edge from the published WorldSlotHost machine.</summary>
public readonly record struct WorldSlotTransition(
    WorldSlotHostState From,
    WorldSlotHostState To,
    string Event);

/// <summary>
/// The fail-stop rule is deliberately separate from the forward table. The published
/// table contains only recoverable/normal edges; any active state may be adjudicated
/// as Faulted when a slot-scoped witness is absent or unsafe.
/// </summary>
public readonly record struct AnyActiveToRule(WorldSlotHostState Target)
{
    public WorldSlotHostState To => this.Target;

    public bool AppliesTo(WorldSlotHostState state)
        => this.Target == WorldSlotHostState.Faulted
            && state is not WorldSlotHostState.Destroyed and not WorldSlotHostState.Faulted;

    public bool Covers(WorldSlotHostState state) => this.AppliesTo(state);

    public IEnumerable<WorldSlotTransition> Expand()
    {
        var rule = this;
        return Enum.GetValues<WorldSlotHostState>()
            .Where(rule.AppliesTo)
            .Select(state => new WorldSlotTransition(state, rule.Target, "Fault"));
    }

}

/// <summary>
/// C#'s state-machine projection. ForwardTransitions mirrors the generated contract
/// exactly; AnyActiveToFaulted remains a single independent rule rather than eleven
/// copied edges.
/// </summary>
public static class WorldSlotStateMachine
{
    public static IReadOnlyList<WorldSlotTransition> ForwardTransitions { get; } =
        new[]
        {
            new WorldSlotTransition(WorldSlotHostState.Allocated, WorldSlotHostState.Bootstrapping, "BeginBootstrap"),
            new WorldSlotTransition(WorldSlotHostState.Bootstrapping, WorldSlotHostState.NativeReady, "NativeLoaded"),
            new WorldSlotTransition(WorldSlotHostState.NativeReady, WorldSlotHostState.ManagedReady, "ManagedLoaded"),
            new WorldSlotTransition(WorldSlotHostState.ManagedReady, WorldSlotHostState.LoadingSession, "LoadSession"),
            new WorldSlotTransition(WorldSlotHostState.LoadingSession, WorldSlotHostState.Running, "SessionLoaded"),
            new WorldSlotTransition(WorldSlotHostState.Running, WorldSlotHostState.Quiescing, "Quiesce"),
            new WorldSlotTransition(WorldSlotHostState.Quiescing, WorldSlotHostState.Running, "Resume"),
            new WorldSlotTransition(WorldSlotHostState.Quiescing, WorldSlotHostState.Snapshotting, "BeginSnapshot"),
            new WorldSlotTransition(WorldSlotHostState.Quiescing, WorldSlotHostState.Reloading, "BeginReload"),
            new WorldSlotTransition(WorldSlotHostState.Quiescing, WorldSlotHostState.Migrating, "BeginMigrate"),
            new WorldSlotTransition(WorldSlotHostState.Snapshotting, WorldSlotHostState.Quiescing, "SnapshotComplete"),
            new WorldSlotTransition(WorldSlotHostState.Reloading, WorldSlotHostState.Quiescing, "ReloadComplete"),
            new WorldSlotTransition(WorldSlotHostState.Migrating, WorldSlotHostState.Stopping, "MigrationHandedOff"),
            new WorldSlotTransition(WorldSlotHostState.Quiescing, WorldSlotHostState.Stopping, "Stop"),
            new WorldSlotTransition(WorldSlotHostState.Stopping, WorldSlotHostState.Destroyed, "TeardownComplete"),
        };

    public static AnyActiveToRule AnyActiveToFaulted { get; } =
        new(WorldSlotHostState.Faulted);

    public static WorldSlotHostState InitialState => WorldSlotHostState.Allocated;

    public static bool IsTerminal(WorldSlotHostState state)
        => state is WorldSlotHostState.Destroyed or WorldSlotHostState.Faulted;

    public static bool CanFailStop(WorldSlotHostState state)
        => AnyActiveToFaulted.AppliesTo(state);

    public static bool TryGetForward(
        WorldSlotHostState from,
        WorldSlotHostState to,
        out WorldSlotTransition transition)
    {
        foreach (var candidate in ForwardTransitions)
        {
            if (candidate.From == from && candidate.To == to)
            {
                transition = candidate;
                return true;
            }
        }

        transition = default;
        return false;
    }
}

/// <summary>
/// Short projection name for callers that consume the aggregate transition tables.
/// It is an alias over <see cref="WorldSlotStateMachine"/>, not a second state model.
/// </summary>
public static class WorldSlotTransitions
{
    public static IReadOnlyList<WorldSlotTransition> ForwardTransitions
        => WorldSlotStateMachine.ForwardTransitions;

    public static AnyActiveToRule AnyActiveToFaulted
        => WorldSlotStateMachine.AnyActiveToFaulted;

    public static IReadOnlyList<WorldSlotTransition> Transitions
        => WorldSlotStateMachine.ForwardTransitions;
}
