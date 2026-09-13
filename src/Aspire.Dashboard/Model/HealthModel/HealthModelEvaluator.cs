// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;

namespace Aspire.Dashboard.Model.HealthModel;

/// <summary>
/// Evaluates a <see cref="HealthModelDefinition"/> into a <see cref="HealthModelSnapshot"/> by resolving
/// each entity's signals and rolling child health up through the model.
/// </summary>
/// <remarks>
/// The rollup reproduces the Azure Monitor pipeline: each signal is evaluated to a state, the entity takes
/// the worst state across its own signals, each child's state is rewritten by its own
/// <see cref="EntityImpact"/>, those results are combined using the parent's
/// <see cref="DependenciesAggregation"/>, and finally the entity's own state and its aggregated dependency
/// state are combined worst-of.
/// See https://learn.microsoft.com/azure/azure-monitor/health-models/rollup.
/// </remarks>
public static class HealthModelEvaluator
{
    /// <summary>Evaluates a model definition.</summary>
    public static HealthModelSnapshot Evaluate(HealthModelDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        if (definition.Entities.Length == 0)
        {
            return new HealthModelSnapshot { Definition = definition, Root = null, AllNodes = [] };
        }

        var topology = HealthModelTopology.Create(definition);
        var nodes = new Dictionary<string, HealthModelNode>(StringComparer.Ordinal);
        // Evaluate each entity once, from leaves up. Shared dependencies must not produce duplicate
        // rows (or duplicate DOM keys) when the same entity is reached through several parents.
        foreach (var entity in topology.Order.Reverse())
        {
            var children = topology.Children[entity.Name].Select(n => nodes[n]).ToImmutableArray();
            var signalsState = HealthStateExtensions.WorstOf(entity.Signals.Select(s => s.State));
            HealthState? dependenciesState = children.Length == 0
                ? null
                : AggregateDependencies(entity.Dependencies, children);
            var state = HealthStateExtensions.WorstOf(signalsState, dependenciesState ?? HealthState.Unknown);

            nodes.Add(entity.Name, new HealthModelNode
            {
                Entity = entity,
                State = state,
                SignalsState = signalsState,
                DependenciesState = dependenciesState,
                Children = children,
                Depth = topology.Depth[entity.Name]
            });
        }

        var root = nodes.GetValueOrDefault(definition.Name) ?? nodes[topology.Order[0].Name];
        var allNodes = ImmutableArray.CreateBuilder<HealthModelNode>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<HealthModelNode>([root]);
        while (pending.TryPop(out var node))
        {
            if (visited.Add(node.Name))
            {
                allNodes.Add(node);
                foreach (var child in node.Children.Reverse())
                {
                    pending.Push(child);
                }
            }
        }
        foreach (var entity in topology.Order.Where(e => !visited.Contains(e.Name)))
        {
            allNodes.Add(nodes[entity.Name]);
        }

        return new HealthModelSnapshot { Definition = definition, Root = root, AllNodes = allNodes.ToImmutable() };
    }

    /// <summary>
    /// Combines the states of an entity's children into the single state they contribute to their parent.
    /// </summary>
    internal static HealthState AggregateDependencies(DependenciesAggregation aggregation, ImmutableArray<HealthModelNode> children)
    {
        // Each child's state is first rewritten by its own impact, then fed into the parent's aggregation.
        var memberStates = children.Select(c => c.State.ApplyImpact(c.Entity.Impact));

        if (aggregation.AggregationType == DependenciesAggregationType.WorstOf)
        {
            return HealthStateExtensions.WorstOf(memberStates);
        }

        var members = aggregation.IgnoreUnknown
            ? memberStates.Where(s => s != HealthState.Unknown).ToList()
            : memberStates.ToList();

        if (members.Count == 0)
        {
            return HealthState.Unknown;
        }

        var healthyCount = members.Count(s => s == HealthState.Healthy);

        // MinHealthy counts what is working, MaxNotHealthy counts what is broken. The two therefore breach
        // in opposite directions, which is handled by IsBreached below.
        var measured = aggregation.AggregationType == DependenciesAggregationType.MinHealthy
            ? healthyCount
            : members.Count - healthyCount;

        var value = aggregation.Unit == AggregationUnit.Percentage
            ? measured * 100d / members.Count
            : measured;

        if (aggregation.UnhealthyThreshold is { } unhealthyThreshold && IsBreached(value, unhealthyThreshold))
        {
            return HealthState.Unhealthy;
        }

        if (aggregation.DegradedThreshold is { } degradedThreshold && IsBreached(value, degradedThreshold))
        {
            return HealthState.Degraded;
        }

        return HealthState.Healthy;

        bool IsBreached(double measuredValue, double threshold) => aggregation.AggregationType switch
        {
            // "At least N children must be healthy" breaches once the healthy count falls to or below N.
            DependenciesAggregationType.MinHealthy => measuredValue <= threshold,
            // "No more than N children may be unhealthy" breaches once the not-healthy count reaches N.
            DependenciesAggregationType.MaxNotHealthy => measuredValue >= threshold,
            _ => false
        };
    }

}
