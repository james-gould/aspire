// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Aspire.Dashboard.Model.HealthModel;
using Aspire.Tests.Shared.DashboardModel;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Xunit;

namespace Aspire.Dashboard.Tests.Model;

public class AspireHealthModelBuilderTests
{
    [Fact]
    public void Build_NoResources_StillProducesLogicalEntities()
    {
        var definition = AspireHealthModelBuilder.Build([]);

        Assert.Collection(definition.Entities,
            e => Assert.Equal(AspireHealthModelBuilder.RootEntityName, e.Name),
            e => Assert.Equal(AspireHealthModelBuilder.ServicesEntityName, e.Name),
            e => Assert.Equal(AspireHealthModelBuilder.InfrastructureEntityName, e.Name));

        Assert.Collection(definition.Relationships,
            r => Assert.Equal(new HealthModelRelationship(AspireHealthModelBuilder.RootEntityName, AspireHealthModelBuilder.ServicesEntityName), r),
            r => Assert.Equal(new HealthModelRelationship(AspireHealthModelBuilder.RootEntityName, AspireHealthModelBuilder.InfrastructureEntityName), r));
    }

    [Fact]
    public void Build_ProjectsAndContainers_AreGroupedUnderDifferentParents()
    {
        var project = ModelTestHelpers.CreateResource(resourceName: "api", resourceType: KnownResourceTypes.Project, state: KnownResourceState.Running);
        var container = ModelTestHelpers.CreateResource(resourceName: "cache", resourceType: KnownResourceTypes.Container, state: KnownResourceState.Running);

        var definition = AspireHealthModelBuilder.Build([project, container]);

        var projectEntityName = AspireHealthModelBuilder.GetEntityName(project);
        var containerEntityName = AspireHealthModelBuilder.GetEntityName(container);

        Assert.Contains(new HealthModelRelationship(AspireHealthModelBuilder.ServicesEntityName, projectEntityName), definition.Relationships);
        Assert.Contains(new HealthModelRelationship(AspireHealthModelBuilder.InfrastructureEntityName, containerEntityName), definition.Relationships);
    }

    [Fact]
    public void Build_ResourceWithoutRuntimeHealth_IsExcluded()
    {
        var parameter = ModelTestHelpers.CreateResource(resourceName: "secret", resourceType: KnownResourceTypes.Parameter, state: KnownResourceState.Running);

        var definition = AspireHealthModelBuilder.Build([parameter]);

        Assert.Collection(definition.Entities,
            e => Assert.Equal(AspireHealthModelBuilder.RootEntityName, e.Name),
            e => Assert.Equal(AspireHealthModelBuilder.ServicesEntityName, e.Name),
            e => Assert.Equal(AspireHealthModelBuilder.InfrastructureEntityName, e.Name));
    }

    [Fact]
    public void Build_HiddenResource_IsExcluded()
    {
        var hidden = ModelTestHelpers.CreateResource(resourceName: "hidden", resourceType: KnownResourceTypes.Container, hidden: true);

        var definition = AspireHealthModelBuilder.Build([hidden]);

        Assert.Collection(definition.Entities,
            e => Assert.Equal(AspireHealthModelBuilder.RootEntityName, e.Name),
            e => Assert.Equal(AspireHealthModelBuilder.ServicesEntityName, e.Name),
            e => Assert.Equal(AspireHealthModelBuilder.InfrastructureEntityName, e.Name));
    }

    [Fact]
    public void Build_ResourceEntity_HasStateSignalAndOneSignalPerHealthReport()
    {
        var resource = ModelTestHelpers.CreateResource(
            resourceName: "api",
            resourceType: KnownResourceTypes.Project,
            state: KnownResourceState.Running,
            healthReports:
            [
                new HealthReportViewModel("live", HealthStatus.Healthy, "All good", null),
                new HealthReportViewModel("ready", HealthStatus.Degraded, "Warming up", null)
            ]);

        var definition = AspireHealthModelBuilder.Build([resource]);
        var entity = Assert.Single(definition.Entities, e => e.ResourceName == "api");

        Assert.Collection(entity.Signals,
            s =>
            {
                Assert.Equal(AspireHealthModelBuilder.ResourceStateSignalName, s.Name);
                Assert.Equal(HealthState.Healthy, s.State);
            },
            s =>
            {
                Assert.Equal("live", s.Name);
                Assert.Equal(HealthState.Healthy, s.State);
                Assert.Equal("All good", s.Description);
            },
            s =>
            {
                Assert.Equal("ready", s.Name);
                Assert.Equal(HealthState.Degraded, s.State);
                Assert.Equal("Warming up", s.Description);
            });
    }

