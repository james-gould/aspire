// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model.HealthModel;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using Strings = Aspire.Dashboard.Resources.HealthModel;

namespace Aspire.Dashboard.Components.Controls;

public partial class HealthModelEntityDetails : ComponentBase
{
    [Inject]
    public required IStringLocalizer<Strings> Loc { get; init; }
    [Parameter, EditorRequired]
    public required HealthModelNode Node { get; set; }
    [Parameter]
    public HealthModelSnapshot? Snapshot { get; set; }
    [Parameter]
    public HealthModelEntityConfiguration? Configuration { get; set; }
    [Parameter]
    public bool Editable { get; set; }
    [Parameter]
    public EventCallback<HealthModelEntityConfiguration> OnApply { get; set; }
    [Parameter]
    public EventCallback<string> OnSelect { get; set; }

    private IQueryable<HealthModelSignal> _signals = Enumerable.Empty<HealthModelSignal>().AsQueryable();
    private IQueryable<HealthModelNode> _children = Enumerable.Empty<HealthModelNode>().AsQueryable();
    private HealthModelEntityConfiguration? _previous;
    private string _displayName = string.Empty;
    private string _impact = nameof(EntityImpact.Standard);
    private string _aggregation = nameof(DependenciesAggregationType.WorstOf);
    private string _unit = nameof(AggregationUnit.Absolute);
    private double _x;
    private double _y;
    private double? _objective;
    private double? _degraded;
    private double? _unhealthy;
    private bool _ignoreUnknown = true;
    private bool _validationError;

    private string RollupDescription => HealthModelLabels.Aggregation(Node.Entity.Dependencies.AggregationType, Loc);
    private string ThresholdUnit => Loc[Node.Entity.Dependencies.Unit == AggregationUnit.Percentage
        ? nameof(Strings.HealthModelPercentage) : nameof(Strings.HealthModelAbsolute)];
    private IReadOnlyList<HealthModelNode> Parents => Snapshot is null ? [] :
        Snapshot.AllNodes.Where(n => n.Children.Any(child => child.Name == Node.Name)).ToArray();

    protected override void OnParametersSet()
    {
        _signals = Node.Entity.Signals.ToList().AsQueryable();
        _children = Node.Children.ToList().AsQueryable();
        if (Configuration is not { } config)
        {
            return;
        }
        // Health refreshes must not overwrite a partially edited form. Only reload when the selected
        // entity or its declarative settings actually change (for example, Discard or Undo).
        if (_previous is { } previous &&
            (previous.Name, previous.DisplayName, previous.CanvasPosition, previous.Impact, previous.Dependencies, previous.HealthObjective) ==
            (config.Name, config.DisplayName, config.CanvasPosition, config.Impact, config.Dependencies, config.HealthObjective))
        {
            return;
        }
        _previous = config;
        _displayName = config.DisplayName;
        _x = config.CanvasPosition.X;
        _y = config.CanvasPosition.Y;
        _impact = config.Impact.ToString();
        _aggregation = config.Dependencies.AggregationType.ToString();
        _unit = config.Dependencies.Unit.ToString();
        _degraded = config.Dependencies.DegradedThreshold;
        _unhealthy = config.Dependencies.UnhealthyThreshold;
        _ignoreUnknown = config.Dependencies.IgnoreUnknown;
        _objective = config.HealthObjective;
        _validationError = false;
    }

    private async Task ApplyAsync()
    {
        if (!Editable || Configuration is null)
        {
            return;
        }
        _validationError = false;
        if (!Enum.TryParse<EntityImpact>(_impact, out var impact) ||
            !Enum.TryParse<DependenciesAggregationType>(_aggregation, out var aggregation) ||
            !Enum.TryParse<AggregationUnit>(_unit, out var unit))
        {
            _validationError = true;
            return;
        }

        var dependencies = aggregation == DependenciesAggregationType.WorstOf
            ? DependenciesAggregation.WorstOf
            : new DependenciesAggregation
            {
                AggregationType = aggregation,
                Unit = unit,
                DegradedThreshold = _degraded,
                UnhealthyThreshold = _unhealthy,
                IgnoreUnknown = _ignoreUnknown
            };
        try
        {
            HealthModelDocuments.ValidateAggregation(dependencies);
        }
        catch (InvalidDataException)
        {
            _validationError = true;
            return;
        }
        if (string.IsNullOrWhiteSpace(_displayName) || _displayName.Length > 260 ||
            !HealthModelDocuments.IsValidPosition(new HealthModelCanvasPosition(_x, _y)) ||
            _objective is { } objective && (!double.IsFinite(objective) || objective < 0 || objective > 100))
        {
            _validationError = true;
            return;
        }

        await OnApply.InvokeAsync(Configuration with
        {
            DisplayName = _displayName.Trim(),
            CanvasPosition = new HealthModelCanvasPosition(_x, _y),
            Impact = Node.Entity.ResourceName is null ? EntityImpact.Standard : impact,
            Dependencies = dependencies,
            HealthObjective = _objective
        });
    }
}
