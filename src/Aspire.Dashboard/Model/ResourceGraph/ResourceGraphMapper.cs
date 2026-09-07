// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using System.Xml.Linq;
using Aspire.Dashboard.Model.HealthModel;
using Aspire.Dashboard.Resources;
using Microsoft.Extensions.Localization;
using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components.Extensions;

namespace Aspire.Dashboard.Model.ResourceGraph;

public static class ResourceGraphMapper
{
    /// <summary>
    /// Maps every resource in the graph, deriving the parent-child structure from app host dependencies and
    /// rolling child health up through it.
    /// </summary>
    /// <remarks>
    /// The rollup needs to see the whole graph, so it is computed once here and shared by each mapped
    /// resource rather than being recomputed per resource.
    /// </remarks>
    public static List<ResourceDto> MapResources(
        IReadOnlyList<ResourceViewModel> graphResources,
        IDictionary<string, ResourceViewModel> resourcesByName,
        IStringLocalizer<Columns> columnsLoc,
        bool showHiddenResources,
        IconResolver iconResolver)
    {
        ArgumentNullException.ThrowIfNull(graphResources);

        var edges = ResourceGraphHealth.BuildEdges(graphResources, showHiddenResources);
        var healthStates = ResourceGraphHealth.ComputeEffectiveStates(graphResources, edges);

        var childNamesByParent = edges
            .GroupBy(e => e.ParentName, StringComparers.ResourceName)
            .ToDictionary(
                g => g.Key,
                g => g.Select(e => e.ChildName).Distinct(StringComparers.ResourceName).OrderBy(n => n, StringComparers.ResourceName).ToImmutableArray(),
                StringComparers.ResourceName);

        var dtos = new List<ResourceDto>(graphResources.Count);
        foreach (var resource in graphResources)
        {
            var childNames = childNamesByParent.TryGetValue(resource.Name, out var children) ? children : [];
            var healthState = healthStates.TryGetValue(resource.Name, out var state) ? state : HealthState.Unknown;

            dtos.Add(MapResource(resource, resourcesByName, columnsLoc, iconResolver, childNames, healthState));
        }

        return dtos;
    }

    public static ResourceDto MapResource(
        ResourceViewModel r,
        IDictionary<string, ResourceViewModel> resourcesByName,
        IStringLocalizer<Columns> columnsLoc,
        IconResolver iconResolver,
        ImmutableArray<string> childNames,
        HealthState healthState)
    {
        var endpoint = ResourceUrlHelpers.GetUrls(r, includeInternalUrls: false, includeNonEndpointUrls: false).FirstOrDefault()
            ?? ResourceUrlHelpers.GetUrls(r, includeInternalUrls: false, includeNonEndpointUrls: true).FirstOrDefault();
        var resolvedEndpointText = r.IsParameter ? null : ResolvedEndpointText(endpoint);
        var resourceName = ResourceViewModel.GetResourceName(r, resourcesByName);
        var color = ColorGenerator.Instance.GetColorVariableByKey(resourceName);

        var icon = GetIconPathData(ResourceIconHelpers.GetIconForResource(iconResolver, r, IconSize.Size24));

        var stateIcon = ResourceStateViewModel.GetStateViewModel(r, columnsLoc);

        var dto = new ResourceDto
        {
            Name = r.Name,
            ResourceType = r.ResourceType,
            DisplayName = resourceName,
            Uid = r.Uid,
            ResourceIcon = new IconDto
            {
                Path = icon,
                Color = color,
                Tooltip = r.ResourceType
            },
            StateIcon = new IconDto
            {
                Path = GetIconPathData(stateIcon.Icon),
                Color = stateIcon.Color.ToAttributeValue()!,
                Tooltip = stateIcon.Text ?? r.State
            },
            ChildNames = childNames,
            HealthState = healthState.ToString(),
            EndpointUrl = r.IsParameter ? null : endpoint?.Url,
            EndpointText = resolvedEndpointText
        };

        return dto;
    }

    private static string ResolvedEndpointText(DisplayedUrl? endpoint)
    {
        var text = endpoint?.OriginalUrlString;
        if (string.IsNullOrEmpty(text))
        {
            return ControlsStrings.ResourceGraphNoEndpoints;
        }

        if (Uri.TryCreate(text, UriKind.Absolute, out var uri))
        {
            return $"{uri.Host}:{uri.Port}";
        }

        return text;
    }

    public static string GetIconPathData(Icon icon)
    {
        // Fluent UI icon content is an SVG fragment. Most icons contain one path:
        //   <path d="M..." />
        // Some icons, such as DocumentMultiple, contain sibling paths:
        //   <path d="M..." /><path d="M..." />
        // Wrap the fragment so XML parsing accepts both shapes, then combine the path data into one compound SVG path.
        var iconContent = XElement.Parse($"<svg>{icon.Content}</svg>");
        var pathData = iconContent.Elements()
            .Select(e => e.Attribute("d")?.Value ?? throw new InvalidOperationException($"Icon '{icon.Name}' contains an element without path data."))
            .ToArray();

        if (pathData.Length == 0)
        {
            throw new InvalidOperationException($"Icon '{icon.Name}' doesn't contain path data.");
        }

        return string.Join(' ', pathData);
    }
}
