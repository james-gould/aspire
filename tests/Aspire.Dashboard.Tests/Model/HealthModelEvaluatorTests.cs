// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using Aspire.Dashboard.Model.HealthModel;
using Xunit;

namespace Aspire.Dashboard.Tests.Model;

public class HealthModelEvaluatorTests
{
    [Fact]
    public void Evaluate_EntityWithNoSignalsOrChildren_IsUnknown()
    {
        var definition = CreateModel([Entity("root")], []);

        var snapshot = HealthModelEvaluator.Evaluate(definition);

        Assert.Equal(HealthState.Unknown, snapshot.State);
    }

    [Theory]
    [InlineData(HealthState.Healthy, HealthState.Healthy, HealthState.Healthy)]
    [InlineData(HealthState.Healthy, HealthState.Degraded, HealthState.Degraded)]
    [InlineData(HealthState.Degraded, HealthState.Unhealthy, HealthState.Unhealthy)]
    [InlineData(HealthState.Unhealthy, HealthState.Unknown, HealthState.Unhealthy)]
    public void Evaluate_EntitySignals_TakesWorstSignalState(HealthState first, HealthState second, HealthState expected)
    {
        var definition = CreateModel([Entity("root", signals: [Signal("a", first), Signal("b", second)])], []);

        var snapshot = HealthModelEvaluator.Evaluate(definition);

        Assert.Equal(expected, snapshot.State);
    }

    [Fact]
    public void Evaluate_UnknownChild_DoesNotDragParentDown()
    {
        // Unknown is the least severe state in Azure Monitor, so a child that has not reported yet must
        // leave a healthy parent healthy rather than making the whole model look broken.
        var definition = CreateModel(
            [
                Entity("root"),
                Entity("reporting", signals: [Signal("a", HealthState.Healthy)]),
                Entity("silent")
            ],
            [("root", "reporting"), ("root", "silent")]);

        var snapshot = HealthModelEvaluator.Evaluate(definition);

        Assert.Equal(HealthState.Healthy, snapshot.State);
    }

    [Fact]
    public void Evaluate_WorstOfRollup_PropagatesWorstChild()
    {
        var definition = CreateModel(
            [
                Entity("root"),
                Entity("a", signals: [Signal("s", HealthState.Healthy)]),
                Entity("b", signals: [Signal("s", HealthState.Unhealthy)])
            ],
            [("root", "a"), ("root", "b")]);

        var snapshot = HealthModelEvaluator.Evaluate(definition);

        Assert.Equal(HealthState.Unhealthy, snapshot.State);
    }

    [Fact]
    public void Evaluate_EntityCombinesOwnSignalsWithDependencies()
    {
        // The parent's own signal is degraded while its child is unhealthy. The final state is the worst
        // of the two, not just whichever was evaluated last.
        var definition = CreateModel(
            [
                Entity("root", signals: [Signal("s", HealthState.Degraded)]),
                Entity("child", signals: [Signal("s", HealthState.Unhealthy)])
            ],
            [("root", "child")]);

        var snapshot = HealthModelEvaluator.Evaluate(definition);

        Assert.Equal(HealthState.Unhealthy, snapshot.State);
        Assert.Equal(HealthState.Degraded, snapshot.Root!.SignalsState);
        Assert.Equal(HealthState.Unhealthy, snapshot.Root.DependenciesState);
    }

    [Theory]
    [InlineData(EntityImpact.Standard, HealthState.Unhealthy)]
    [InlineData(EntityImpact.Limited, HealthState.Degraded)]
    [InlineData(EntityImpact.Suppressed, HealthState.Healthy)]
    public void Evaluate_ChildImpact_RewritesStateSeenByParent(EntityImpact impact, HealthState expected)
    {
        var definition = CreateModel(
            [
                Entity("root"),
                Entity("child", impact: impact, signals: [Signal("s", HealthState.Unhealthy)])
            ],
            [("root", "child")]);

        var snapshot = HealthModelEvaluator.Evaluate(definition);

        Assert.Equal(expected, snapshot.State);

        // The child itself still reports its true state. Only what the parent sees is rewritten.
        Assert.Equal(HealthState.Unhealthy, snapshot.Root!.Children.Single().State);
    }

