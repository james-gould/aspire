// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Threading.Channels;
using Aspire.Dashboard.Components.Resize;
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
            var text = cut.Markup;
            Assert.Contains("Application", text, StringComparison.Ordinal);
            Assert.Contains("Services", text, StringComparison.Ordinal);
            Assert.Contains("Infrastructure", text, StringComparison.Ordinal);
            Assert.Contains("api", text, StringComparison.Ordinal);
            Assert.Contains("cache", text, StringComparison.Ordinal);
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
    public void Render_FailedContainer_ShowsDegradedOverallState()
    {
        var cut = RenderHealthModelPage(
            ModelTestHelpers.CreateResource(resourceName: "api", resourceType: KnownResourceTypes.Project, state: KnownResourceState.Running),
            ModelTestHelpers.CreateResource(resourceName: "cache", resourceType: KnownResourceTypes.Container, state: KnownResourceState.FailedToStart));

        cut.WaitForAssertion(() =>
        {
            var overview = cut.Find(".health-model-overview-state");
            Assert.Equal(nameof(HealthState.Degraded), overview.TextContent.Trim());
        });
    }

    [Fact]
    public void Render_NoResources_StillShowsLogicalEntities()
    {
        var cut = RenderHealthModelPage();

        cut.WaitForAssertion(() =>
        {
            var overview = cut.Find(".health-model-overview-state");
            Assert.Equal(nameof(HealthState.Unknown), overview.TextContent.Trim());
            Assert.Contains("Services", cut.Markup, StringComparison.Ordinal);
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

        ResourceSetupHelpers.SetupResourcesPage(this, viewport, dashboardClient);

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
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var dashboardClient = new TestDashboardClient(
            isEnabled: true,
            initialResources: resources,
            resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>);

        ResourceSetupHelpers.SetupResourcesPage(this, viewport, dashboardClient);

        return RenderComponent<Components.Pages.HealthModel>(builder =>
        {
            builder.AddCascadingValue(viewport);
        });
    }
}
