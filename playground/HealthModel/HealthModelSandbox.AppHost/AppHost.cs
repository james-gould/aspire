// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Eventing;
using Aspire.Hosting.Lifecycle;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

// Playground for the health model and the resource graph.
//
// The topology below is a strict tree: every resource has exactly one parent, and nothing is shared between
// branches. That is deliberate. A shared dependency turns the model into a graph, which is legitimate but
// makes it much harder to see at a glance whether health is rolling up correctly, because a single unhealthy
// leaf lights up several unrelated-looking paths at once.
//
//   storefront                        (Healthy)   web front end
//   ├── checkout-api                  (Healthy)
//   │   ├── orders-db                 (Healthy)   database, child of its server
//   │   │   └── orders-db-server      (Healthy)
//   │   └── payments-gateway          (Degraded)  <- degrades the checkout branch
//   ├── catalog-api                   (Healthy)
//   │   ├── catalog-db                (Healthy)
//   │   │   └── catalog-db-server     (Healthy)
//   │   └── search-index              (Unhealthy) <- fails the catalog branch
//   └── identity-api                  (Healthy)   fully healthy branch
//       └── identity-cache            (Healthy)
//
// Only three resources are anything other than healthy, so every other colour in the graph has been
// inherited. The expected result once everything has started:
//
//   storefront    Unhealthy  (worst of its branches, via catalog-api -> search-index)
//   checkout-api  Degraded   (via payments-gateway)
//   catalog-api   Unhealthy  (via search-index)
//   identity-api  Healthy    (nothing unhealthy underneath it)

var builder = DistributedApplication.CreateBuilder(args);

builder.Services.TryAddEventingSubscriber<TestResourceLifecycle>();

// Checkout branch. The database hangs off its server as a parent/child pair, which is how a real database
// integration models itself, so the graph gets a three-level chain to lay out.
var ordersDbServer = AddTestResource("orders-db-server", HealthStatus.Healthy, "Accepting connections.");
var ordersDb = AddTestResource("orders-db", HealthStatus.Healthy, "Migrations applied.")
    .WithParentRelationship(ordersDbServer);

var paymentsGateway = AddTestResource("payments-gateway", HealthStatus.Degraded, "Elevated latency from the payment provider.");

var checkoutApi = AddTestResource("checkout-api", HealthStatus.Healthy, "Accepting orders.")
    .WithReferenceRelationship(ordersDb)
    .WithReferenceRelationship(paymentsGateway);

// Catalog branch.
var catalogDbServer = AddTestResource("catalog-db-server", HealthStatus.Healthy, "Accepting connections.");
var catalogDb = AddTestResource("catalog-db", HealthStatus.Healthy, "Migrations applied.")
    .WithParentRelationship(catalogDbServer);

var searchIndex = AddTestResource("search-index", HealthStatus.Unhealthy, "Index rebuild failed.", exceptionMessage: "Shard 3 is offline.");

var catalogApi = AddTestResource("catalog-api", HealthStatus.Healthy, "Serving product data.")
    .WithReferenceRelationship(catalogDb)
    .WithReferenceRelationship(searchIndex);

// Identity branch, kept entirely healthy so there is a green path to compare the other two against.
var identityCache = AddTestResource("identity-cache", HealthStatus.Healthy, "Cache warm.");

var identityApi = AddTestResource("identity-api", HealthStatus.Healthy, "Issuing tokens.")
    .WithReferenceRelationship(identityCache);

AddTestResource("storefront", HealthStatus.Healthy, "Serving customers.")
    .WithReferenceRelationship(checkoutApi)
    .WithReferenceRelationship(catalogApi)
    .WithReferenceRelationship(identityApi);

#if !SKIP_DASHBOARD_REFERENCE
// This project is only added in playground projects to support development/debugging
// of the dashboard. It is not required in end developer code. Comment out this code
// or build with `/p:SkipDashboardReference=true`, to test end developer
// dashboard launch experience, Refer to Directory.Build.props for the path to
// the dashboard binary (defaults to the Aspire.Dashboard bin output in the
// artifacts dir).
builder.AddProject<Projects.Aspire_Dashboard>(KnownResourceNames.AspireDashboard);
#endif

builder.Build().Run();

IResourceBuilder<TestResource> AddTestResource(string name, HealthStatus status, string? description = null, string? exceptionMessage = null)
{
    builder.Services.AddHealthChecks()
                    .AddCheck(
                        $"{name}_check",
                        () => new HealthCheckResult(status, description, exceptionMessage is null ? null : new InvalidOperationException(exceptionMessage)));

    return builder
        .AddResource(new TestResource(name))
        .WithHealthCheck($"{name}_check")
        .WithInitialState(new()
        {
            ResourceType = "Test Resource",
            State = "Starting",
            Properties = [],
        })
        .ExcludeFromManifest();
}

internal sealed class TestResource(string name) : Resource(name), IResourceWithEndpoints;

/// <summary>
/// Moves the test resources to the running state shortly after startup.
/// </summary>
/// <remarks>
/// The delay is intentional. It leaves the resources in an unknown state for long enough to see that a
/// starting resource does not colour the graph, which is the behaviour that keeps the model from flashing
/// red every time the app host boots.
/// </remarks>
internal sealed class TestResourceLifecycle(ResourceNotificationService notificationService) : IDistributedApplicationEventingSubscriber
{
    public Task OnBeforeStartAsync(BeforeStartEvent @event, CancellationToken cancellationToken)
    {
        foreach (var resource in @event.Model.Resources.OfType<TestResource>())
        {
            Task.Run(
                async () =>
                {
                    await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);

                    await notificationService.PublishUpdateAsync(
                        resource,
                        state => state with { State = new("Running", "success") });
                },
                cancellationToken);
        }

        return Task.CompletedTask;
    }

    public Task SubscribeAsync(IDistributedApplicationEventing eventing, DistributedApplicationExecutionContext executionContext, CancellationToken cancellationToken)
    {
        eventing.Subscribe<BeforeStartEvent>(OnBeforeStartAsync);
        return Task.CompletedTask;
    }
}
