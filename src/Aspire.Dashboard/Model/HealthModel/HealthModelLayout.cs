// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Dashboard.Model.HealthModel;

internal static class HealthModelLayout
{
    public const double CardWidth = 224;
    public const double CardHeight = 104;
    private const double ColumnSpacing = 272;
    private const double RowSpacing = 180;

    public static Dictionary<string, HealthModelCanvasPosition> Arrange(HealthModelDefinition definition)
    {
        var topology = HealthModelTopology.Create(definition);
        var positions = new Dictionary<string, HealthModelCanvasPosition>(StringComparer.Ordinal);
        var entities = definition.Entities.ToDictionary(e => e.Name, StringComparer.Ordinal);
        var parents = definition.Relationships.GroupBy(r => r.ChildEntityName, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(r => r.ParentEntityName)
                .OrderByDescending(n => topology.Depth[n]).ThenBy(n => n, StringComparer.Ordinal).First(), StringComparer.Ordinal);
        var treeChildren = parents.ToLookup(pair => pair.Value, pair => pair.Key, StringComparer.Ordinal);
        var roots = topology.Order.Where(e => !parents.ContainsKey(e.Name)).Select(e => e.Name).ToArray();
        var pending = new Stack<(string Name, bool Visited)>(Enumerable.Reverse(roots).Select(n => (n, false)));
        var nextSlot = 0;
        while (pending.TryPop(out var item))
        {
            var children = treeChildren[item.Name]
                .OrderBy(n => entities[n].DisplayName ?? n, StringComparer.Ordinal).ToArray();
            if (children.Length == 0)
            {
                positions[item.Name] = new(nextSlot++ * ColumnSpacing, topology.Depth[item.Name] * RowSpacing);
            }
            else if (item.Visited)
            {
                // A shared dependency has one positioning parent but keeps every relationship. Centre
                // parents over their own branch instead of alphabetizing unrelated nodes across rows.
                positions[item.Name] = new(
                    (positions[children[0]].X + positions[children[^1]].X) / 2,
                    topology.Depth[item.Name] * RowSpacing);
            }
            else
            {
                pending.Push((item.Name, true));
                foreach (var child in Enumerable.Reverse(children))
                {
                    pending.Push((child, false));
                }
            }
        }
        var offset = positions.Count == 0 ? 0 : (positions.Values.Min(p => p.X) + positions.Values.Max(p => p.X)) / 2;
        return positions.ToDictionary(pair => pair.Key, pair => pair.Value with { X = pair.Value.X - offset }, StringComparer.Ordinal);
    }

    public static HealthModelCanvasPosition Place(HealthModelDocument document, string name, HealthModelCanvasPosition requested)
    {
        if (!HealthModelDocuments.IsValidPosition(requested))
        {
            throw new InvalidDataException("The canvas position must contain finite coordinates within the supported canvas.");
        }
        var otherPositions = document.Entities.Where(e => e.Name != name).Select(e => e.CanvasPosition).ToArray();
        var snapped = new HealthModelCanvasPosition(Math.Round(requested.X / 8) * 8, Math.Round(requested.Y / 8) * 8);
        if (IsFree(snapped))
        {
            return snapped;
        }

        // Keep the other saved positions intact. Find a nearby free slot for the dropped card instead
        // of letting a force simulation rearrange positions that must round-trip to Azure.
        for (var radius = 1; radius <= document.Entities.Length + 1; radius++)
        {
            for (var x = -radius; x <= radius; x++)
            {
                foreach (var y in new[] { -radius, radius })
                {
                    var candidate = new HealthModelCanvasPosition(snapped.X + x * ColumnSpacing, snapped.Y + y * RowSpacing);
                    if (IsFree(candidate))
                    {
                        return candidate;
                    }
                }
            }
        }
        throw new InvalidDataException("There is no available position on the canvas.");

        bool IsFree(HealthModelCanvasPosition position) => HealthModelDocuments.IsValidPosition(position) &&
            otherPositions.All(other => Math.Abs(other.X - position.X) >= CardWidth + 16 || Math.Abs(other.Y - position.Y) >= CardHeight + 16);
    }
}
