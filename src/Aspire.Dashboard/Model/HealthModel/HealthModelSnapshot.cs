// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;

namespace Aspire.Dashboard.Model.HealthModel;

/// <summary>
/// An entity with its evaluated health state and its place in the model hierarchy.
/// </summary>
public sealed class HealthModelNode
{
    /// <summary>The entity this node was evaluated from.</summary>
    public required HealthModelEntity Entity { get; init; }

    /// <summary>The final state of the entity, combining its own signals with its dependencies.</summary>
    public required HealthState State { get; init; }

    /// <summary>
    /// The worst state across the entity's own signals, or <see cref="HealthState.Unknown"/> when it has none.
    /// Surfaced separately so the UI can show why an entity is unhealthy.
    /// </summary>
    public required HealthState SignalsState { get; init; }

    /// <summary>
    /// The state contributed by the entity's children after aggregation, or <see langword="null"/> when the
    /// entity has no children.
    /// </summary>
    public required HealthState? DependenciesState { get; init; }

    /// <summary>The entity's children, already evaluated.</summary>
    public required ImmutableArray<HealthModelNode> Children { get; init; }

    /// <summary>The distance from the root entity. The root itself is zero.</summary>
    public required int Depth { get; init; }

    /// <summary>The unique name of the entity.</summary>
    public string Name => Entity.Name;

    /// <summary>The name to show in the UI.</summary>
    public string DisplayName => Entity.DisplayName ?? Entity.Name;
}

/// <summary>
/// A fully evaluated health model, ready to render.
/// </summary>
public sealed class HealthModelSnapshot
{
    /// <summary>An empty model, used before the first resource snapshot arrives.</summary>
    public static HealthModelSnapshot Empty { get; } = new()
    {
        Definition = new HealthModelDefinition { Name = "empty" },
        Root = null,
        AllNodes = []
    };

    /// <summary>The definition this snapshot was evaluated from.</summary>
    public required HealthModelDefinition Definition { get; init; }

    /// <summary>The root entity of the model, or <see langword="null"/> when the model has no entities.</summary>
    public required HealthModelNode? Root { get; init; }

    /// <summary>
    /// Every node in depth-first order. The UI renders the hierarchy as an indented flat list, so this
    /// ordering is the render order.
    /// </summary>
    public required ImmutableArray<HealthModelNode> AllNodes { get; init; }

    /// <summary>The overall state of the model.</summary>
    public HealthState State => Root?.State ?? HealthState.Unknown;
}
