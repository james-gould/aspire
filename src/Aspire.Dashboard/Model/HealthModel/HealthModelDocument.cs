// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Dashboard.Model.HealthModel;

internal static class HealthModelDocuments
{
    public const int MaxFileSize = HealthModelContract.MaxFileSize;

    public static HealthModelDocument Create(HealthModelDefinition definition, string applicationName)
    {
        var positions = HealthModelLayout.Arrange(definition);
        return new HealthModelDocument
        {
            Name = definition.Name,
            ApplicationName = applicationName,
            Entities = [.. definition.Entities.Select(entity => new HealthModelEntityConfiguration
            {
                Name = entity.Name,
                DisplayName = entity.DisplayName ?? entity.Name,
                AspireResourceName = entity.AspireResourceName,
                ReplicaIndex = entity.ReplicaIndex,
                CanvasPosition = positions[entity.Name],
                Impact = entity.Impact,
                Dependencies = entity.Dependencies,
                HealthObjective = entity.HealthObjective,
                LocalSignals = [.. entity.Signals.Select(s => new HealthModelSignalBinding(s.Name, s.Kind))]
            })],
            Relationships = definition.Relationships
        };
    }

    public static HealthModelDocument Reconcile(HealthModelDocument saved, HealthModelDefinition definition)
    {
        var current = Create(definition, saved.ApplicationName);
        var savedEntities = saved.Entities.ToDictionary(e => e.Name, StringComparer.Ordinal);
        return current with
        {
            Entities = [.. current.Entities.Select(entity =>
                savedEntities.TryGetValue(entity.Name, out var previous)
                    ? entity with
                    {
                        CanvasPosition = previous.CanvasPosition,
                        DisplayName = previous.DisplayName,
                        Impact = previous.Impact,
                        Dependencies = previous.Dependencies,
                        HealthObjective = previous.HealthObjective
                    }
                    : entity)]
        };
    }

    public static HealthModelDefinition Apply(HealthModelDefinition live, HealthModelDocument document)
    {
        var settings = document.Entities.ToDictionary(e => e.Name, StringComparer.Ordinal);
        return live with
        {
            Entities = [.. live.Entities.Select(entity => settings.TryGetValue(entity.Name, out var config)
                ? entity with { DisplayName = config.DisplayName, Impact = config.Impact, Dependencies = config.Dependencies, HealthObjective = config.HealthObjective }
                : entity)]
        };
    }

    public static string Serialize(HealthModelDocument document) => HealthModelContract.Serialize(document);

    public static HealthModelDocument Deserialize(string json, HealthModelDocument current)
    {
        var document = HealthModelContract.Deserialize(json);
        ValidateApplication(document, current.ApplicationName);
        if (document.Name != current.Name ||
            !document.Entities.Select(e => (e.Name, e.AspireResourceName, e.ReplicaIndex)).ToHashSet()
                .SetEquals(current.Entities.Select(e => (e.Name, e.AspireResourceName, e.ReplicaIndex))) ||
            !document.Relationships.ToHashSet().SetEquals(current.Relationships))
        {
            throw new InvalidDataException("The imported topology does not match this AppHost. Relationships are defined in the AppHost, not the designer.");
        }
        var currentEntities = current.Entities.ToDictionary(e => e.Name, StringComparer.Ordinal);
        if (document.Entities.Any(e => !e.LocalSignals.ToHashSet().SetEquals(currentEntities[e.Name].LocalSignals)))
        {
            throw new InvalidDataException("Local signal bindings are defined by the AppHost and cannot be changed in an imported layout.");
        }
        return document;
    }

    public static void Validate(HealthModelDocument document, string applicationName)
    {
        HealthModelContract.Validate(document);
        ValidateApplication(document, applicationName);
    }

    public static bool IsValidPosition(HealthModelCanvasPosition position) => HealthModelContract.IsValidPosition(position);

    public static void ValidateAggregation(DependenciesAggregation aggregation) => HealthModelContract.ValidateAggregation(aggregation);

    private static void ValidateApplication(HealthModelDocument document, string applicationName)
    {
        if (document.ApplicationName != applicationName)
        {
            throw new InvalidDataException("The model application does not match this AppHost.");
        }
    }
}
