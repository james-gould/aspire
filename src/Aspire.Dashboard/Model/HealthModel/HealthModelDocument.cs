// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Aspire.Dashboard.Model.HealthModel;

/// <summary>Coordinates in the model's canvas space, independent of zoom and viewport size.</summary>
public sealed record HealthModelCanvasPosition(double X, double Y);

/// <summary>A local signal binding, without measurements, descriptions, or exception data.</summary>
public sealed record HealthModelSignalBinding(string Name, SignalKind Kind);

/// <summary>Portable configuration for an entity; observed health is deliberately excluded.</summary>
public sealed record HealthModelEntityConfiguration
{
    public required string Name { get; init; }
    public required string DisplayName { get; init; }
    public string? AspireResourceName { get; init; }
    public int? ReplicaIndex { get; init; }
    public required HealthModelCanvasPosition CanvasPosition { get; init; }
    public EntityImpact Impact { get; init; }
    public double? HealthObjective { get; init; }
    public DependenciesAggregation Dependencies { get; init; } = DependenciesAggregation.WorstOf;
    public ImmutableArray<HealthModelSignalBinding> LocalSignals { get; init; } = [];
}

/// <summary>A versioned, portable model definition for saved layout and future publishing integration.</summary>
/// <remarks>
/// This is not an ARM template. Entity names, relationships, impact, dependency settings and canvas
/// coordinates map to Microsoft.CloudHealth entities. Local signal bindings still require an Azure
/// metric/query mapping or an external signal producer when a publisher consumes this definition.
/// </remarks>
public sealed record HealthModelDocument
{
    [JsonRequired]
    public int SchemaVersion { get; init; } = 1;
    public required string Name { get; init; }
    public required string ApplicationName { get; init; }
    public required ImmutableArray<HealthModelEntityConfiguration> Entities { get; init; }
    public required ImmutableArray<HealthModelRelationship> Relationships { get; init; }
}

internal static partial class HealthModelDocuments
{
    public const int MaxFileSize = 2 * 1024 * 1024;
    private static readonly JsonSerializerOptions s_jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 32,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    [GeneratedRegex("^[a-zA-Z0-9][a-zA-Z0-9-]{1,258}[a-zA-Z0-9]$", RegexOptions.CultureInvariant)]
    private static partial Regex EntityNamePattern();

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

    public static string Serialize(HealthModelDocument document) => JsonSerializer.Serialize(document, s_jsonOptions);

    public static HealthModelDocument Deserialize(string json, HealthModelDocument current)
    {
        // A model file has the shape { schemaVersion: 1, name, applicationName, entities: [...],
        // relationships: [{ parentEntityName, childEntityName }] }. Unknown fields are rejected so
        // runtime measurements or unsupported Azure settings cannot be silently discarded on import.
        var document = JsonSerializer.Deserialize<HealthModelDocument>(json, s_jsonOptions)
            ?? throw new InvalidDataException("The model document is null.");
        Validate(document, current.ApplicationName);
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
        if (document.SchemaVersion != 1 || document.ApplicationName != applicationName ||
            document.Entities.IsDefaultOrEmpty || document.Entities.Length > 2000 || document.Relationships.IsDefault ||
            document.Relationships.Length > 10000 || string.IsNullOrEmpty(document.Name))
        {
            throw new InvalidDataException("The model version, application or collection sizes are invalid.");
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entity in document.Entities)
        {
            if (entity is null || string.IsNullOrEmpty(entity.Name) || !EntityNamePattern().IsMatch(entity.Name) ||
                !names.Add(entity.Name) || string.IsNullOrWhiteSpace(entity.DisplayName) || entity.DisplayName.Length > 260 ||
                entity.CanvasPosition is null || !IsValidPosition(entity.CanvasPosition) ||
                !Enum.IsDefined(entity.Impact) || entity.Dependencies is null || entity.LocalSignals.IsDefault ||
                entity.LocalSignals.Length > 1000 || entity.LocalSignals.Any(s => s is null || string.IsNullOrEmpty(s.Name) || !Enum.IsDefined(s.Kind)) ||
                entity.Name != document.Name && (string.IsNullOrEmpty(entity.AspireResourceName) || entity.ReplicaIndex is null) ||
                entity.ReplicaIndex is < 0 || entity.HealthObjective is { } objective && (!double.IsFinite(objective) || objective < 0 || objective > 100))
            {
                throw new InvalidDataException("An entity has an invalid name, binding, position, impact or health objective.");
            }
            ValidateAggregation(entity.Dependencies);
        }
        if (!names.Contains(document.Name) || document.Entities.Single(e => e.Name == document.Name).Impact != EntityImpact.Standard ||
            document.Relationships.Any(r => r is null || r.ChildEntityName == document.Name ||
                !names.Contains(r.ParentEntityName) || !names.Contains(r.ChildEntityName)))
        {
            throw new InvalidDataException("The model must have a standard-impact root with valid relationships and no parent.");
        }

        var definition = new HealthModelDefinition
        {
            Name = document.Name,
            Entities = [.. document.Entities.Select(e => new HealthModelEntity { Name = e.Name })],
            Relationships = document.Relationships
        };
        var topology = HealthModelTopology.Create(definition);
        var reachable = new HashSet<string>(StringComparer.Ordinal) { document.Name };
        foreach (var entity in topology.Order.Where(e => reachable.Contains(e.Name)))
        {
            reachable.UnionWith(topology.Children[entity.Name]);
        }
        if (reachable.Count != names.Count)
        {
            throw new InvalidDataException("All entities must be reachable from the model root.");
        }
    }

    public static bool IsValidPosition(HealthModelCanvasPosition position) =>
        double.IsFinite(position.X) && double.IsFinite(position.Y) &&
        Math.Abs(position.X) <= 1_000_000 && Math.Abs(position.Y) <= 1_000_000;

    public static void ValidateAggregation(DependenciesAggregation aggregation)
    {
        if (!Enum.IsDefined(aggregation.AggregationType) || !Enum.IsDefined(aggregation.Unit))
        {
            throw new InvalidDataException("The dependency aggregation type or unit is invalid.");
        }
        if (aggregation.AggregationType == DependenciesAggregationType.WorstOf)
        {
            if (aggregation.DegradedThreshold is not null || aggregation.UnhealthyThreshold is not null)
            {
                throw new InvalidDataException("Worst-of rollup does not accept thresholds.");
            }
            return;
        }

        if (aggregation.UnhealthyThreshold is not { } unhealthy || !ValidThreshold(unhealthy) ||
            aggregation.DegradedThreshold is { } degraded && (!ValidThreshold(degraded) ||
                (aggregation.AggregationType == DependenciesAggregationType.MinHealthy ? degraded <= unhealthy : degraded >= unhealthy)))
        {
            throw new InvalidDataException("Set an unhealthy threshold and order the thresholds from degraded to unhealthy.");
        }

        bool ValidThreshold(double value) => double.IsFinite(value) && value >= 0 &&
            (aggregation.Unit == AggregationUnit.Percentage ? value <= 100 : value == Math.Truncate(value));
    }
}
