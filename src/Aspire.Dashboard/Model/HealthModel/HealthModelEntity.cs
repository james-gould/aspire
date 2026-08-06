// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;

namespace Aspire.Dashboard.Model.HealthModel;

/// <summary>
/// A node in a health model. Represents either a real resource or a logical component such as a code
/// component, a user flow, or a team.
/// </summary>
/// <remarks>
/// Mirrors <c>Microsoft.CloudHealth/healthmodels/entities</c>. Azure has no entity kind discriminator, so
/// whether an entity represents a resource is determined structurally by whether <see cref="ResourceName"/>
/// is set. On translation that becomes the presence of an <c>azureResource</c> signal group.
/// </remarks>
public sealed record HealthModelEntity
{
    /// <summary>
    /// The name of the entity. Must be unique within the model and is the key used by relationships.
    /// </summary>
    /// <remarks>
    /// Azure constrains entity names to <c>^[a-zA-Z0-9][a-zA-Z0-9-]{1,258}[a-zA-Z0-9]$</c>. Names are not
    /// validated here because the local model has no such restriction, but keeping to that shape avoids
    /// having to rewrite names when the model is translated to Bicep.
    /// </remarks>
    public required string Name { get; init; }

    /// <summary>The name shown in the UI. Falls back to <see cref="Name"/> when not set.</summary>
    public string? DisplayName { get; init; }

    /// <summary>How much of this entity's state is propagated to its parents.</summary>
    public EntityImpact Impact { get; init; } = EntityImpact.Standard;

    /// <summary>
    /// The percentage of time the entity is expected to be healthy, between 0 and 100. Informational only
    /// in the local model; it maps to <c>healthObjective</c> on translation.
    /// </summary>
    public double? HealthObjective { get; init; }

    /// <summary>How this entity combines the health states of its children.</summary>
    public DependenciesAggregation Dependencies { get; init; } = DependenciesAggregation.WorstOf;

    /// <summary>The signals that determine this entity's own state, before dependencies are considered.</summary>
    public ImmutableArray<HealthModelSignal> Signals { get; init; } = [];

    /// <summary>The name of the Aspire resource this entity was projected from, when it represents one.</summary>
    public string? ResourceName { get; init; }

    /// <summary>The type of the Aspire resource this entity was projected from, such as <c>Project</c>.</summary>
    public string? ResourceType { get; init; }

    /// <summary>The name shown in the UI for what this entity represents, such as "Container" or "Service".</summary>
    public string? Category { get; init; }
}

/// <summary>
/// A directed parent-to-child edge in a health model.
/// </summary>
/// <remarks>
/// Azure models relationships as standalone resources with immutable <c>parentEntityName</c> and
/// <c>childEntityName</c>, and carries no health or aggregation configuration on the edge itself. Rollup
/// tuning lives on the two entities instead: <see cref="HealthModelEntity.Impact"/> on the child and
/// <see cref="HealthModelEntity.Dependencies"/> on the parent.
/// </remarks>
/// <param name="ParentEntityName">The name of the parent entity.</param>
/// <param name="ChildEntityName">The name of the child entity.</param>
public sealed record HealthModelRelationship(string ParentEntityName, string ChildEntityName);

/// <summary>
/// A complete health model: a set of entities and the relationships that connect them.
/// </summary>
public sealed record HealthModelDefinition
{
    /// <summary>
    /// The name of the model. This is also the name of the root entity, matching the Azure behaviour where
    /// the root entity is created automatically with the same name as the health model.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>The name shown in the UI. Falls back to <see cref="Name"/> when not set.</summary>
    public string? DisplayName { get; init; }

    /// <summary>All entities in the model, including the root entity.</summary>
    public ImmutableArray<HealthModelEntity> Entities { get; init; } = [];

    /// <summary>The parent-to-child edges connecting <see cref="Entities"/>.</summary>
    public ImmutableArray<HealthModelRelationship> Relationships { get; init; } = [];
}
