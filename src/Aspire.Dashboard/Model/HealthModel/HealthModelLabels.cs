// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Localization;
using Strings = Aspire.Dashboard.Resources.HealthModel;

namespace Aspire.Dashboard.Model.HealthModel;

internal static class HealthModelLabels
{
    public static string State(HealthState state, IStringLocalizer<Strings> loc) => loc[state switch
    {
        HealthState.Healthy => nameof(Strings.HealthModelHealthy),
        HealthState.Degraded => nameof(Strings.HealthModelDegraded),
        HealthState.Unhealthy => nameof(Strings.HealthModelUnhealthy),
        _ => nameof(Strings.HealthModelUnknown)
    }];

    public static string Impact(EntityImpact impact, IStringLocalizer<Strings> loc) => loc[impact switch
    {
        EntityImpact.Limited => nameof(Strings.HealthModelImpactLimited),
        EntityImpact.Suppressed => nameof(Strings.HealthModelImpactSuppressed),
        _ => nameof(Strings.HealthModelImpactStandard)
    }];

    public static string Aggregation(DependenciesAggregationType type, IStringLocalizer<Strings> loc) => loc[type switch
    {
        DependenciesAggregationType.MinHealthy => nameof(Strings.HealthModelMinimumHealthy),
        DependenciesAggregationType.MaxNotHealthy => nameof(Strings.HealthModelMaximumNotHealthy),
        _ => nameof(Strings.HealthModelRollupWorstOf)
    }];
}
