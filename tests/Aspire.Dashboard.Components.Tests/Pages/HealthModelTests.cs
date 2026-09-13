// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Threading.Channels;
using Aspire.Dashboard.Components.Resize;
using Aspire.Dashboard.Components.Controls;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Model.HealthModel;
using Aspire.Dashboard.Tests.Shared;
using Aspire.Dashboard.Utils;
using Aspire.Tests.Shared.DashboardModel;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.JSInterop;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Pages;

[UseCulture("en-US")]
public class HealthModelTests : DashboardTestContext
{
    [Fact]
    public void Render_ProjectsAndContainers_ShowsEntityHierarchy()
    {
        var cut = RenderHealthModelPage(
            ModelTestHelpers.CreateResource(resourceName: "api", resourceType: KnownResourceTypes.Project, state: KnownResourceState.Running),
            ModelTestHelpers.CreateResource(resourceName: "cache", resourceType: KnownResourceTypes.Container, state: KnownResourceState.Running));

        cut.WaitForAssertion(() =>
        {
            Assert.Equal(3, cut.FindAll(".health-model-entity").Count);
            Assert.Collection(cut.FindAll(".health-model-card-name").Select(e => e.TextContent.Trim()).Order(StringComparer.Ordinal),
                name => Assert.Equal("AppHost", name),
                name => Assert.Equal("api", name),
                name => Assert.Equal("cache", name));
        });
    }

    [Fact]
    public void Render_AllResourcesRunning_ShowsHealthyOverallState()
    {
        var cut = RenderHealthModelPage(
            ModelTestHelpers.CreateResource(resourceName: "api", resourceType: KnownResourceTypes.Project, state: KnownResourceState.Running));

        cut.WaitForAssertion(() =>
        {
            var overview = cut.Find(".health-model-overview-state");
            Assert.Equal(nameof(HealthState.Healthy), overview.TextContent.Trim());
        });
    }

    [Fact]
    public void Render_FailedContainer_ShowsUnhealthyOverallState()
    {
        var cut = RenderHealthModelPage(
            ModelTestHelpers.CreateResource(resourceName: "api", resourceType: KnownResourceTypes.Project, state: KnownResourceState.Running),
            ModelTestHelpers.CreateResource(resourceName: "cache", resourceType: KnownResourceTypes.Container, state: KnownResourceState.FailedToStart));

        cut.WaitForAssertion(() =>
        {
            var overview = cut.Find(".health-model-overview-state");
            Assert.Equal(nameof(HealthState.Unhealthy), overview.TextContent.Trim());
        });
    }

    [Fact]
    public void Render_NoResources_StillShowsAppHostRoot()
    {
        var cut = RenderHealthModelPage();

        cut.WaitForAssertion(() =>
        {
            var overview = cut.Find(".health-model-overview-state");
            Assert.Equal(nameof(HealthState.Unknown), overview.TextContent.Trim());
            Assert.Equal("AppHost", Assert.Single(cut.FindAll(".health-model-card-name")).TextContent.Trim());
        });
    }

    [Fact]
    public void Render_SelectedEntity_ShowsSignalsInDetailsPane()
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var dashboardClient = new TestDashboardClient(
            isEnabled: true,
            initialResources:
            [
                ModelTestHelpers.CreateResource(
                    resourceName: "api",
                    displayName: "api",
                    resourceType: KnownResourceTypes.Project,
                    state: KnownResourceState.Running,
                    healthReports: [new HealthReportViewModel("live", HealthStatus.Degraded, "Warming up", null)])
            ],
            resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>);

        HealthModelSetupHelpers.Setup(this, viewport, dashboardClient);

        // The selected entity is a query string parameter, so navigate to the deep link rather than
        // supplying the parameter directly. This also exercises the real deep-link path.
        var navigationManager = Services.GetRequiredService<NavigationManager>();
        navigationManager.NavigateTo(DashboardUrls.HealthModelUrl("api_0"));

        var cut = RenderComponent<Components.Pages.HealthModel>(builder =>
        {
            builder.AddCascadingValue(viewport);
        });

