// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;

namespace Aspire.Dashboard.Model.HealthModel;

/// <summary>
/// A node in a health model. Represents either a real resource or a logical component such as a code
/// component, a user flow, or a team.
/// </summary>
/// <remarks>
/// Uses the common configuration concepts of <c>Microsoft.CloudHealth/healthmodels/entities</c>.
/// <see cref="ResourceName"/> identifies a local resource, not an ARM resource ID. A future publisher
/// must supply a deployed resource binding and signal source rather than copying the local name into
/// an Azure resource signal group.
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

    /// <summary>The stable local key used to associate designer settings with a resource replica.</summary>
    public string? ResourceKey { get; init; }

    /// <summary>The AppHost resource name, without the runtime-generated instance suffix.</summary>
    public string? AspireResourceName { get; init; }

    /// <summary>The replica index of the bound AppHost resource.</summary>
    public int? ReplicaIndex { get; init; }

    /// <summary>The type of the Aspire resource this entity was projected from, such as <c>Project</c>.</summary>
    public string? ResourceType { get; init; }

    /// <summary>The name shown in the UI for what this entity represents, such as "Container" or "Service".</summary>
    public string? Category { get; init; }
}

/// <summary>
/// A complete health model: a set of entities and the relationships that connect them.
/// </summary>
/// <remarks>
/// <para>
/// The shape of this type mirrors <c>Microsoft.CloudHealth/healthmodels</c> and its <c>entities</c> and
/// <c>relationships</c> child resources so the model can be translated to Bicep. <see cref="Entities"/> and
/// <see cref="Relationships"/> are kept as flat lists rather than a tree for that reason: the Azure model is
/// a graph in which an entity may have several parents, and relationships are standalone resources.
/// </para>
/// <para>
/// Two pieces are still required before a model can be deployed, and neither can be derived from the local
/// app model: an entity that represents a real Azure resource needs the ARM resource ID of its deployed
/// counterpart, and every data-source signal group needs an <c>authenticationsettings</c> resource to read
/// through. Both arrive with deployment information rather than from the running app host.
/// </para>
/// <para>
/// The initial documented publishing target is the <c>2026-05-01-preview</c> API version.
/// Azure coordinate mapping and execution parity require service-level validation.
/// See https://learn.microsoft.com/azure/azure-monitor/health-models/tutorial-bicep.
/// </para>
/// </remarks>
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
