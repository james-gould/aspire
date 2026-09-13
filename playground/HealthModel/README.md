# Health model playground

A container-free sample for iterating on the dashboard's resource graph and health model.
The resources are simulated; the database servers, APIs, and gateways do not start real services.

From the repository root, run:

```powershell
dotnet run --project playground\HealthModel\HealthModelSandbox.AppHost\HealthModelSandbox.AppHost.csproj
```

Open the login URL printed by the AppHost, then select **Health**.
The AppHost uses the repository's dashboard so local UI changes are included.
Keep the terminal open while using the playground; stop it with **Ctrl+C**.

## Topology and health

Each resource has one incoming relationship. APIs reference their branch's database server;
the database declares that server as its parent. This avoids a database appearing under both
the API and a separate top-level server.

```text
AppHost
  storefront
    checkout-api
      orders-db-server
        orders-db
      payments-gateway       Degraded
    catalog-api
      catalog-db-server
        catalog-db
      search-index           Unhealthy
    identity-api
      identity-cache
```

All other resources report Healthy. Once startup completes, the graph should show:

| Entity | Aggregate health | Cause |
|---|---|---|
| checkout-api | Degraded | payments-gateway |
| catalog-api | Unhealthy | search-index |
| identity-api | Healthy | Healthy dependencies |
| storefront / AppHost | Unhealthy | catalog-api |

To try a different scenario, change a leaf's `HealthStatus` in `AppHost.cs` and restart.
Both **Health** and **Resources > Graph** derive relationships from the AppHost. Health no
longer invents Services/Infrastructure groups or automatically limits the impact of containers.

## Health model lite

The Health page follows the
[Azure Monitor graph and entity inspection workflow](https://learn.microsoft.com/azure/azure-monitor/health-models/analyze-health)
and a restricted version of its
[designer](https://learn.microsoft.com/azure/azure-monitor/health-models/designer).

- **Graph** shows live, aggregated health on rectangular entity cards. Select a card to see
  its own signals, dependencies, parents and the health it propagates.
- **Entities** presents the same entities as a searchable list. State-count buttons filter
  the list or dim nonmatching cards without removing relationship context.
- **Designer** lets you move cards, edit their display names, impact, health objectives and
  dependency rollup rules. Apply entity edits to the draft, then use **Save changes** to persist.
- **Arrange** restores a deterministic hierarchical layout. **Undo** and **Discard changes**
  operate on the draft; live health updates do not reset the layout.
- Drag cards only in Designer, or focus a card and use arrow keys to move it (Shift moves
  further). Positions are canvas coordinates, not screen pixels or zoom transforms.

Saved settings are scoped to the application in this browser's local storage. They are
not automatically written into the AppHost project and are not shared across browsers.
Export the saved model to keep a project-owned copy.

### Portable model definition

**Export model** downloads `aspire-healthmodel.json`. Add that file to your AppHost project
when you want to retain the design alongside your code. **Import model** restores its
positions and propagation settings as an unsaved draft. Imports must match the current
AppHost's application name, resource bindings, entities, relationships and local signals.
Changing topology remains an AppHost operation, not a browser-only edit.

The version 1 document contains:

| Field | Purpose |
|---|---|
| `name` | Model identifier; also identifies the root entity |
| `applicationName` | Prevents applying another application's settings |
| `entities[].name` | Stable, Azure-compatible identity, independent of runtime suffixes |
| `aspireResourceName`, `replicaIndex` | Binding back to the corresponding AppHost resource |
| `canvasPosition` | Saved X/Y coordinates, preserved by export and import |
| `impact`, `dependencies`, `healthObjective` | Declarative health propagation settings |
| `localSignals` | Local signal names and kinds, without readings or exception details |
| `relationships` | Parent and child entity identities |

No environment variables, credentials, endpoint addresses, observed health values, or
exception text are exported. Root identity, topology and positions are deliberately separate
from the live signal readings.

### Publishing boundary

The export is a **portable definition, not an ARM/Bicep deployment template**. This iteration
does not register an Azure publisher and does not create cloud resources. A future publisher
should consume the project-owned definition rather than reconstructing a different graph:
emit entities using its stable identities, emit the same relationships, and copy
`canvasPosition`, impact and dependency settings to the Azure model.

Local lifecycle and health-check signals need explicit cloud equivalents (metrics, queries,
or an external signal producer). The publisher must also bind AppHost resources to deployed
ARM resource IDs and configure authentication. A matching picture alone does not establish
equivalent cloud health evaluation.

The initial documented Bicep target is
[`Microsoft.CloudHealth/healthmodels@2026-05-01-preview`](https://learn.microsoft.com/azure/templates/microsoft.cloudhealth/2026-05-01-preview/healthmodels).
The health model is **a separate Azure resource**, not a workspace or a child of a workspace:

| Resource | Purpose |
|---|---|
| `Microsoft.CloudHealth/healthmodels` | Owns entities, relationships and health configuration |
| `Microsoft.Monitor/accounts` | Optional Azure Monitor workspace for Prometheus/PromQL signals |
| `Microsoft.OperationalInsights/workspaces` | Optional Log Analytics workspace for KQL signals |

A future publisher should create the appropriate signal sources rather than automatically
creating both workspace types. Azure resource metrics can reference the monitored resource directly.

Important integration constraints:

- Azure creates its root entity with the **model's name**. The publisher must keep that identity
  consistent with the root referenced by the exported relationships.
- Relationship endpoints use entity resource names. Rewiring requires replacing the relationship,
  not updating its endpoints in place.
- The local editor preserves X/Y values independently of zoom. The REST schema defines floating-point
  coordinates while the generated Bicep reference presents integers, and the portal's coordinate
  origin/anchor is not documented. Validate the conversion against Azure before claiming identical
  positioning; do not silently round exported coordinates.
- `signalGroups.external` is read-only. Aspire health-check results require ongoing
  [health-report ingestion](https://learn.microsoft.com/azure/azure-monitor/health-models/health-report-ingestion)
  or an explicit metric/query equivalent. Bicep cannot provision a persistent external health result.
- The local threshold evaluator follows the inclusive comparisons in the pinned API schema.
  Conceptual examples differ at equality, and some Unknown-state edge cases are underspecified.
  Cloud execution parity still needs service-level validation.

This preview does not include historical timelines, alert delivery, Azure discovery,
cloud metric/query execution or arbitrary browser-defined entities and relationships.
Cyclic AppHost references remain inspectable in **Resources > Graph**, but the Health
designer reports them as unsupported rather than silently dropping edges. Requiring an acyclic,
root-connected topology is a local lite-product restriction, not a claim that Azure prohibits
every other topology.

## Resource graph controls

- Drag a node to position and pin it. Physics pauses while dragging, allowing overlap;
  after release, neighbours separate. Dropping onto an older pinned node releases that older pin.
- Double-click a pinned node to release it.
- Zoom with the wheel or the zoom buttons; drag the background to pan.
- **Reset** clears pins, restores the hierarchical layout, and fits all nodes and labels into view.
- Focus a resource's action cog and press **Enter** to use its menu with the keyboard;
  **Escape** closes the menu and returns focus to the cog.

Known health states keep their colour on hover and selection: solid relationship lines,
full-colour node outlines, and a 30% background tint.