    [Fact]
    public void Build_EntityName_IsStableAcrossAppHostRestarts()
    {
        // Resource names carry a random suffix that changes on every app host restart, so entity identity
        // must come from the persistent key instead.
        var first = ModelTestHelpers.CreateResource(resourceName: "api-abcdefgh", displayName: "api", resourceType: KnownResourceTypes.Project);
        var second = ModelTestHelpers.CreateResource(resourceName: "api-ijklmnop", displayName: "api", resourceType: KnownResourceTypes.Project);

        Assert.Equal(AspireHealthModelBuilder.GetEntityName(first), AspireHealthModelBuilder.GetEntityName(second));
    }

    [Theory]
    [InlineData(KnownResourceState.Running, HealthState.Healthy)]
    [InlineData(KnownResourceState.Finished, HealthState.Healthy)]
    [InlineData(KnownResourceState.FailedToStart, HealthState.Unhealthy)]
    [InlineData(KnownResourceState.Exited, HealthState.Unhealthy)]
    [InlineData(KnownResourceState.RuntimeUnhealthy, HealthState.Unhealthy)]
    [InlineData(KnownResourceState.ValueMissing, HealthState.Unhealthy)]
    [InlineData(KnownResourceState.Starting, HealthState.Unknown)]
    [InlineData(KnownResourceState.Waiting, HealthState.Unknown)]
    [InlineData(KnownResourceState.Stopping, HealthState.Unknown)]
    public void MapResourceState_MapsLifecycleStates(KnownResourceState state, HealthState expected)
    {
        Assert.Equal(expected, AspireHealthModelBuilder.MapResourceState(state));
    }

    [Fact]
    public void MapResourceState_NullState_IsUnknown()
    {
        Assert.Equal(HealthState.Unknown, AspireHealthModelBuilder.MapResourceState(null));
    }

    [Theory]
    [InlineData(HealthStatus.Healthy, HealthState.Healthy)]
    [InlineData(HealthStatus.Degraded, HealthState.Degraded)]
    [InlineData(HealthStatus.Unhealthy, HealthState.Unhealthy)]
    public void MapHealthStatus_MapsHealthCheckResults(HealthStatus status, HealthState expected)
    {
        Assert.Equal(expected, AspireHealthModelBuilder.MapHealthStatus(status));
    }

    [Fact]
    public void BuildAndEvaluate_UnhealthyProject_FailsApplication()
    {
        var project = ModelTestHelpers.CreateResource(resourceName: "api", resourceType: KnownResourceTypes.Project, state: KnownResourceState.FailedToStart);

        var snapshot = HealthModelEvaluator.Evaluate(AspireHealthModelBuilder.Build([project]));

        Assert.Equal(HealthState.Unhealthy, snapshot.State);
    }

    [Fact]
    public void BuildAndEvaluate_AllResourcesRunning_IsHealthy()
    {
        var project = ModelTestHelpers.CreateResource(resourceName: "api", resourceType: KnownResourceTypes.Project, state: KnownResourceState.Running);
        var container = ModelTestHelpers.CreateResource(resourceName: "cache", resourceType: KnownResourceTypes.Container, state: KnownResourceState.Running);

        var snapshot = HealthModelEvaluator.Evaluate(AspireHealthModelBuilder.Build([project, container]));

        Assert.Equal(HealthState.Healthy, snapshot.State);
    }

    [Fact]
    public void BuildAndEvaluate_StartingResource_LeavesApplicationUnknownRatherThanUnhealthy()
    {
        var project = ModelTestHelpers.CreateResource(resourceName: "api", resourceType: KnownResourceTypes.Project, state: KnownResourceState.Starting);

        var snapshot = HealthModelEvaluator.Evaluate(AspireHealthModelBuilder.Build([project]));

        Assert.Equal(HealthState.Unknown, snapshot.State);
    }
}