    [Fact]
    public void Evaluate_LimitedImpact_SwallowsDegradedEntirely()
    {
        var definition = CreateModel(
            [
                Entity("root"),
                Entity("child", impact: EntityImpact.Limited, signals: [Signal("s", HealthState.Degraded)])
            ],
            [("root", "child")]);

        var snapshot = HealthModelEvaluator.Evaluate(definition);

        Assert.Equal(HealthState.Healthy, snapshot.State);
    }

    [Theory]
    // Three children, unhealthy once two or more are broken, degraded once one is broken.
    [InlineData(0, HealthState.Healthy)]
    [InlineData(1, HealthState.Degraded)]
    [InlineData(2, HealthState.Unhealthy)]
    [InlineData(3, HealthState.Unhealthy)]
    public void Evaluate_MaxNotHealthyRollup_BreachesWhenNotHealthyCountReachesThreshold(int unhealthyCount, HealthState expected)
    {
        var entities = new List<HealthModelEntity>
        {
            Entity("root", dependencies: new DependenciesAggregation
            {
                AggregationType = DependenciesAggregationType.MaxNotHealthy,
                DegradedThreshold = 1,
                UnhealthyThreshold = 2
            })
        };

        var relationships = new List<(string, string)>();
        for (var i = 0; i < 3; i++)
        {
            var state = i < unhealthyCount ? HealthState.Unhealthy : HealthState.Healthy;
            entities.Add(Entity($"child{i}", signals: [Signal("s", state)]));
            relationships.Add(("root", $"child{i}"));
        }

        var snapshot = HealthModelEvaluator.Evaluate(CreateModel([.. entities], relationships));

        Assert.Equal(expected, snapshot.State);
    }

    [Theory]
    // Four children where at least three must be healthy. Degrades at three, fails at two.
    [InlineData(4, HealthState.Healthy)]
    [InlineData(3, HealthState.Degraded)]
    [InlineData(2, HealthState.Unhealthy)]
    public void Evaluate_MinHealthyRollup_BreachesWhenHealthyCountFallsToThreshold(int healthyCount, HealthState expected)
    {
        var entities = new List<HealthModelEntity>
        {
            Entity("root", dependencies: new DependenciesAggregation
            {
                AggregationType = DependenciesAggregationType.MinHealthy,
                DegradedThreshold = 3,
                UnhealthyThreshold = 2
            })
        };

        var relationships = new List<(string, string)>();
        for (var i = 0; i < 4; i++)
        {
            var state = i < healthyCount ? HealthState.Healthy : HealthState.Unhealthy;
            entities.Add(Entity($"child{i}", signals: [Signal("s", state)]));
            relationships.Add(("root", $"child{i}"));
        }

        var snapshot = HealthModelEvaluator.Evaluate(CreateModel([.. entities], relationships));

        Assert.Equal(expected, snapshot.State);
    }

    [Fact]
    public void Evaluate_PercentageUnit_UsesShareOfChildren()
    {
        // Two of four children unhealthy is 50%, which reaches the 50% unhealthy threshold.
        var entities = new List<HealthModelEntity>
        {
            Entity("root", dependencies: new DependenciesAggregation
            {
                AggregationType = DependenciesAggregationType.MaxNotHealthy,
                UnhealthyThreshold = 50,
                Unit = AggregationUnit.Percentage
            })
        };

        var relationships = new List<(string, string)>();
        for (var i = 0; i < 4; i++)
        {
            var state = i < 2 ? HealthState.Unhealthy : HealthState.Healthy;
            entities.Add(Entity($"child{i}", signals: [Signal("s", state)]));
            relationships.Add(("root", $"child{i}"));
        }

        var snapshot = HealthModelEvaluator.Evaluate(CreateModel([.. entities], relationships));

        Assert.Equal(HealthState.Unhealthy, snapshot.State);
    }

    [Fact]
    public void Evaluate_IgnoreUnknown_ExcludesUnknownChildrenFromThreshold()
    {
        // One unhealthy and one unknown child. With unknown ignored the denominator is one, so the single
        // unhealthy child is 100% and breaches. Without ignoring it the share would only be 50%.
        var entities = new List<HealthModelEntity>
        {
            Entity("root", dependencies: new DependenciesAggregation
            {
                AggregationType = DependenciesAggregationType.MaxNotHealthy,
                UnhealthyThreshold = 100,
                Unit = AggregationUnit.Percentage,
                IgnoreUnknown = true
            }),
            Entity("broken", signals: [Signal("s", HealthState.Unhealthy)]),
            Entity("silent")
        };

        var snapshot = HealthModelEvaluator.Evaluate(
            CreateModel([.. entities], [("root", "broken"), ("root", "silent")]));

        Assert.Equal(HealthState.Unhealthy, snapshot.State);
    }

