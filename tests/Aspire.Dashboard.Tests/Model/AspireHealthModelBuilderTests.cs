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
    public void Build_NoResources_StillProducesAppHostRoot()
    {
        var definition = AspireHealthModelBuilder.Build([]);

        Assert.Equal(AspireHealthModelBuilder.RootEntityName, Assert.Single(definition.Entities).Name);
        Assert.Empty(definition.Relationships);
    }

    [Fact]
    public void Build_IndependentResources_AreChildrenOfTheAppHost()
    {
        var project = ModelTestHelpers.CreateResource(resourceName: "api", resourceType: KnownResourceTypes.Project, state: KnownResourceState.Running);
        var container = ModelTestHelpers.CreateResource(resourceName: "cache", resourceType: KnownResourceTypes.Container, state: KnownResourceState.Running);

        var definition = AspireHealthModelBuilder.Build([project, container]);

        var projectEntityName = AspireHealthModelBuilder.GetEntityName(project);
        var containerEntityName = AspireHealthModelBuilder.GetEntityName(container);

        Assert.Contains(new HealthModelRelationship(AspireHealthModelBuilder.RootEntityName, projectEntityName), definition.Relationships);
        Assert.Contains(new HealthModelRelationship(AspireHealthModelBuilder.RootEntityName, containerEntityName), definition.Relationships);
        Assert.All(definition.Entities, entity => Assert.Equal(EntityImpact.Standard, entity.Impact));
    }

    [Fact]
    public void Build_ResourceWithoutRuntimeHealth_IsExcluded()
    {
        var parameter = ModelTestHelpers.CreateResource(resourceName: "secret", resourceType: KnownResourceTypes.Parameter, state: KnownResourceState.Running);
        var connectionString = ModelTestHelpers.CreateResource(resourceName: "conn", resourceType: KnownResourceTypes.ConnectionString, state: KnownResourceState.Running);

        var definition = AspireHealthModelBuilder.Build([parameter, connectionString]);

        Assert.Equal(AspireHealthModelBuilder.RootEntityName, Assert.Single(definition.Entities).Name);
    }

    [Fact]
    public void Build_CustomResourceType_IsIncluded()
    {
        // Custom resource types can carry health checks, so they must appear in the model rather than being
        // dropped because they are not a known type.
        var custom = ModelTestHelpers.CreateResource(resourceName: "widget", resourceType: "Test Resource", state: KnownResourceState.Running);

        var definition = AspireHealthModelBuilder.Build([custom]);

        Assert.Contains(
            new HealthModelRelationship(AspireHealthModelBuilder.RootEntityName, AspireHealthModelBuilder.GetEntityName(custom)),
            definition.Relationships);
    }

    [Fact]
    public void Build_ExternalService_IsIncluded()
    {
        var external = ModelTestHelpers.CreateResource(resourceName: "api-gateway", resourceType: KnownResourceTypes.ExternalService, state: KnownResourceState.Running);

        var definition = AspireHealthModelBuilder.Build([external]);

        Assert.Contains(
            new HealthModelRelationship(AspireHealthModelBuilder.RootEntityName, AspireHealthModelBuilder.GetEntityName(external)),
            definition.Relationships);
    }

    [Fact]
    public void Build_HiddenResource_IsExcluded()
    {
        var hidden = ModelTestHelpers.CreateResource(resourceName: "hidden", resourceType: KnownResourceTypes.Container, hidden: true);

        var definition = AspireHealthModelBuilder.Build([hidden]);

        Assert.Equal(AspireHealthModelBuilder.RootEntityName, Assert.Single(definition.Entities).Name);
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
    public void BuildAndEvaluate_UnhealthyContainer_UsesStandardImpactUnlessConfigured()
    {
        var project = ModelTestHelpers.CreateResource(resourceName: "api", resourceType: KnownResourceTypes.Project, state: KnownResourceState.Running);
        var container = ModelTestHelpers.CreateResource(resourceName: "cache", resourceType: KnownResourceTypes.Container, state: KnownResourceState.FailedToStart);

        var snapshot = HealthModelEvaluator.Evaluate(AspireHealthModelBuilder.Build([project, container]));

        Assert.Equal(HealthState.Unhealthy, snapshot.State);
    }

    [Fact]
    public void Build_UsesTheAppHostDependencyChain_NotResourceTypeBuckets()
    {
        var api = ModelTestHelpers.CreateResource("api", state: KnownResourceState.Running,
            relationships: [new("server", KnownRelationshipTypes.Reference)]);
        var server = ModelTestHelpers.CreateResource("server", state: KnownResourceState.Running);
        var database = ModelTestHelpers.CreateResource("database", state: KnownResourceState.Running,
            relationships: [new("server", KnownRelationshipTypes.Parent)]);
        var model = AspireHealthModelBuilder.Build([database, api, server]);
        var expected = new[]
        {
            new HealthModelRelationship(AspireHealthModelBuilder.RootEntityName, AspireHealthModelBuilder.GetEntityName(api)),
            new HealthModelRelationship(AspireHealthModelBuilder.GetEntityName(api), AspireHealthModelBuilder.GetEntityName(server)),
            new HealthModelRelationship(AspireHealthModelBuilder.GetEntityName(server), AspireHealthModelBuilder.GetEntityName(database))
        };

        Assert.Equal(expected.OrderBy(r => r.ParentEntityName).ThenBy(r => r.ChildEntityName), model.Relationships);
    }

    [Theory]
    [InlineData("api_v1")]
    [InlineData("a.b")]
    [InlineData("应用 service")]
    public void EntityNames_AreStableAndAzureCompatible(string displayName)
    {
        var resource = ModelTestHelpers.CreateResource("runtime-suffix", displayName: displayName);
        var name = AspireHealthModelBuilder.GetEntityName(resource);
        Assert.Matches("^[a-zA-Z0-9][a-zA-Z0-9-]{1,258}[a-zA-Z0-9]$", name);
        Assert.Equal(name, AspireHealthModelBuilder.GetEntityName(ModelTestHelpers.CreateResource("another-runtime-suffix", displayName: displayName)));
    }

    [Fact]
    public void EntityNames_DoNotCollideWhenDisplayNamesNormalizeToTheSameSlug()
    {
        var a = ModelTestHelpers.CreateResource(displayName: "api.v1");
        var b = ModelTestHelpers.CreateResource(displayName: "api_v1");
        Assert.NotEqual(AspireHealthModelBuilder.GetEntityName(a), AspireHealthModelBuilder.GetEntityName(b));
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