        cut.WaitForAssertion(() =>
        {
            var details = cut.FindComponent<Aspire.Dashboard.Components.Controls.HealthModelEntityDetails>();
            var markup = details.Markup;

            Assert.Contains("Resource state", markup, StringComparison.Ordinal);
            Assert.Contains("live", markup, StringComparison.Ordinal);
            Assert.Contains("Warming up", markup, StringComparison.Ordinal);
        });
    }

    private IRenderedComponent<Components.Pages.HealthModel> RenderHealthModelPage(params ResourceViewModel[] resources)
        => RenderHealthModelPage("Graph", null, resources);

    [Fact]
    public async Task SaveFailureKeepsTheDraftAndShowsAnError()
    {
        var storage = new TestLocalStorage
        {
            OnSetAsync = (_, _) => throw new JSException("Storage quota exceeded.")
        };
        var cut = RenderHealthModelPage("Designer", storage, ModelTestHelpers.CreateResource("api", state: KnownResourceState.Running));
        var graph = cut.FindComponent<HealthModelGraph>();
        var api = graph.Instance.Document.Entities.Single(e => e.AspireResourceName == "api");
        await cut.InvokeAsync(() => graph.Instance.MoveEntity(api.Name, 400, 300));

        cut.FindAll("fluent-button").Single(b => b.TextContent.Trim() == Resources.HealthModel.HealthModelSave).Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Equal(Resources.HealthModel.HealthModelSaveError, cut.Find("[role='alert']").TextContent.Trim());
            Assert.Equal(Resources.HealthModel.HealthModelUnsaved, cut.Find(".health-model-save-state").TextContent.Trim());
            Assert.NotEqual(api.CanvasPosition, cut.FindComponent<HealthModelGraph>().Instance.Document.Entities.Single(e => e.Name == api.Name).CanvasPosition);
        });
    }

    [Fact]
    public async Task SavePersistsThePortableDocumentAndDiscardRestoresIt()
    {
        HealthModelDocument? saved = null;
        var storage = new TestLocalStorage
        {
            OnSetAsync = (_, value) =>
            {
                saved = Assert.IsType<HealthModelDocument>(value);
                return Task.CompletedTask;
            }
        };
        var cut = RenderHealthModelPage("Designer", storage, ModelTestHelpers.CreateResource("api", state: KnownResourceState.Running));
        var graph = cut.FindComponent<HealthModelGraph>();
        var api = graph.Instance.Document.Entities.Single(e => e.AspireResourceName == "api");
        await cut.InvokeAsync(() => graph.Instance.MoveEntity(api.Name, 400, 300));
        cut.FindAll("fluent-button").Single(b => b.TextContent.Trim() == Resources.HealthModel.HealthModelSave).Click();
        cut.WaitForAssertion(() => Assert.NotNull(saved));
        var savedPosition = saved!.Entities.Single(e => e.Name == api.Name).CanvasPosition;

        await cut.InvokeAsync(() => graph.Instance.MoveEntity(api.Name, 800, 600));
        cut.FindAll("fluent-button").Single(b => b.TextContent.Trim() == Resources.HealthModel.HealthModelDiscard).Click();

        cut.WaitForAssertion(() =>
            Assert.Equal(savedPosition, cut.FindComponent<HealthModelGraph>().Instance.Document.Entities.Single(e => e.Name == api.Name).CanvasPosition));
    }

    [Fact]
    public async Task ResourcesDiscoveredAfterInitialLoadRecoverTheirSavedPositions()
    {
        var channel = Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>();
        var client = new TestDashboardClient(isEnabled: true, initialResources: [], resourceChannelProvider: () => channel);
        var resource = ModelTestHelpers.CreateResource("api", state: KnownResourceState.Running);
        var model = AspireHealthModelBuilder.Build([resource]);
        var document = HealthModelDocuments.Create(model, client.ApplicationName);
        var savedPosition = new HealthModelCanvasPosition(640, 512);
        document = document with
        {
            Entities = [.. document.Entities.Select(e => e.AspireResourceName == "api" ? e with { CanvasPosition = savedPosition } : e)]
        };
        var storage = new TestLocalStorage
        {
            OnGetAsync = key => key.StartsWith("Aspire_HealthModel_v1_", StringComparison.Ordinal)
                ? (true, document) : (false, null)
        };
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        HealthModelSetupHelpers.Setup(this, viewport, client, storage);
        var cut = RenderComponent<Components.Pages.HealthModel>(parameters => parameters.AddCascadingValue(viewport));

        await channel.Writer.WriteAsync([new ResourceViewModelChange(ResourceViewModelChangeType.Upsert, resource)], Xunit.TestContext.Current.CancellationToken);

        cut.WaitForAssertion(() => Assert.Equal(savedPosition,
            cut.FindComponent<HealthModelGraph>().Instance.Document.Entities.Single(e => e.AspireResourceName == "api").CanvasPosition));
    }

    private IRenderedComponent<Components.Pages.HealthModel> RenderHealthModelPage(string view, TestLocalStorage? storage, params ResourceViewModel[] resources)
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var dashboardClient = new TestDashboardClient(
            isEnabled: true,
            initialResources: resources,
            resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>);

        HealthModelSetupHelpers.Setup(this, viewport, dashboardClient, storage);
        Services.GetRequiredService<NavigationManager>().NavigateTo(DashboardUrls.HealthModelUrl(view: view));

        return RenderComponent<Components.Pages.HealthModel>(builder =>
        {
            builder.AddCascadingValue(viewport);
        });
    }
}
