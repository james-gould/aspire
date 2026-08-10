// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Aspire.Dashboard.Model.HealthModel;

/// <summary>
/// Projects the live Aspire application model into a <see cref="HealthModelDefinition"/>.
/// </summary>
/// <remarks>
/// <para>
/// This is the sample model for the MVP. It is deliberately small and declarative so it is easy to change
/// while the shape of the feature settles. It builds two logical entities under the application root:
/// </para>
/// <code>
/// aspire-app-health              (root, worst-of rollup)
///   |- services                  (projects and executables, standard impact)
///   |- infrastructure            (containers, limited impact, tolerates one unhealthy member)
/// </code>
/// <para>
/// The two logical entities exist to exercise the parts of the Azure model that are not obvious: the root
/// uses a plain worst-of rollup, while <c>infrastructure</c> combines a threshold rollup with limited impact
/// so a single broken container degrades the application rather than failing it outright.
/// </para>
/// </remarks>
public static class AspireHealthModelBuilder
{
    /// <summary>The name of the health model, which is also the name of its root entity.</summary>
    public const string RootEntityName = "aspire-app-health";

    /// <summary>The logical entity that groups projects and executables.</summary>
    public const string ServicesEntityName = "services";

    /// <summary>The logical entity that groups containers.</summary>
    public const string InfrastructureEntityName = "infrastructure";

    /// <summary>The name of the signal projected from a resource's lifecycle state.</summary>
    public const string ResourceStateSignalName = "resource-state";

    /// <summary>
    /// Builds the sample model from a set of resources.
    /// </summary>
    /// <param name="resources">The resources currently known to the dashboard.</param>
    public static HealthModelDefinition Build(IEnumerable<ResourceViewModel> resources)
    {
        ArgumentNullException.ThrowIfNull(resources);

        var entities = ImmutableArray.CreateBuilder<HealthModelEntity>();
        var relationships = ImmutableArray.CreateBuilder<HealthModelRelationship>();

        entities.Add(new HealthModelEntity
        {
            Name = RootEntityName,
            DisplayName = "Application",
            Category = "Application",
            Dependencies = DependenciesAggregation.WorstOf
        });

        entities.Add(new HealthModelEntity
        {
            Name = ServicesEntityName,
            DisplayName = "Services",
            Category = "Logical component",
            Dependencies = DependenciesAggregation.WorstOf
        });

        entities.Add(new HealthModelEntity
        {
            Name = InfrastructureEntityName,
            DisplayName = "Infrastructure",
            Category = "Logical component",

            // Infrastructure is backing services rather than the app itself, so a total failure here is
            // reported to the application as degraded rather than unhealthy.
            Impact = EntityImpact.Limited,

            // Tolerate a single unhealthy container before the group itself reports a problem. This is the
            // "4 VMs, tolerate 1 offline" pattern from the Azure docs expressed as a not-healthy limit.
            Dependencies = new DependenciesAggregation
            {
                AggregationType = DependenciesAggregationType.MaxNotHealthy,
                DegradedThreshold = 1,
                UnhealthyThreshold = 2,
                Unit = AggregationUnit.Absolute
            }
        });

        relationships.Add(new HealthModelRelationship(RootEntityName, ServicesEntityName));
        relationships.Add(new HealthModelRelationship(RootEntityName, InfrastructureEntityName));

        foreach (var resource in resources.OrderBy(r => r.Name, StringComparers.ResourceName))
        {
            if (resource.IsResourceHidden(showHiddenResources: false))
            {
                continue;
            }

            var parentName = GetParentEntityName(resource.ResourceType);
            if (parentName is null)
            {
                continue;
            }

            entities.Add(CreateResourceEntity(resource));
            relationships.Add(new HealthModelRelationship(parentName, GetEntityName(resource)));
        }

        return new HealthModelDefinition
        {
            Name = RootEntityName,
            DisplayName = "Application health",
            Entities = entities.ToImmutable(),
            Relationships = relationships.ToImmutable()
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

        return resource.PersistentKey;
    }

    private static string? GetParentEntityName(string resourceType) => resourceType switch
    {
        // Parameters and connection strings are configuration values resolved at startup. They have no
        // runtime health of their own, so including them would add permanently unknown entities.
        KnownResourceTypes.Parameter or KnownResourceTypes.ConnectionString => null,

        // Containers and external services are things the application depends on rather than the
        // application itself, so they roll up through the limited-impact infrastructure entity.
        KnownResourceTypes.Container
            or KnownResourceTypes.ContainerExec
            or KnownResourceTypes.ExternalService => InfrastructureEntityName,

        // Projects, executables and custom resource types are all treated as application services. Falling
        // through by default rather than listing known types means a custom resource with health checks
        // still appears in the model.
        _ => ServicesEntityName
    };

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
