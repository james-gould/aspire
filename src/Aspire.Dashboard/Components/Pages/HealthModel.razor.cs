// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Globalization;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Model.HealthModel;
using Aspire.Dashboard.Utils;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using Microsoft.FluentUI.AspNetCore.Components;

namespace Aspire.Dashboard.Components.Pages;

public partial class HealthModel : ComponentBase, IAsyncDisposable
{
    /// <summary>The left padding, in pixels, applied per level of model depth in the entity column.</summary>
    private const int IndentPerDepth = 16;

    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<string, ResourceViewModel> _resourceByName = new(StringComparers.ResourceName);

    private ColumnResizeLabels _resizeLabels = ColumnResizeLabels.Default;
    private ColumnSortLabels _sortLabels = ColumnSortLabels.Default;
    private HealthModelSnapshot _snapshot = HealthModelSnapshot.Empty;

    // FluentDataGrid composes LINQ operators such as Count() onto the queryable it is given. An
    // ImmutableArray<T> is a struct, so a queryable built directly over one produces an expression tree
    // typed as ImmutableArray<T> that those operators reject at runtime. Materializing into a list first
    // keeps the expression typed as a reference type, and caching it avoids rebuilding on every render.
    private IQueryable<HealthModelNode> _nodes = Enumerable.Empty<HealthModelNode>().AsQueryable();

    private HealthModelNode? _selectedNode;
    private Task? _resourceSubscriptionTask;

    [Inject]
    public required DashboardDataSource DataSource { get; init; }

    [Inject]
    public required NavigationManager NavigationManager { get; init; }

    [Parameter]
    [SupplyParameterFromQuery(Name = "entity")]
    public string? EntityName { get; set; }

    protected override void OnInitialized()
    {
        (_resizeLabels, _sortLabels) = DashboardUIHelpers.CreateGridLabels(ControlsStringsLoc);
    }

    protected override async Task OnInitializedAsync()
    {
        var (snapshot, subscription) = await DataSource.ResourceRepository.SubscribeResourcesAsync(_cts.Token);

        foreach (var resource in snapshot)
        {
            _resourceByName[resource.Name] = resource;
        }

        RebuildModel();

        _resourceSubscriptionTask = Task.Run(async () =>
        {
            await foreach (var changes in subscription.WithCancellation(_cts.Token).ConfigureAwait(false))
            {
                foreach (var (changeType, resource) in changes)
                {
                    if (changeType == ResourceViewModelChangeType.Upsert)
                    {
                        _resourceByName[resource.Name] = resource;
                    }
                    else if (changeType == ResourceViewModelChangeType.Delete)
                    {
                        _resourceByName.TryRemove(resource.Name, out _);
                    }
                }

                await InvokeAsync(() =>
                {
                    RebuildModel();
                    StateHasChanged();
                });
            }
        });
    }

    protected override void OnParametersSet()
    {
        // The selected entity is carried in the query string so the details pane survives a page reload and
        // follows browser navigation. Clearing when the parameter is absent is what closes the pane when the
        // user navigates back to the page without a selection.
        if (EntityName is null)
        {
            _selectedNode = null;
        }
        else if (_selectedNode?.Name != EntityName)
        {
            _selectedNode = _snapshot.AllNodes.FirstOrDefault(n => string.Equals(n.Name, EntityName, StringComparison.Ordinal));
        }
    }

    private void RebuildModel()
    {
        var definition = AspireHealthModelBuilder.Build(_resourceByName.Values);
        _snapshot = HealthModelEvaluator.Evaluate(definition);
        _nodes = _snapshot.AllNodes.ToList().AsQueryable();

        // Entities are rebuilt from scratch on every resource change, so the previously selected node is a
        // stale instance. Re-resolve it by name to keep the details pane pointing at live data.
        if (_selectedNode is not null)
        {
            _selectedNode = _snapshot.AllNodes.FirstOrDefault(n => string.Equals(n.Name, _selectedNode.Name, StringComparison.Ordinal));
        }
    }

    private void SelectEntity(HealthModelNode node)
    {
        _selectedNode = node;
        NavigationManager.NavigateTo(DashboardUrls.HealthModelUrl(node.Name), replace: true);
    }

    private void ClearSelectedEntity()
    {
        _selectedNode = null;
        NavigationManager.NavigateTo(DashboardUrls.HealthModelUrl(), replace: true);
    }

    private static string GetIndentStyle(HealthModelNode node)
        => $"padding-left: {node.Depth * IndentPerDepth}px;";

    private string GetSignalSummary(HealthModelNode node)
    {
        var healthy = node.Entity.Signals.Count(s => s.State == HealthState.Healthy);

        return string.Format(
            CultureInfo.CurrentCulture,
            Loc[nameof(Dashboard.Resources.HealthModel.HealthModelSignalCount)],
            healthy,
            node.Entity.Signals.Length);
    }

    private string GetRollupDescription(HealthModelNode node) => GetRollupDescription(node.Entity.Dependencies, Loc);

    /// <summary>
    /// Describes a dependency rollup in the terms the Azure portal uses, so the configured aggregation is
    /// readable without having to know the enum values.
    /// </summary>
    internal static string GetRollupDescription(DependenciesAggregation aggregation, IStringLocalizer<Dashboard.Resources.HealthModel> loc)
    {
        if (aggregation.AggregationType == DependenciesAggregationType.WorstOf)
        {
            return loc[nameof(Dashboard.Resources.HealthModel.HealthModelRollupWorstOf)];
        }

        var threshold = aggregation.UnhealthyThreshold ?? aggregation.DegradedThreshold ?? 0;
        var formattedThreshold = aggregation.Unit == AggregationUnit.Percentage
            ? threshold.ToString("0.##", CultureInfo.CurrentCulture) + "%"
            : threshold.ToString("0.##", CultureInfo.CurrentCulture);

        var format = aggregation.AggregationType == DependenciesAggregationType.MinHealthy
            ? loc[nameof(Dashboard.Resources.HealthModel.HealthModelRollupMinHealthy)]
            : loc[nameof(Dashboard.Resources.HealthModel.HealthModelRollupMaxNotHealthy)];

        return string.Format(CultureInfo.CurrentCulture, format, formattedThreshold);
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();

        // Wait for the subscription loop to unwind before disposing the source. Disposing it first would
        // make the loop throw ObjectDisposedException while observing the token.
        await TaskHelpers.WaitIgnoreCancelAsync(_resourceSubscriptionTask);

        _cts.Dispose();
    }
}
