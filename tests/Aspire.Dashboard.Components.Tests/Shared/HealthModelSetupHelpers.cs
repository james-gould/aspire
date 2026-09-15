// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Resize;
using Aspire.Dashboard.Model.BrowserStorage;
using Bunit;

namespace Aspire.Dashboard.Components.Tests.Shared;

internal static class HealthModelSetupHelpers
{
    public static void Setup(TestContext context, ViewportInformation viewport, IDashboardClient client, ILocalStorage? storage = null)
    {
        ResourceSetupHelpers.SetupResourcesPage(context, viewport, client, localStorage: storage);
        FluentUISetupHelpers.SetupFluentList(context);
        FluentUISetupHelpers.SetupFluentTextField(context);

        var module = context.JSInterop.SetupModule("/js/app-healthmodel.js");
        var graph = module.SetupModule("createHealthModelGraph", _ => true);
        graph.SetupVoid("update", _ => true).SetVoidResult();
        graph.SetupVoid("fit", _ => true).SetVoidResult();
        graph.SetupVoid("zoomBy", _ => true).SetVoidResult();
        graph.SetupVoid("dispose", _ => true).SetVoidResult();
    }
}
