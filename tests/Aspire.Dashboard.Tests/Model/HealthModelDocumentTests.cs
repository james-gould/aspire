// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Model.HealthModel;
using Aspire.Tests.Shared.DashboardModel;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using VerifyXunit;
using Xunit;

namespace Aspire.Dashboard.Tests.Model;

public class HealthModelDocumentTests
{
    [Fact]
    public Task ExportedDocumentContainsOnlyDefinition()
    {
        var definition = CreateModel("api-random");
        var document = HealthModelDocuments.Create(definition, "SampleApp");

        return Verifier.Verify(HealthModelDocuments.Serialize(document), "json").UseDirectory("Snapshots");
    }

    [Fact]
    public void DocumentRoundTripPreservesExactPositionsAndPropagation()
    {
        var model = CreateModel("api-runtime-one");
        var original = HealthModelDocuments.Create(model, "SampleApp");
        var api = original.Entities.Single(e => e.AspireResourceName == "api");
        var changed = api with
        {
            CanvasPosition = new HealthModelCanvasPosition(-712.25, 318.5),
            Impact = EntityImpact.Limited,
            HealthObjective = 99.9,
            Dependencies = new DependenciesAggregation
            {
                AggregationType = DependenciesAggregationType.MinHealthy,
                Unit = AggregationUnit.Percentage,
                UnhealthyThreshold = 40,
                DegradedThreshold = 80
            }
        };
        original = original with { Entities = [.. original.Entities.Select(e => e.Name == api.Name ? changed : e)] };

        var imported = HealthModelDocuments.Deserialize(HealthModelDocuments.Serialize(original), original);
        var restarted = HealthModelDocuments.Reconcile(imported, CreateModel("api-runtime-two"));
        var rebound = restarted.Entities.Single(e => e.AspireResourceName == "api");

        Assert.Equal(changed.CanvasPosition, rebound.CanvasPosition);
        Assert.Equal(changed.Impact, rebound.Impact);
        Assert.Equal(changed.HealthObjective, rebound.HealthObjective);
        Assert.Equal(changed.Dependencies, rebound.Dependencies);
        Assert.Equal(original.Relationships, restarted.Relationships);
        Assert.Equal(changed.Name, rebound.Name);
    }

    [Fact]
    public void ImportRejectsAnotherApplicationOrChangedTopology()
    {
        var current = HealthModelDocuments.Create(CreateModel("api"), "SampleApp");
        var otherApp = current with { ApplicationName = "OtherApp" };
        var otherTopology = current with { Relationships = [] };

        Assert.Throws<InvalidDataException>(() => HealthModelDocuments.Deserialize(HealthModelDocuments.Serialize(otherApp), current));
        Assert.Throws<InvalidDataException>(() => HealthModelDocuments.Deserialize(HealthModelDocuments.Serialize(otherTopology), current));
    }

    [Fact]
    public void ImportRejectsUnknownPropertiesRatherThanDroppingData()
    {
        var current = HealthModelDocuments.Create(CreateModel("api"), "SampleApp");
        var json = HealthModelDocuments.Serialize(current).Replace("\"schemaVersion\": 1,", "\"schemaVersion\": 1, \"unsupportedSetting\": true,");

        Assert.Throws<JsonException>(() => HealthModelDocuments.Deserialize(json, current));
    }

    [Fact]
    public void ImportRequiresAnExplicitSchemaVersion()
    {
        var current = HealthModelDocuments.Create(CreateModel("api"), "SampleApp");
        var json = HealthModelDocuments.Serialize(current).Replace("\"schemaVersion\": 1,", string.Empty);
        Assert.Throws<JsonException>(() => HealthModelDocuments.Deserialize(json, current));
    }

    [Fact]
    public void ImportCannotReplaceTheLiveSignalBindings()
    {
        var current = HealthModelDocuments.Create(CreateModel("api"), "SampleApp");
        var edited = current with { Entities = [.. current.Entities.Select(e => e with { LocalSignals = [] })] };
        Assert.Throws<InvalidDataException>(() => HealthModelDocuments.Deserialize(HealthModelDocuments.Serialize(edited), current));
    }

    [Theory]
    [InlineData(double.NaN, 0)]
    [InlineData(0, double.PositiveInfinity)]
    [InlineData(1000001, 0)]
    public void InvalidCoordinatesAreRejected(double x, double y)
    {
        var current = HealthModelDocuments.Create(CreateModel("api"), "SampleApp");
        var invalid = current with
        {
            Entities = [.. current.Entities.Select((e, i) => i == 0 ? e with { CanvasPosition = new(x, y) } : e)]
        };

        Assert.Throws<InvalidDataException>(() => HealthModelDocuments.Validate(invalid, "SampleApp"));
    }

    [Theory]
    [InlineData(DependenciesAggregationType.MaxNotHealthy, 20, 10)]
    [InlineData(DependenciesAggregationType.MinHealthy, 10, 20)]
    public void ThresholdOrderingIsValidated(DependenciesAggregationType type, double degraded, double unhealthy)
    {
        var aggregation = new DependenciesAggregation
        {
            AggregationType = type,
            Unit = AggregationUnit.Percentage,
            DegradedThreshold = degraded,
            UnhealthyThreshold = unhealthy
        };
        Assert.Throws<InvalidDataException>(() => HealthModelDocuments.ValidateAggregation(aggregation));
    }

    [Fact]
    public void ArrangeIsIndependentOfResourceOrderAndDoesNotOverlap()
    {
        var model = CreateModel("api");
        var original = HealthModelLayout.Arrange(model);
        var reordered = HealthModelLayout.Arrange(model with
        {
            Entities = [.. model.Entities.Reverse()],
            Relationships = [.. model.Relationships.Reverse()]
        });

        Assert.Equal(original.OrderBy(p => p.Key), reordered.OrderBy(p => p.Key));
        foreach (var first in original)
        {
            foreach (var second in original.Where(pair => pair.Key != first.Key))
            {
                Assert.True(Math.Abs(first.Value.X - second.Value.X) >= HealthModelLayout.CardWidth ||
                    Math.Abs(first.Value.Y - second.Value.Y) >= HealthModelLayout.CardHeight);
            }
        }
    }

    [Fact]
    public void DropOnAnotherEntityFindsAFreePositionWithoutMovingTheOtherEntity()
    {
        var document = HealthModelDocuments.Create(CreateModel("api"), "SampleApp");
        var root = document.Entities[0];
        var api = document.Entities.Single(e => e.AspireResourceName == "api");

        var position = HealthModelLayout.Place(document, api.Name, root.CanvasPosition);

        Assert.NotEqual(root.CanvasPosition, position);
        Assert.True(Math.Abs(position.X - root.CanvasPosition.X) >= HealthModelLayout.CardWidth ||
            Math.Abs(position.Y - root.CanvasPosition.Y) >= HealthModelLayout.CardHeight);
        Assert.Equal(root, document.Entities[0]);
    }

    private static HealthModelDefinition CreateModel(string runtimeName) => AspireHealthModelBuilder.Build(
    [
        ModelTestHelpers.CreateResource(runtimeName, displayName: "api", state: KnownResourceState.Running,
            relationships: [new("database", KnownRelationshipTypes.Reference)],
            environment: [new EnvironmentVariableViewModel("SECRET", "not-for-export", fromSpec: true)]),
        ModelTestHelpers.CreateResource("database", displayName: "database", state: KnownResourceState.Running,
            healthReports: [new("ready", HealthStatus.Unhealthy, "private measurement description", "private exception text")])
    ]);
}
