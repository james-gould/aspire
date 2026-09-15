// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Dashboard.Model.HealthModel;
using Aspire.Dashboard.Utils;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using Microsoft.JSInterop;
using Strings = Aspire.Dashboard.Resources.HealthModel;

namespace Aspire.Dashboard.Components.Controls;

public sealed record HealthModelPositionChange(string Name, double X, double Y);

public partial class HealthModelGraph : ComponentBase, IAsyncDisposable
{
    private readonly string _gridId = $"health-model-grid-{Guid.NewGuid():N}";
    private readonly string _arrowId = $"health-model-arrow-{Guid.NewGuid():N}";
    private ElementReference _svg;
    private IJSObjectReference? _module;
    private IJSObjectReference? _graph;
    private DotNetObjectReference<HealthModelGraph>? _reference;
    private bool _disposed;
    private int _lastLayoutVersion = -1;
    private Dictionary<string, HealthModelNode> _nodes = new(StringComparer.Ordinal);
    private Dictionary<string, HealthModelEntityConfiguration> _configuration = new(StringComparer.Ordinal);

    [Inject]
    public required IJSRuntime JS { get; init; }
    [Inject]
    public required IStringLocalizer<Strings> Loc { get; init; }
    [Parameter, EditorRequired]
    public required HealthModelSnapshot Snapshot { get; set; }
    [Parameter, EditorRequired]
    public required HealthModelDocument Document { get; set; }
    [Parameter]
    public bool Editable { get; set; }
    [Parameter]
    public string? SelectedEntityName { get; set; }
    [Parameter]
    public string Filter { get; set; } = string.Empty;
    [Parameter]
    public HealthState? StateFilter { get; set; }
    [Parameter]
    public int LayoutVersion { get; set; }
    [Parameter]
    public EventCallback<string> OnSelect { get; set; }
    [Parameter]
    public EventCallback<HealthModelPositionChange> OnPositionChanged { get; set; }

    protected override void OnParametersSet()
    {
        _nodes = Snapshot.AllNodes.ToDictionary(n => n.Name, StringComparer.Ordinal);
        _configuration = Document.Entities.ToDictionary(e => e.Name, StringComparer.Ordinal);
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_disposed)
        {
            return;
        }
        if (firstRender)
        {
            _module = await JS.InvokeAsync<IJSObjectReference>("import", "/js/app-healthmodel.js");
            if (_disposed)
            {
                await JSInteropHelpers.SafeDisposeAsync(_module);
                return;
            }
            _reference = DotNetObjectReference.Create(this);
            _graph = await _module.InvokeAsync<IJSObjectReference>("createHealthModelGraph", _svg, _reference);
        }
        if (_graph is not null)
        {
            await _graph.InvokeVoidAsync("update",
                Document.Entities.Select(e => new { e.Name, e.CanvasPosition.X, e.CanvasPosition.Y }).ToArray(),
                Editable);
            if (_lastLayoutVersion != LayoutVersion)
            {
                _lastLayoutVersion = LayoutVersion;
                await _graph.InvokeVoidAsync("fit");
            }
        }
    }

    [JSInvokable]
    public Task MoveEntity(string name, double x, double y) =>
        !_disposed && Editable && _configuration.ContainsKey(name)
            ? OnPositionChanged.InvokeAsync(new HealthModelPositionChange(name, x, y))
            : Task.CompletedTask;

    [JSInvokable]
    public Task SelectEntity(string name) =>
        !_disposed && _configuration.ContainsKey(name) ? OnSelect.InvokeAsync(name) : Task.CompletedTask;

    public async Task FitAsync()
    {
        if (_graph is not null)
        {
            await _graph.InvokeVoidAsync("fit");
        }
    }

    private async Task ZoomAsync(double factor)
    {
        if (_graph is not null)
        {
            await _graph.InvokeVoidAsync("zoomBy", factor);
        }
    }

    private string GetNodeClass(HealthModelNode node)
    {
        var matches = (StateFilter is null || node.State == StateFilter) &&
            (Filter.Length == 0 || node.DisplayName.Contains(Filter, StringComparisons.UserTextSearch));
        return $"health-model-entity{(SelectedEntityName == node.Name ? " is-selected" : "")}{(matches ? "" : " is-dimmed")}";
    }

    private static string Truncate(string value) => value.Length > 27 ? value[..24] + "..." : value;
    private static string GetTransform(HealthModelCanvasPosition position) =>
        FormattableString.Invariant($"translate({position.X},{position.Y})");

    private string GetPath(HealthModelRelationship relationship)
    {
        var parent = _configuration[relationship.ParentEntityName].CanvasPosition;
        var child = _configuration[relationship.ChildEntityName].CanvasPosition;
        var y1 = parent.Y + HealthModelLayout.CardHeight / 2;
        var y2 = child.Y - HealthModelLayout.CardHeight / 2;
        var middle = (y1 + y2) / 2;
        return string.Create(CultureInfo.InvariantCulture, $"M {parent.X} {y1} C {parent.X} {middle}, {child.X} {middle}, {child.X} {y2}");
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        if (_graph is not null)
        {
            try
            {
                await _graph.InvokeVoidAsync("dispose");
            }
            catch (JSDisconnectedException)
            {
                // The browser already discarded the graph when the circuit disconnected.
            }
            await JSInteropHelpers.SafeDisposeAsync(_graph);
        }
        _reference?.Dispose();
        await JSInteropHelpers.SafeDisposeAsync(_module);
    }
}
