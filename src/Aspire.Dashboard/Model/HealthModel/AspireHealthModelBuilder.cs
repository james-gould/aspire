// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using System.Globalization;
using System.IO.Hashing;
using System.Text;
using Aspire.Dashboard.Model.ResourceGraph;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Aspire.Dashboard.Model.HealthModel;

/// <summary>
/// Projects the live Aspire application model into a <see cref="HealthModelDefinition"/>.
/// </summary>
/// <remarks>
/// Uses the same AppHost relationships as the resource graph. All entities have standard impact and
/// worst-of rollup initially; the designer can explicitly change those settings.
/// </remarks>
public static class AspireHealthModelBuilder
{
    /// <summary>The name of the health model, which is also the name of its root entity.</summary>
    public const string RootEntityName = "aspire-app-health";

    /// <summary>The name of the signal projected from a resource's lifecycle state.</summary>
    public const string ResourceStateSignalName = "resource-state";

    /// <summary>
    /// Builds the dependency model from a set of resources.
    /// </summary>
    /// <param name="resources">The resources currently known to the dashboard.</param>
    public static HealthModelDefinition Build(IEnumerable<ResourceViewModel> resources)
    {
        ArgumentNullException.ThrowIfNull(resources);

        var entities = ImmutableArray.CreateBuilder<HealthModelEntity>();
        var relationships = ImmutableArray.CreateBuilder<HealthModelRelationship>();
        var modelResources = resources
            .Where(r => !r.IsResourceHidden(showHiddenResources: false))
            .Where(r => r.ResourceType is not (KnownResourceTypes.Parameter or KnownResourceTypes.ConnectionString))
            .OrderBy(r => r.PersistentKey, StringComparers.ResourceName)
            .ToArray();
        var entityNames = modelResources.ToDictionary(r => r.Name, GetEntityName, StringComparers.ResourceName);

        entities.Add(new HealthModelEntity
        {
            Name = RootEntityName,
            DisplayName = "AppHost",
            Category = "AppHost",
            Dependencies = DependenciesAggregation.WorstOf
        });

        foreach (var resource in modelResources)
        {
            entities.Add(CreateResourceEntity(resource));
        }

        var edges = ResourceGraphHealth.BuildEdges(modelResources, showHiddenResources: false);
        foreach (var name in ResourceGraphHealth.GetRootNames(modelResources, edges))
        {
            relationships.Add(new HealthModelRelationship(RootEntityName, entityNames[name]));
        }
        foreach (var edge in edges)
        {
            relationships.Add(new HealthModelRelationship(entityNames[edge.ParentName], entityNames[edge.ChildName]));
        }

        return new HealthModelDefinition
        {
            Name = RootEntityName,
            DisplayName = "Application health",
            Entities = entities.ToImmutable(),
            Relationships = [.. relationships.OrderBy(r => r.ParentEntityName, StringComparer.Ordinal).ThenBy(r => r.ChildEntityName, StringComparer.Ordinal)]
        };
    }

    /// <summary>
    /// Gets the entity name used for a resource.
    /// </summary>
    /// <remarks>
    /// Uses the resource's persistent key rather than its name because the name includes a randomly
    /// generated suffix that changes every time the app host restarts, which would churn entity identity
    /// across restarts and break any deployed model that references it.
    /// </remarks>
    public static string GetEntityName(ResourceViewModel resource)
    {
        ArgumentNullException.ThrowIfNull(resource);

        // Preserve identity across runtime suffix changes and use only Azure-valid characters. The
        // non-cryptographic suffix distinguishes display names that normalize to the same readable slug.
        var slug = new string(resource.DisplayName.ToLowerInvariant()
            .Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').Take(48).ToArray()).Trim('-');
        var hash = XxHash3.HashToUInt64(Encoding.UTF8.GetBytes(resource.PersistentKey.ToLowerInvariant()));
        return $"resource-{slug}-{hash.ToString("x16", CultureInfo.InvariantCulture)}";
    }

    private static HealthModelEntity CreateResourceEntity(ResourceViewModel resource)
    {
        var signals = ImmutableArray.CreateBuilder<HealthModelSignal>(resource.HealthReports.Length + 1);

        signals.Add(new HealthModelSignal
        {
            Name = ResourceStateSignalName,
            DisplayName = "Resource state",
            Kind = SignalKind.External,
            ReportedState = MapResourceState(resource.KnownState),
            Description = resource.State
        });

        foreach (var report in resource.HealthReports)
        {
            signals.Add(new HealthModelSignal
            {
                Name = report.Name,
                DisplayName = report.Name,
                Kind = SignalKind.External,
                ReportedState = MapHealthStatus(report.HealthStatus),
                Description = report.Description ?? report.ExceptionText
            });
        }

        return new HealthModelEntity
        {
            Name = GetEntityName(resource),
            DisplayName = resource.DisplayName,
            Category = resource.ResourceType,
            ResourceName = resource.Name,
            ResourceKey = resource.PersistentKey,
            AspireResourceName = resource.DisplayName,
            ReplicaIndex = resource.ReplicaIndex,
            ResourceType = resource.ResourceType,
            Signals = signals.ToImmutable()
        };
    }

    /// <summary>
    /// Maps an Aspire resource lifecycle state to a health state.
    /// </summary>
    /// <remarks>
    /// Transient states map to <see cref="HealthState.Unknown"/> rather than to a non-healthy state. Because
    /// unknown is the least severe state under a worst-of rollup, a service that is still starting does not
    /// make the whole application look broken.
    /// </remarks>
    internal static HealthState MapResourceState(KnownResourceState? state) => state switch
    {
        KnownResourceState.Running or KnownResourceState.Finished => HealthState.Healthy,

        KnownResourceState.FailedToStart
            or KnownResourceState.Exited
            or KnownResourceState.RuntimeUnhealthy
            or KnownResourceState.ValueMissing => HealthState.Unhealthy,

        _ => HealthState.Unknown
    };

    /// <summary>Maps a health check result to a health state.</summary>
    internal static HealthState MapHealthStatus(HealthStatus? status) => status switch
    {
        HealthStatus.Healthy => HealthState.Healthy,
        HealthStatus.Degraded => HealthState.Degraded,
        HealthStatus.Unhealthy => HealthState.Unhealthy,
        _ => HealthState.Unknown
    };
}
