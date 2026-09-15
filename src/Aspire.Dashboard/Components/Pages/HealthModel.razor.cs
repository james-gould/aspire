// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.Dashboard.Components.Controls;
using Aspire.Dashboard.Extensions;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Model.HealthModel;
using Aspire.Dashboard.Utils;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.Localization;
using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.JSInterop;
using Strings = Aspire.Dashboard.Resources.HealthModel;

namespace Aspire.Dashboard.Components.Pages;

public partial class HealthModel : ComponentBase, IAsyncDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly Dictionary<string, ResourceViewModel> _resources = new(StringComparers.ResourceName);
    private readonly Stack<HealthModelDocument> _undo = new();
    private HealthModelDefinition _live = new() { Name = AspireHealthModelBuilder.RootEntityName };
    private HealthModelSnapshot _snapshot = HealthModelSnapshot.Empty;
    private HealthModelDocument? _saved;
    private HealthModelDocument? _draft;
    private HealthModelNode? _selectedNode;
    private HealthModelGraph? _graph;
    private Task? _subscriptionTask;
    private string _applicationName = string.Empty;
    private string _filter = string.Empty;
    private string? _message;
    private bool _isError;
    private bool _invalidTopology;
    private bool _dirty;
    private bool _savedInBrowser;
    private bool _saving;
    private bool _disposed;
    private int _layoutVersion;
    private HealthState? _stateFilter;
    private HealthModelView _view = HealthModelView.Graph;

    [Inject]
    public required DashboardDataSource DataSource { get; init; }
    [Inject]
    public required IDashboardClient DashboardClient { get; init; }
    [Inject]
    public required NavigationManager NavigationManager { get; init; }
    [Inject]
    public required ILocalStorage LocalStorage { get; init; }
    [Inject]
    public required IJSRuntime JS { get; init; }
    [Inject]
    public required ILogger<HealthModel> Logger { get; init; }

    [Parameter]
    [SupplyParameterFromQuery(Name = "entity")]
    public string? EntityName { get; set; }
    [Parameter]
    [SupplyParameterFromQuery(Name = "view")]
    public string? ViewName { get; set; }

    private bool CanEdit => !DashboardClient.IsReadOnly && !_invalidTopology && !_saving;
    private bool IsDesigner => _view == HealthModelView.Designer && CanEdit;
    private string StorageKey => $"Aspire_HealthModel_v1_{Uri.EscapeDataString(_applicationName)}";
    private IQueryable<HealthModelNode> FilteredNodes => _snapshot.AllNodes
        .Where(n => (_stateFilter is null || n.State == _stateFilter) &&
            (_filter.Length == 0 || n.DisplayName.Contains(_filter, StringComparisons.UserTextSearch)))
        .ToList().AsQueryable();
    private HealthModelEntityConfiguration? SelectedConfiguration => _draft?.Entities.FirstOrDefault(e => e.Name == _selectedNode?.Name);

    protected override async Task OnInitializedAsync()
    {
        var cancellationToken = _cts.Token;
        if (DashboardClient.IsEnabled)
        {
            await DashboardClient.WhenConnected.WaitAsync(cancellationToken);
        }
        if (_disposed)
        {
            return;
        }
        _applicationName = DashboardClient.ApplicationName;
        var stored = await LocalStorage.GetAsync<HealthModelDocument>(StorageKey);
        if (_disposed)
        {
            return;
        }
        if (stored.Success && stored.Value is { } document)
        {
            try
            {
                HealthModelDocuments.Validate(document, _applicationName);
                _saved = document;
                _savedInBrowser = true;
            }
            catch (InvalidDataException ex)
            {
                Logger.LogWarning(ex, "The saved health model is invalid.");
                ShowMessage(nameof(Strings.HealthModelLoadError), error: true);
            }
        }

        var (snapshot, subscription) = await DataSource.ResourceRepository.SubscribeResourcesAsync(cancellationToken);
        if (_disposed)
        {
            return;
        }
        foreach (var resource in snapshot)
        {
            _resources[resource.Name] = resource;
        }
        RebuildModel();
        _subscriptionTask = WatchAsync(subscription);
    }

    private async Task WatchAsync(IAsyncEnumerable<IReadOnlyList<ResourceViewModelChange>> subscription)
    {
        try
        {
            await foreach (var changes in subscription.WithCancellation(_cts.Token).ConfigureAwait(false))
            {
                // All model and draft mutations are serialized on the renderer, including subscription
                // updates that arrive while the user is dragging or saving.
                await InvokeAsync(() =>
                {
                    if (_disposed)
                    {
                        return;
                    }
                    foreach (var (changeType, resource) in changes)
                    {
                        if (changeType == ResourceViewModelChangeType.Upsert)
                        {
                            _resources[resource.Name] = resource;
                        }
                        else if (changeType == ResourceViewModelChangeType.Delete)
                        {
                            _resources.Remove(resource.Name);
                        }
                    }
                    RebuildModel();
                    StateHasChanged();
                });
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Health model resource subscription failed.");
            if (!_disposed)
            {
                await InvokeAsync(() =>
                {
                    ShowMessage(nameof(Strings.HealthModelSubscriptionError), error: true);
                    StateHasChanged();
                });
            }
        }
    }

    protected override void OnParametersSet()
    {
        if (Enum.TryParse<HealthModelView>(ViewName, ignoreCase: true, out var view) && Enum.IsDefined(view))
        {
            _view = view;
        }
        else
        {
            _view = HealthModelView.Graph;
        }
        ResolveSelection();
    }

    private void RebuildModel()
    {
        _live = AspireHealthModelBuilder.Build(_resources.Values);
        try
        {
            var defaults = HealthModelDocuments.Create(_live, _applicationName);
            _saved ??= defaults;
            if (!_dirty)
            {
                // AppHost discovery can initially return only the root. Keep the full saved document
                // as the baseline so late-arriving resources recover their saved coordinates.
                _draft = _savedInBrowser ? HealthModelDocuments.Reconcile(_saved, _live) : defaults;
                if (!_savedInBrowser)
                {
                    _saved = defaults;
                }
            }
            else
            {
                var baseline = _draft! with
                {
                    Entities = [.. _saved.Entities.Concat(_draft!.Entities).GroupBy(e => e.Name, StringComparer.Ordinal).Select(g => g.Last())]
                };
                _draft = HealthModelDocuments.Reconcile(baseline, _live);
            }
            EvaluateDraft();
            _invalidTopology = false;
        }
        catch (InvalidDataException ex)
        {
            Logger.LogWarning(ex, "The AppHost topology cannot be represented as a health model.");
            _invalidTopology = true;
            ShowMessage(nameof(Strings.HealthModelInvalidTopology), error: true);
        }
    }

    private void EvaluateDraft()
    {
        _snapshot = HealthModelEvaluator.Evaluate(_draft is null ? _live : HealthModelDocuments.Apply(_live, _draft));
        ResolveSelection();
    }

    private void ResolveSelection()
    {
        _selectedNode = EntityName is null ? null : _snapshot.AllNodes.FirstOrDefault(n =>
            n.Name == EntityName || n.Entity.ResourceKey == EntityName);
    }

    private void ChangeView(FluentTab tab)
    {
        if (Enum.TryParse<HealthModelView>(tab.Id, out var view))
        {
            _view = view;
            Navigate();
        }
    }

    private void SelectEntity(string name)
    {
        EntityName = name;
        ResolveSelection();
        Navigate();
    }

    private void ClearSelectedEntity()
    {
        EntityName = null;
        _selectedNode = null;
        Navigate();
    }

    private void Navigate() => NavigationManager.NavigateTo(
        DashboardUrls.HealthModelUrl(EntityName, _view.ToString()), replace: true);

    private void ToggleState(HealthState state) => _stateFilter = _stateFilter == state ? null : state;

    private void ChangeDraft(HealthModelDocument document)
    {
        if (!CanEdit || _draft is null)
        {
            return;
        }
        // A bounded undo stack stores only declarative configuration, never live resource data.
        if (_undo.Count >= 20)
        {
            var recent = _undo.Take(19).Reverse().ToArray();
            _undo.Clear();
            foreach (var item in recent)
            {
                _undo.Push(item);
            }
        }
        _undo.Push(_draft);
        _draft = document;
        _dirty = HealthModelDocuments.Serialize(_draft) != HealthModelDocuments.Serialize(HealthModelDocuments.Reconcile(_saved!, _live));
        _message = null;
        EvaluateDraft();
    }

    private Task MoveEntity(HealthModelPositionChange change)
    {
        if (IsDesigner && _draft is not null && _draft.Entities.Any(e => e.Name == change.Name))
        {
            try
            {
                var position = HealthModelLayout.Place(_draft, change.Name, new HealthModelCanvasPosition(change.X, change.Y));
                ChangeDraft(_draft with
                {
                    Entities = [.. _draft.Entities.Select(e => e.Name == change.Name ? e with { CanvasPosition = position } : e)]
                });
            }
            catch (InvalidDataException ex)
            {
                Logger.LogWarning(ex, "Invalid position for health model entity '{EntityName}'.", change.Name);
                ShowMessage(nameof(Strings.HealthModelInvalidSettings), error: true);
            }
        }
        return Task.CompletedTask;
    }

    private void ApplyEntity(HealthModelEntityConfiguration configuration)
    {
        if (!IsDesigner || _draft is null)
        {
            return;
        }
        try
        {
            var position = HealthModelLayout.Place(_draft, configuration.Name, configuration.CanvasPosition);
            var updated = _draft with
            {
                Entities = [.. _draft.Entities.Select(e => e.Name == configuration.Name ? configuration with { CanvasPosition = position } : e)]
            };
            HealthModelDocuments.Validate(updated, _applicationName);
            ChangeDraft(updated);
        }
        catch (InvalidDataException ex)
        {
            Logger.LogWarning(ex, "Invalid health model entity configuration.");
            ShowMessage(nameof(Strings.HealthModelInvalidSettings), error: true);
        }
    }

    private void Arrange()
    {
        if (!IsDesigner || _draft is null)
        {
            return;
        }
        var positions = HealthModelLayout.Arrange(_live);
        ChangeDraft(_draft with { Entities = [.. _draft.Entities.Select(e => e with { CanvasPosition = positions[e.Name] })] });
        _layoutVersion++;
    }

    private void Undo()
    {
        if (!IsDesigner || !_undo.TryPop(out var document))
        {
            return;
        }
        _draft = HealthModelDocuments.Reconcile(document, _live);
        _dirty = HealthModelDocuments.Serialize(_draft) != HealthModelDocuments.Serialize(HealthModelDocuments.Reconcile(_saved!, _live));
        EvaluateDraft();
    }

    private void Discard()
    {
        if (!CanEdit || _saved is null)
        {
            return;
        }
        _draft = HealthModelDocuments.Reconcile(_saved, _live);
        _dirty = false;
        _undo.Clear();
        _message = null;
        _layoutVersion++;
        EvaluateDraft();
    }

    private async Task SaveAsync()
    {
        if (!CanEdit || _draft is null)
        {
            return;
        }
        _saving = true;
        var saving = _draft;
        try
        {
            HealthModelDocuments.Validate(saving, _applicationName);
            await LocalStorage.SetAsync(StorageKey, saving);
            _saved = saving;
            _savedInBrowser = true;
            _dirty = false;
            _undo.Clear();
            ShowMessage(nameof(Strings.HealthModelSaveSuccess), error: false);
        }
        catch (Exception ex) when (ex is JSException or InvalidDataException or JsonException)
        {
            Logger.LogError(ex, "Failed to save the health model.");
            ShowMessage(nameof(Strings.HealthModelSaveError), error: true);
        }
        finally
        {
            _saving = false;
        }
    }

    private async Task ExportAsync()
    {
        if (_saved is null || _dirty || _invalidTopology)
        {
            return;
        }
        try
        {
            var document = HealthModelDocuments.Reconcile(_saved, _live);
            HealthModelDocuments.Validate(document, _applicationName);
            await JS.DownloadFileAsync("aspire-healthmodel.json", HealthModelDocuments.Serialize(document));
        }
        catch (Exception ex) when (ex is JSException or InvalidDataException)
        {
            Logger.LogError(ex, "Failed to export the health model.");
            ShowMessage(nameof(Strings.HealthModelExportError), error: true);
        }
    }

    private async Task ImportAsync(InputFileChangeEventArgs args)
    {
        if (!CanEdit || _draft is null)
        {
            return;
        }
        try
        {
            await using var stream = args.File.OpenReadStream(HealthModelDocuments.MaxFileSize, _cts.Token);
            using var reader = new StreamReader(stream);
            var json = await reader.ReadToEndAsync(_cts.Token);
            var document = HealthModelDocuments.Deserialize(json, _draft);
            ChangeDraft(document);
            _view = HealthModelView.Designer;
            _layoutVersion++;
            Navigate();
            ShowMessage(nameof(Strings.HealthModelImportSuccess), error: false);
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException)
        {
            Logger.LogWarning(ex, "Failed to import the health model.");
            ShowMessage(nameof(Strings.HealthModelImportError), error: true);
        }
    }

    private void ShowMessage(string key, bool error)
    {
        _message = Loc[key];
        _isError = error;
    }

    private string StateLabel(HealthState state) => HealthModelLabels.State(state, Loc);
    private string SignalSummary(HealthModelNode node) => Loc[nameof(Strings.HealthModelSignalCount),
        node.Entity.Signals.Count(s => s.State == HealthState.Healthy), node.Entity.Signals.Length];

    internal static string GetRollupDescription(DependenciesAggregation aggregation, IStringLocalizer<Strings> loc) =>
        HealthModelLabels.Aggregation(aggregation.AggregationType, loc);

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        await _cts.CancelAsync();
        await TaskHelpers.WaitIgnoreCancelAsync(_subscriptionTask);
        _cts.Dispose();
    }

    private enum HealthModelView { Graph, Entities, Designer }
}
