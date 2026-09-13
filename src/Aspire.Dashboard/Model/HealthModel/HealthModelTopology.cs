// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;

namespace Aspire.Dashboard.Model.HealthModel;

internal sealed class HealthModelTopology
{
    public required ImmutableArray<HealthModelEntity> Order { get; init; }
    public required ILookup<string, string> Children { get; init; }
    public required Dictionary<string, int> Depth { get; init; }

    public static HealthModelTopology Create(HealthModelDefinition definition)
    {
        var entities = definition.Entities.ToDictionary(e => e.Name, StringComparer.Ordinal);
        var incoming = entities.Keys.ToDictionary(n => n, _ => 0, StringComparer.Ordinal);
        var depth = entities.Keys.ToDictionary(n => n, _ => 0, StringComparer.Ordinal);
        var children = definition.Relationships.ToLookup(r => r.ParentEntityName, r => r.ChildEntityName, StringComparer.Ordinal);
        var seen = new HashSet<HealthModelRelationship>();
        foreach (var relationship in definition.Relationships)
        {
            if (!entities.ContainsKey(relationship.ParentEntityName) ||
                !entities.ContainsKey(relationship.ChildEntityName) || !seen.Add(relationship))
            {
                throw new InvalidDataException("The health model contains a dangling or duplicate relationship.");
            }
            incoming[relationship.ChildEntityName]++;
        }

        var ready = new Queue<string>(definition.Entities.Where(e => incoming[e.Name] == 0).Select(e => e.Name));
        var order = ImmutableArray.CreateBuilder<HealthModelEntity>();
        while (ready.TryDequeue(out var name))
        {
            order.Add(entities[name]);
            foreach (var child in children[name])
            {
                depth[child] = Math.Max(depth[child], depth[name] + 1);
                if (--incoming[child] == 0)
                {
                    ready.Enqueue(child);
                }
            }
        }

        if (order.Count != entities.Count)
        {
            // A cyclic AppHost reference graph can still be inspected in Resources, but cannot form
            // a health-model hierarchy with unambiguous parent-to-child propagation.
            throw new InvalidDataException("Health model relationships must not contain a cycle.");
        }

        return new HealthModelTopology { Order = order.ToImmutable(), Children = children, Depth = depth };
    }
}
