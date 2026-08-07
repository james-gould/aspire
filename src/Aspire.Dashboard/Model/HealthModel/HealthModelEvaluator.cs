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

        var entitiesByName = definition.Entities.ToDictionary(e => e.Name, StringComparer.Ordinal);

        var childNamesByParent = definition.Relationships
            .GroupBy(r => r.ParentEntityName, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(r => r.ChildEntityName).ToArray(), StringComparer.Ordinal);

        // The root is the entity named after the model, matching the Azure convention where the root entity
        // is created automatically using the health model's own name. Fall back to any entity that is never
        // a child so a hand-built model without that convention still renders.
        var root = entitiesByName.TryGetValue(definition.Name, out var namedRoot)
            ? namedRoot
            : FindImplicitRoot(definition);

        if (root is null)
        {
            return new HealthModelSnapshot { Definition = definition, Root = null, AllNodes = [] };
        }

        var allNodes = ImmutableArray.CreateBuilder<HealthModelNode>();

        // Entities can legitimately have multiple parents, so an entity may be visited more than once.
        // The visiting set only guards against cycles on the current path, which would otherwise recurse forever.
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var rootNode = EvaluateEntity(root, depth: 0);

        return new HealthModelSnapshot
        {
            Definition = definition,
            Root = rootNode,
            AllNodes = allNodes.ToImmutable()
        };

        HealthModelNode EvaluateEntity(HealthModelEntity entity, int depth)
        {
            // Reserve this node's slot before recursing so children are appended after their parent and the
            // flattened list comes out in depth-first render order.
            var nodeIndex = allNodes.Count;
            allNodes.Add(null!);

            var children = ImmutableArray<HealthModelNode>.Empty;

            if (childNamesByParent.TryGetValue(entity.Name, out var childNames) && visiting.Add(entity.Name))
            {
                try
                {
                    var builder = ImmutableArray.CreateBuilder<HealthModelNode>(childNames.Length);
                    foreach (var childName in childNames)
                    {
                        if (entitiesByName.TryGetValue(childName, out var child) && !visiting.Contains(childName))
                        {
                            builder.Add(EvaluateEntity(child, depth + 1));
                        }
                    }

                    children = builder.ToImmutable();
                }
                finally
                {
                    visiting.Remove(entity.Name);
                }
            }

            var signalsState = entity.Signals.Length == 0
                ? HealthState.Unknown
                : HealthStateExtensions.WorstOf(entity.Signals.Select(s => s.State));

            HealthState? dependenciesState = children.Length == 0
                ? null
                : AggregateDependencies(entity.Dependencies, children);

            // Unknown is the least severe state, so an entity with no signals simply inherits its dependency
            // state and an entity with no children is driven entirely by its own signals. No special casing needed.
            var state = HealthStateExtensions.WorstOf(signalsState, dependenciesState ?? HealthState.Unknown);

            var node = new HealthModelNode
            {
                Entity = entity,
                State = state,
                SignalsState = signalsState,
                DependenciesState = dependenciesState,
                Children = children,
                Depth = depth
            };

            allNodes[nodeIndex] = node;
            return node;
        }
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

    private static HealthModelEntity? FindImplicitRoot(HealthModelDefinition definition)
    {
        var childNames = definition.Relationships.Select(r => r.ChildEntityName).ToHashSet(StringComparer.Ordinal);

        foreach (var entity in definition.Entities)
        {
            if (!childNames.Contains(entity.Name))
            {
                return entity;
            }
        }

        // Every entity is a child of something, which means the model is a cycle. Fall back to the first
        // entity so the UI still renders something rather than failing.
        return definition.Entities.Length > 0 ? definition.Entities[0] : null;
    }
}
