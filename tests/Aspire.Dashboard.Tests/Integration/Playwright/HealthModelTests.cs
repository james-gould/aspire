// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using Aspire.Dashboard.Model.HealthModel;
using Aspire.Dashboard.Tests.Integration.Playwright.Infrastructure;
using Aspire.TestUtilities;
using Microsoft.Playwright;
using Xunit;
using Strings = Aspire.Dashboard.Resources.HealthModel;

namespace Aspire.Dashboard.Tests.Integration.Playwright;

[RequiresFeature(TestFeature.Playwright)]
public class HealthModelTests(ResourceGraphTests.GraphDashboardServerFixture fixture)
    : PlaywrightTestsBase<ResourceGraphTests.GraphDashboardServerFixture>(fixture)
{
    [Fact]
    public async Task GraphAndEntitiesUseTheSameAppHostTopology()
    {
        await RunTestAsync(async page =>
        {
            var model = CreateModel();
            await OpenAsync(page, model.Entities.Length);
            var paths = await page.Locator(".health-model-edge").EvaluateAllAsync<string[]>(
                "elements => elements.map(e => e.dataset.parent + ':' + e.dataset.child)");
            Assert.Equal(model.Relationships.Select(r => r.ParentEntityName + ":" + r.ChildEntityName).Order(), paths.Order());

            var root = page.Locator($".health-model-entity[data-entity='{model.Name}']");
            await Assertions.Expect(root).ToHaveAttributeAsync("data-health", "Unhealthy");
            await page.Locator("#Entities").ClickAsync();
            await Assertions.Expect(page.Locator(".health-model-entity-name")).ToHaveCountAsync(model.Entities.Length);
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "database", Exact = true }).ClickAsync();
            await Assertions.Expect(page.Locator(".health-model-details-layout")).ToContainTextAsync(Strings.HealthModelSignalsSource);
            await Assertions.Expect(page.Locator(".health-model-parent-list")).ToContainTextAsync("api");
        });
    }

    [Fact]
    public async Task DesignerSavesExactPositionsAcrossReloadAndDefinitionExport()
    {
        await RunTestAsync(async page =>
        {
            var model = CreateModel();
            await OpenAsync(page, model.Entities.Length);
            await page.Locator("#Designer").ClickAsync();
            var entity = model.Entities.Single(e => e.AspireResourceName == "healthy");
            var card = page.Locator($".health-model-entity[data-entity='{entity.Name}']");
            var before = await card.GetAttributeAsync("transform");
            await DragAsync(page, card, 72, 36);
            await Assertions.Expect(SaveButton(page)).ToBeEnabledAsync();
            var moved = await card.GetAttributeAsync("transform");
            Assert.NotEqual(before, moved);
            await SaveButton(page).ClickAsync();
            await Assertions.Expect(SaveButton(page)).ToBeDisabledAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Status)).ToContainTextAsync("Model saved");

            await page.Locator("#Graph").ClickAsync();
            await page.ReloadAsync();
            await Assertions.Expect(card).ToHaveAttributeAsync("transform", moved!);

            var download = await page.RunAndWaitForDownloadAsync(() =>
                page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = Strings.HealthModelExport, Exact = true }).ClickAsync());
            await using var stream = await download.CreateReadStreamAsync();
            Assert.NotNull(stream);
            using var reader = new StreamReader(stream);
            var json = await reader.ReadToEndAsync();
            var baseline = HealthModelDocuments.Create(model, "IntegrationTestApplication");
            var document = HealthModelDocuments.Deserialize(json, baseline);
            var position = document.Entities.Single(e => e.Name == entity.Name).CanvasPosition;
            Assert.Equal(FormattableString.Invariant($"translate({position.X},{position.Y})"), moved);

            await page.Locator("#Designer").ClickAsync();
            await DragAsync(page, card, -48, 56);
            await Assertions.Expect(SaveButton(page)).ToBeEnabledAsync();
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = Strings.HealthModelDiscard, Exact = true }).ClickAsync();
            await Assertions.Expect(card).ToHaveAttributeAsync("transform", moved!);
        });
    }

    [Fact]
    public async Task ImportedPositionsAndImpactAreAppliedAsAnExplicitDraft()
    {
        await RunTestAsync(async page =>
        {
            var model = CreateModel();
            await OpenAsync(page, model.Entities.Length);
            var document = HealthModelDocuments.Create(model, "IntegrationTestApplication");
            var database = document.Entities.Single(e => e.AspireResourceName == "database");
            var imported = document with
            {
                Entities = [.. document.Entities.Select(e => e.Name == database.Name
                    ? e with { CanvasPosition = new(888, 688), Impact = EntityImpact.Limited }
                    : e)]
            };
            await page.Locator("input[type='file']").SetInputFilesAsync(new FilePayload
            {
                Name = "aspire-healthmodel.json",
                MimeType = "application/json",
                Buffer = Encoding.UTF8.GetBytes(HealthModelDocuments.Serialize(imported))
            });

            await Assertions.Expect(SaveButton(page)).ToBeEnabledAsync();
            await Assertions.Expect(page.Locator($".health-model-entity[data-entity='{database.Name}']"))
                .ToHaveAttributeAsync("transform", "translate(888,688)");
            await Assertions.Expect(page.Locator($".health-model-entity[data-entity='{model.Name}']"))
                .ToHaveAttributeAsync("data-health", "Degraded");

            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = Strings.HealthModelDiscard, Exact = true }).ClickAsync();
            await Assertions.Expect(page.Locator($".health-model-entity[data-entity='{model.Name}']"))
                .ToHaveAttributeAsync("data-health", "Unhealthy");
        });
    }

    [Fact]
    public async Task DesignerEditsPropagationWithoutChangingTheAppHostTopology()
    {
        await RunTestAsync(async page =>
        {
            var model = CreateModel();
            await OpenAsync(page, model.Entities.Length);
            await page.Locator("#Designer").ClickAsync();
            var database = model.Entities.Single(e => e.AspireResourceName == "database");
            await page.Locator($".health-model-entity[data-entity='{database.Name}']").ClickAsync();
            await page.GetByRole(AriaRole.Combobox, new PageGetByRoleOptions { Name = Strings.HealthModelPropertyImpact, Exact = true }).ClickAsync();
            await page.GetByRole(AriaRole.Option, new PageGetByRoleOptions { Name = Strings.HealthModelImpactLimited, Exact = true }).ClickAsync();
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = Strings.HealthModelApply, Exact = true }).ClickAsync();
            await Assertions.Expect(page.Locator($".health-model-entity[data-entity='{model.Name}']"))
                .ToHaveAttributeAsync("data-health", "Degraded");
            await Assertions.Expect(page.Locator(".health-model-edge")).ToHaveCountAsync(model.Relationships.Length);
            await Assertions.Expect(SaveButton(page)).ToBeEnabledAsync();
        });
    }

    [Fact]
    public async Task InvalidImportDoesNotReplaceTheCurrentModel()
    {
        await RunTestAsync(async page =>
        {
            var model = CreateModel();
            await OpenAsync(page, model.Entities.Length);
            var before = await page.Locator(".health-model-entity").EvaluateAllAsync<string[]>("nodes => nodes.map(n => n.getAttribute('transform'))");
            await page.Locator("input[type='file']").SetInputFilesAsync(new FilePayload
            {
                Name = "broken.json",
                MimeType = "application/json",
                Buffer = Encoding.UTF8.GetBytes("{\"schemaVersion\": 999}")
            });
            await Assertions.Expect(page.GetByRole(AriaRole.Alert)).ToContainTextAsync(Strings.HealthModelImportError);
            Assert.Equal(before, await page.Locator(".health-model-entity").EvaluateAllAsync<string[]>("nodes => nodes.map(n => n.getAttribute('transform'))"));
        });
    }

    private static HealthModelDefinition CreateModel() => AspireHealthModelBuilder.Build(
        ResourceGraphTests.GraphDashboardServerFixture.CreateResources(Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Unhealthy));

    private static ILocator SaveButton(IPage page) => page.GetByRole(AriaRole.Button,
        new PageGetByRoleOptions { Name = Strings.HealthModelSave, Exact = true });

    private static async Task OpenAsync(IPage page, int expectedEntities)
    {
        await page.SetViewportSizeAsync(1450, 1000);
        await page.GotoAsync("/healthmodel");
        await Assertions.Expect(page.Locator(".health-model-entity")).ToHaveCountAsync(expectedEntities);
    }

    private static async Task DragAsync(IPage page, ILocator card, float deltaX, float deltaY)
    {
        var bounds = await card.Locator(".health-model-card").BoundingBoxAsync();
        Assert.NotNull(bounds);
        var x = bounds.X + bounds.Width / 2;
        var y = bounds.Y + bounds.Height / 2;
        await page.Mouse.MoveAsync(x, y);
        await page.Mouse.DownAsync();
        try
        {
            await page.Mouse.MoveAsync(x + deltaX, y + deltaY, new MouseMoveOptions { Steps = 5 });
        }
        finally
        {
            await page.Mouse.UpAsync();
        }
    }
}