    [Fact]
    public void Evaluate_ThresholdRollupWithOnlyUnknownChildren_IsUnknown()
    {
        var entities = new List<HealthModelEntity>
        {
            Entity("root", dependencies: new DependenciesAggregation
            {
                AggregationType = DependenciesAggregationType.MaxNotHealthy,
                UnhealthyThreshold = 1
            }),
            Entity("silent")
        };

        var snapshot = HealthModelEvaluator.Evaluate(CreateModel([.. entities], [("root", "silent")]));

        Assert.Equal(HealthState.Unknown, snapshot.State);
    }

    [Fact]
    public void Evaluate_FlattensNodesInDepthFirstOrderWithDepth()
    {
        var definition = CreateModel(
            [Entity("root"), Entity("group"), Entity("leaf"), Entity("sibling")],
            [("root", "group"), ("group", "leaf"), ("root", "sibling")]);

        var snapshot = HealthModelEvaluator.Evaluate(definition);

        Assert.Collection(snapshot.AllNodes,
            n => { Assert.Equal("root", n.Name); Assert.Equal(0, n.Depth); },
            n => { Assert.Equal("group", n.Name); Assert.Equal(1, n.Depth); },
            n => { Assert.Equal("leaf", n.Name); Assert.Equal(2, n.Depth); },
            n => { Assert.Equal("sibling", n.Name); Assert.Equal(1, n.Depth); });
    }

    [Fact]
    public void Evaluate_CyclicRelationships_DoesNotRecurseForever()
    {
        var definition = CreateModel(
            [Entity("root"), Entity("a"), Entity("b")],
            [("root", "a"), ("a", "b"), ("b", "a")]);

        var snapshot = HealthModelEvaluator.Evaluate(definition);

        Assert.Collection(snapshot.AllNodes,
            n => Assert.Equal("root", n.Name),
            n => Assert.Equal("a", n.Name),
            n => Assert.Equal("b", n.Name));
    }

    [Fact]
    public void Evaluate_NoEntities_ReturnsEmptySnapshot()
    {
        var snapshot = HealthModelEvaluator.Evaluate(new HealthModelDefinition { Name = "root" });

        Assert.Null(snapshot.Root);
        Assert.Empty(snapshot.AllNodes);
        Assert.Equal(HealthState.Unknown, snapshot.State);
    }

    [Fact]
    public void EvaluationRule_UnhealthyWins_WhenBothRulesMatch()
    {
        var rule = new EvaluationRule(
            UnhealthyRule: new ThresholdRule(SignalOperator.LessThan, 99),
            DegradedRule: new ThresholdRule(SignalOperator.LessThan, 100));

        Assert.Equal(HealthState.Healthy, rule.Evaluate(100));
        Assert.Equal(HealthState.Degraded, rule.Evaluate(99.5));
        Assert.Equal(HealthState.Unhealthy, rule.Evaluate(98));
    }

    [Fact]
    public void Signal_WithEvaluationRules_PrefersObservedValueOverReportedState()
    {
        var signal = new HealthModelSignal
        {
            Name = "availability",
            Kind = SignalKind.AzureResourceMetric,
            ObservedValue = 50,
            ReportedState = HealthState.Healthy,
            EvaluationRules = new EvaluationRule(new ThresholdRule(SignalOperator.LessThan, 99))
        };

        Assert.Equal(HealthState.Unhealthy, signal.State);
    }

    private static HealthModelDefinition CreateModel(ImmutableArray<HealthModelEntity> entities, IEnumerable<(string Parent, string Child)> relationships)
    {
        return new HealthModelDefinition
        {
            Name = "root",
            Entities = entities,
            Relationships = [.. relationships.Select(r => new HealthModelRelationship(r.Parent, r.Child))]
        };
    }

    private static HealthModelEntity Entity(
        string name,
        EntityImpact impact = EntityImpact.Standard,
        DependenciesAggregation? dependencies = null,
        ImmutableArray<HealthModelSignal>? signals = null)
    {
        return new HealthModelEntity
        {
            Name = name,
            Impact = impact,
            Dependencies = dependencies ?? DependenciesAggregation.WorstOf,
            Signals = signals ?? []
        };
    }

    private static HealthModelSignal Signal(string name, HealthState state)
        => new() { Name = name, ReportedState = state };
}
