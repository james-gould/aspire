import './d3.v7.min.js'

// Layout constants. The node circle is r=56 with its label sitting below it, so the collision radius is
// wider than the circle to keep labels from colliding as well.
const NODE_COLLIDE_RADIUS = 92;
const LAYER_HEIGHT = 230;
const SIBLING_SPACING = 210;

let resourceGraph = null;

export function initializeResourcesGraph(resourcesInterop, graphIcons) {
    resourceGraph = new ResourceGraph(resourcesInterop, graphIcons);
    resourceGraph.resize();

    const observer = new ResizeObserver(function () {
        resourceGraph.resize();
    });

    // The graph container is what actually bounds the drawing area, but it starts hidden while another tab
    // is selected, so the summary layout is observed too to catch the switch back to the graph.
    const graphContainer = document.querySelector('.resource-graph-container');
    if (graphContainer) {
        observer.observe(graphContainer);
    }

    for (const child of document.getElementsByClassName('resources-summary-layout')) {
        observer.observe(child);
    }
}

export function updateResourcesGraph(resources) {
    if (resourceGraph) {
        resourceGraph.updateResources(resources);
    }
}

export function updateResourcesGraphSelected(resourceName) {
    if (resourceGraph) {
        resourceGraph.switchTo(resourceName);
    }
}

class ResourceGraph {
    constructor(resourcesInterop, graphIcons) {
        this.resources = [];
        this.resourcesInterop = resourcesInterop;
        this.openContextMenu = false;

        // Static icon (SVG path + tooltip) shared by every node's context-menu affordance (cog).
        this.menuIcon = graphIcons ? graphIcons.menu : null;

        this.nodes = [];
        this.links = [];

        this.svg = d3.select('.resource-graph');
        this.baseGroup = this.svg.append("g");

        // Set while a node is being dragged. Collision is skipped for that node so it can be moved freely
        // over the top of others instead of being shouldered away by them.
        this.draggingNodeId = null;

        // The view is auto-fitted to the graph until the user zooms or pans, after which their framing is
        // left alone.
        this.userAdjustedView = false;

        // Enable zoom + pan
        // https://www.d3indepth.com/zoom-and-pan/
        // scaleExtent limits zoom to reasonable values
        this.zoom = d3.zoom().scaleExtent([0.1, 4]).on('zoom', (event) => {
            this.baseGroup.attr('transform', event.transform);

            // sourceEvent is only set when the transform came from a real gesture, so programmatic
            // auto-fitting doesn't count as the user taking control of the framing.
            if (event.sourceEvent) {
                this.userAdjustedView = true;
            }
        });
        this.svg.call(this.zoom);

        // simulation setup with all forces
        this.linkForce = d3
            .forceLink()
            .id(function (link) { return link.id })
            .strength(0.25)
            .distance(LAYER_HEIGHT);

        this.simulation = d3
            .forceSimulation()
            .force('link', this.linkForce)
            .force('charge', d3.forceManyBody().strength(-900).distanceMax(700))
            .force("collide", d3.forceCollide((node) => {
                // A node being dragged has no collision radius, so it slides over its neighbours instead of
                // pushing them around. Everything else keeps a radius, which is what stops nodes from
                // sitting on top of each other once the graph is static again.
                return node.id === this.draggingNodeId ? 0 : NODE_COLLIDE_RADIUS;
            }).iterations(4))
            // These two are what turn a floating force layout into a hierarchy. Y is pinned hard to the
            // node's depth so every generation forms a row, while X only nudges each node toward the slot
            // computed for it so collision can still spread crowded rows out.
            .force("y", d3.forceY((node) => node.targetY || 0).strength(1))
            .force("x", d3.forceX((node) => node.targetX || 0).strength(0.25));

        // Drag start is trigger on mousedown from click.
        // Only change the state of the simulation when the drag event is triggered.
        var dragActive = false;
        var dragged = false;
        this.dragDrop = d3.drag().on('start', (event) => {
            dragActive = event.active;
            dragged = false;

            // Reset defensively. If a previous gesture never delivered its end event (the browser losing
            // focus mid-drag will do this) the node would otherwise stay excluded from collision forever.
            this.draggingNodeId = null;

            event.subject.fx = event.subject.x;
            event.subject.fy = event.subject.y;
        }).on('drag', (event) => {
            if (!dragActive) {
                this.simulation.alphaTarget(0.1).restart();
                dragActive = true;
            }
            if (!dragged) {
                dragged = true;

                // Drop the node out of collision for the duration of the drag so it can be moved anywhere,
                // including straight over other nodes, rather than being blocked by whatever is nearby.
                this.draggingNodeId = event.subject.id;
            }
            event.subject.fx = event.x;
            event.subject.fy = event.y;
        }).on('end', (event) => {
            if (dragged) {
                this.simulation.alphaTarget(0);
                dragged = false;
                this.draggingNodeId = null;

                // Keep fx/fy so the node stays where it was dropped instead of springing back to wherever
                // the simulation wants it. Double clicking the node releases it again.
                event.subject.pinned = true;

                // Collision can't move a node that is fixed in place, so anything else already pinned on
                // this spot would stay overlapped forever. Release those back to the simulation and let it
                // push them clear, which keeps the most recent drop as the one that wins.
                this.releasePinnedNodesOverlapping(event.subject);

                this.updateNodePinnedState();
                this.simulation.alpha(0.4).restart();
            }
            else {
                // Mousedown without movement is a click, not a drag, so release the temporary fixing applied
                // on start. Pinning here would make every click on a node pin it.
                if (!event.subject.pinned) {
                    event.subject.fx = null;
                    event.subject.fy = null;
                }
            }
        });

        var defs = this.svg.append("defs");

        // Dot grid that sits under the graph and pans/zooms with it, so the canvas the nodes live on is
        // visible and it's obvious how far the content extends when dragging around.
        var gridPattern = defs.append("pattern")
            .attr("id", "resource-graph-grid")
            .attr("patternUnits", "userSpaceOnUse")
            .attr("width", "40")
            .attr("height", "40");
        gridPattern
            .append("circle")
            .attr("cx", "2")
            .attr("cy", "2")
            .attr("r", "1.5")
            .attr("class", "resource-graph-grid-dot");

        this.createArrowMarker(defs, "arrow-normal", "arrow-normal", 10, 10, 66);
        this.createArrowMarker(defs, "arrow-highlight", "arrow-highlight", 15, 15, 48);
        this.createArrowMarker(defs, "arrow-highlight-expand", "arrow-highlight-expand", 15, 15, 56);

        var highlightedPattern = defs.append("pattern")
            .attr("id", "highlighted-pattern")
            .attr("patternUnits", "userSpaceOnUse")
            .attr("width", "17.5")
            .attr("height", "17.5")
            .attr("patternTransform", "rotate(45)");

        highlightedPattern
            .append("rect")
            .attr("x", "0")
            .attr("y", "0")
            .attr("width", "17.5")
            .attr("height", "17.5")
            .attr("fill", "var(--fill-color)");

        highlightedPattern
            .append("line")
            .attr("x1", "0")
            .attr("y", "0")
            .attr("x2", "0")
            .attr("y2", "17.5")
            .attr("stroke", "var(--neutral-fill-secondary-hover)")
            .attr("stroke-width", "15");

        // The grid is deliberately much larger than any realistic graph so panning never runs off the edge
        // of the drawn canvas.
        this.baseGroup
            .insert("rect", ":first-child")
            .attr("class", "resource-graph-background")
            .attr("x", -20000)
            .attr("y", -20000)
            .attr("width", 40000)
            .attr("height", 40000)
            .attr("fill", "url(#resource-graph-grid)");

        this.linkElementsG = this.baseGroup.append("g").attr("class", "links");
        this.nodeElementsG = this.baseGroup.append("g").attr("class", "nodes");

        this.initializeButtons();
    }

    initializeButtons() {
        d3.select('.graph-zoom-in').on("click", () => this.zoomIn());
        d3.select('.graph-zoom-out').on("click", () => this.zoomOut());
        d3.select('.graph-reset').on("click", () => this.resetZoomAndPan());
    }

    resetZoomAndPan() {
        this.svg.transition().call(this.zoom.transform, d3.zoomIdentity);
        this.unpinAllNodes();
    }

    // Releases every pinned node so the layout is driven by the simulation again.
    unpinAllNodes() {        var hasPinnedNodes = false;
        for (const node of this.nodes) {
            if (node.pinned) {
                node.pinned = false;
                node.fx = null;
                node.fy = null;
                hasPinnedNodes = true;
            }
        }

        if (hasPinnedNodes) {
            this.updateNodePinnedState();
            this.simulation.alpha(0.3).restart();
        }
    }

    // Reflects the pinned state of each node in the DOM so it can be styled.
    updateNodePinnedState() {
        if (!this.nodeElements) {
            return;
        }

        this.nodeElements.classed("resource-group-pinned", n => !!n.pinned);
    }

    // Unpins any node that the supplied node has been dropped on top of. Collision alone can't separate two
    // pinned nodes because neither is free to move, so the older pin yields to the newer one.
    releasePinnedNodesOverlapping(node) {
        const minimumDistance = NODE_COLLIDE_RADIUS * 2;

        for (const other of this.nodes) {
            if (other === node || !other.pinned) {
                continue;
            }

            const dx = (other.x || 0) - (node.x || 0);
            const dy = (other.y || 0) - (node.y || 0);
            if (Math.sqrt(dx * dx + dy * dy) < minimumDistance) {
                other.pinned = false;
                other.fx = null;
                other.fy = null;
            }
        }
    }

    /*
     * Works out where each node belongs in the hierarchy.
     *
     * The graph is a DAG rather than a tree, because a resource can be depended on by several others. To lay
     * it out as a readable hierarchy each node is assigned to the first parent that reaches it in a breadth
     * first walk from the roots, which produces a spanning tree. Links to additional parents still render;
     * they just don't get a say in where the node sits.
     *
     * Depth becomes a fixed row (targetY) and the spanning tree drives a tidy left-to-right ordering
     * (targetX): leaves are laid out in order and each parent is centred over its own children.
     */
    computeHierarchy() {
        const nodesById = new Map(this.nodes.map(n => [n.id, n]));
        const childIds = new Map();
        const hasParent = new Set();

        for (const link of this.links) {
            const source = linkEndId(link.source);
            const target = linkEndId(link.target);
            if (!nodesById.has(source) || !nodesById.has(target)) {
                continue;
            }

            if (!childIds.has(source)) {
                childIds.set(source, []);
            }
            childIds.get(source).push(target);
            hasParent.add(target);
        }

        const roots = this.nodes.filter(n => !hasParent.has(n.id));

        const depth = new Map();
        const treeChildren = new Map();
        const visited = new Set();
        const queue = [];

        for (const root of roots) {
            visited.add(root.id);
            depth.set(root.id, 0);
            queue.push(root.id);
        }

        // Any node left unvisited is only reachable through a cycle, so promote it to a root of its own
        // rather than leaving it without a position.
        for (const node of this.nodes) {
            if (!visited.has(node.id)) {
                visited.add(node.id);
                depth.set(node.id, 0);
                roots.push(node);
                queue.push(node.id);
            }

            // Walk what is reachable so far before considering the next unvisited node, otherwise every
            // member of a cycle gets promoted instead of just the first one.
            while (queue.length > 0) {
                const current = queue.shift();
                const currentDepth = depth.get(current);

                for (const child of (childIds.get(current) || [])) {
                    if (visited.has(child)) {
                        continue;
                    }

                    visited.add(child);
                    depth.set(child, currentDepth + 1);

                    if (!treeChildren.has(current)) {
                        treeChildren.set(current, []);
                    }
                    treeChildren.get(current).push(child);
                    queue.push(child);
                }
            }
        }

        const xById = new Map();
        let nextLeafSlot = 0;

        const assignX = (id) => {
            const children = treeChildren.get(id);
            if (!children || children.length === 0) {
                const x = nextLeafSlot * SIBLING_SPACING;
                nextLeafSlot++;
                xById.set(id, x);
                return x;
            }

            const childXs = children.map(assignX);
            const x = (Math.min(...childXs) + Math.max(...childXs)) / 2;
            xById.set(id, x);
            return x;
        };

        for (const root of roots) {
            assignX(root.id);
        }

        // Centre the laid out tree on the origin so the initial view is balanced.
        const allX = [...xById.values()];
        const xOffset = allX.length > 0 ? (Math.min(...allX) + Math.max(...allX)) / 2 : 0;
        const maxDepth = Math.max(0, ...depth.values());
        const yOffset = (maxDepth * LAYER_HEIGHT) / 2;

        for (const node of this.nodes) {
            node.depth = depth.get(node.id) || 0;
            node.targetX = (xById.get(node.id) || 0) - xOffset;
            node.targetY = (node.depth * LAYER_HEIGHT) - yOffset;

            // Seed brand new nodes on their target so the first frame is already laid out as a hierarchy
            // instead of animating in from the middle of the canvas.
            if (node.x === undefined || node.y === undefined) {
                node.x = node.targetX;
                node.y = node.targetY;
            }
        }

        function linkEndId(end) {
            return typeof end === "object" ? end.id : end;
        }
    }

    // Frames the whole graph in the viewport. Skipped once the user has zoomed or panned so their framing
    // isn't yanked away when a resource changes state.
    fitToView() {
        if (this.userAdjustedView || this.nodes.length === 0) {
            return;
        }

        const container = document.querySelector(".resource-graph-container");
        if (!container || container.clientWidth === 0) {
            return;
        }

        const padding = NODE_COLLIDE_RADIUS;
        const xs = this.nodes.map(n => n.x || 0);
        const ys = this.nodes.map(n => n.y || 0);
        const minX = Math.min(...xs) - padding;
        const maxX = Math.max(...xs) + padding;
        const minY = Math.min(...ys) - padding;
        const maxY = Math.max(...ys) + padding;

        const width = Math.max(maxX - minX, 1);
        const height = Math.max(maxY - minY, 1);

        // Never scale up past 1. A small graph should sit at natural size in the middle rather than being
        // blown up to fill the panel.
        const scale = Math.min(1, container.clientWidth / width, container.clientHeight / height);
        const centerX = (minX + maxX) / 2;
        const centerY = (minY + maxY) / 2;

        const transform = d3.zoomIdentity.scale(scale).translate(-centerX, -centerY);
        this.svg.call(this.zoom.transform, transform);
    }

    zoomIn() {
        this.svg.transition().call(this.zoom.scaleBy, 1.5);
    }

    zoomOut() {
        this.svg.transition().call(this.zoom.scaleBy, 2 / 3);
    }

    createArrowMarker(parent, id, className, width, height, x) {
        parent.append("marker")
            .attr("id", id)
            .attr("viewBox", "0 -5 10 10")
            .attr("refX", x)
            .attr("refY", 0)
            .attr("markerWidth", width)
            .attr("markerHeight", height)
            .attr("orient", "auto")
            .attr("markerUnits", "userSpaceOnUse")
            .attr("class", className)
            .append("path")
            .attr("d", 'M0,-5L10,0L0,5');
    }

    resize() {
        // Measure the graph container rather than the whole summary panel. The panel also contains the tabs
        // row, so measuring it made the drawing area taller than the space the graph actually occupies.
        var container = document.querySelector(".resource-graph-container");
        if (container && container.clientWidth > 0 && container.clientHeight > 0) {
            var width = container.clientWidth;
            var height = container.clientHeight;
            this.svg.attr("viewBox", [-width / 2, -height / 2, width, height]);

            this.fitToView();
        }
    }

    switchTo(resourceName) {
        this.selectedNode = this.nodes.find(node => node.id === resourceName);
        this.updateNodeHighlights(null);
    }

    resourceEqual(r1, r2) {
        if (r1.name !== r2.name) {
            return false;
        }
        if (r1.displayName !== r2.displayName) {
            return false;
        }
        if (!this.iconEqual(r1.resourceIcon, r2.resourceIcon)) {
            return false;
        }
        if (r1.childNames.length !== r2.childNames.length) {
            return false;
        }
        for (var i = 0; i < r1.childNames.length; i++) {
            if (r1.childNames[i] !== r2.childNames[i]) {
                return false;
            }
        }

        return true;
    }

    iconEqual(i1, i2) {
        if (i1.path !== i2.path) {
            return false;
        }
        if (i1.color !== i2.color) {
            return false;
        }
        if (i1.tooltip !== i2.tooltip) {
            return false;
        }

        return true;
    }

    resourcesChanged(existingResource, newResources) {
        if (!existingResource || newResources.length != existingResource.length) {
            return true;
        }

        for (var i = 0; i < newResources.length; i++) {
            if (!this.resourceEqual(newResources[i], existingResource[i], false)) {
                return true;
            }
        }

        return false;
    }

    updateNodes(newResources) {
        const existingNodes = this.nodes || []; // Ensure nodes is initialized
        const updatedNodes = [];

        // calculate degree (number of connections) for each resource
        const degreeMap = new Map();
        newResources.forEach(resource => {
            degreeMap.set(resource.name, resource.childNames.length);
        });

        // also count incoming connections
        newResources.forEach(resource => {
            resource.childNames.forEach(childName => {
                const currentDegree = degreeMap.get(childName) || 0;
                degreeMap.set(childName, currentDegree + 1);
            });
        });

        newResources.forEach(resource => {
            const existingNode = existingNodes.find(node => node.id === resource.name);
            const degree = degreeMap.get(resource.name) || 1;

            if (existingNode) {
                // Spreading the existing node preserves simulation state, including the fx/fy of a node the
                // user has pinned by dragging it.
                updatedNodes.push({
                    ...existingNode,
                    label: resource.displayName,
                    endpointUrl: resource.endpointUrl,
                    endpointText: resource.endpointText,
                    resourceIcon: createIcon(resource.resourceIcon),
                    stateIcon: createIcon(resource.stateIcon),
                    healthState: resource.healthState,
                    degree: degree
                });
            } else {
                // Add new resource
                updatedNodes.push({
                    id: resource.name,
                    label: resource.displayName,
                    endpointUrl: resource.endpointUrl,
                    endpointText: resource.endpointText,
                    resourceIcon: createIcon(resource.resourceIcon),
                    stateIcon: createIcon(resource.stateIcon),
                    healthState: resource.healthState,
                    degree: degree
                });
            }
        });

        this.nodes = updatedNodes;

        function createIcon(resourceIcon) {
            return {
                path: resourceIcon.path,
                color: resourceIcon.color,
                tooltip: resourceIcon.tooltip
            };
        }
    }

    updateResources(newResources) {
        // Check if the overall structure of the graph has changed. i.e. nodes or links have been added or removed.
        var hasStructureChanged = this.resourcesChanged(this.resources, newResources);

        this.resources = newResources;

        this.updateNodes(newResources);

        this.links = [];
        var healthStateByName = new Map(newResources.map(r => [r.name, r.healthState]));
        for (var i = 0; i < newResources.length; i++) {
            var resource = newResources[i];

            var resourceLinks = resource.childNames
                .filter((childName) => {
                    return newResources.some(r => r.name === childName);
                })
                .map((childName, index) => {
                    return {
                        id: `${resource.name}-${childName}`,
                        target: childName,
                        source: resource.name,
                        // The link takes the child's rolled up state. Because the child's state already
                        // includes everything below it, an unhealthy leaf colours every link on the path
                        // back to the root without any extra propagation here.
                        healthState: healthStateByName.get(childName),
                        strength: 0.7
                    };
                });

            this.links.push(...resourceLinks);
        }

        // Positions have to be resolved before the nodes are rendered so brand new nodes can be seeded on
        // their place in the hierarchy rather than flying in from the origin.
        this.computeHierarchy();

        // Update nodes
        this.nodeElements = this.nodeElementsG
            .selectAll(".resource-group")
            .data(this.nodes, n => n.id);

        // Remove excess nodes:
        this.nodeElements
            .exit()
            .transition()
            .attr("opacity", 0)
            .remove();

        // Resource node
        var newNodes = this.nodeElements
            .enter().append("g")
            .attr("class", "resource-group")
            .attr("opacity", 0)
            .attr("resource-name", n => n.id)
            .call(this.dragDrop);

        var newNodesContainer = newNodes
            .append("g")
            .attr("class", "resource-scale")
            .on('click', this.selectNode)
            .on('dblclick', this.unpinNode)
            .on('contextmenu', this.nodeContextMenu)
            .on('mouseover', this.hoverNode)
            .on('mouseout', this.unHoverNode);
        newNodesContainer
            .append("circle")
            .attr("r", 56)
            .attr("class", "resource-node")
            .attr("stroke", "white")
            .attr("stroke-width", "4");
        newNodesContainer
            .append("circle")
            .attr("r", 53)
            .attr("class", "resource-node-border");
        var iconTransform = newNodesContainer
            .append("g")
            .attr("transform", n => n.endpointText ? "translate(-24,-37)" : "translate(-24,-24)")
        var iconPath = iconTransform
            .append("path");
        iconPath
            .attr("fill", n => n.resourceIcon.color)
            .attr("d", n => n.resourceIcon.path)
            .append("title")
            .text(n => n.resourceIcon.tooltip);

        // Icon paths could be mixed size. We need to transform icons to always be displayed at a consistent size.
        iconPath.each(function (d) {
            const iconSize = 48;

            const path = d3.select(this);
            const node = path.node();
            const bbox = node.getBBox();

            const available = Math.max(1, iconSize - 2);
            const scale = available / Math.max(bbox.width, bbox.height);

            const cx = bbox.x + bbox.width / 2;
            const cy = bbox.y + bbox.height / 2;

            // apply scaling & centering inside this group
            path.attr("transform",
                `translate(${iconSize / 2},${iconSize / 2}) scale(${scale}) translate(${-cx},${-cy})`);
        });

        var endpointGroup = newNodesContainer
            .append("g")
            .attr("transform", "translate(0,28)")
            .attr("class", "resource-endpoint")
            .style("display", n => n.endpointText ? null : "none");
        endpointGroup.append("text");
        endpointGroup.append("title");

        // Resource status
        var statusGroup = newNodesContainer
            .append("g")
            .attr("transform", "scale(1.6) translate(14,-34)");
        statusGroup
            .append("circle")
            .attr("r", 8)
            .attr("cy", 8)
            .attr("cx", 8)
            .attr("class", "resource-status-circle")
            .append("title");
        statusGroup
            .append("path")
            .attr("class", "resource-status-path")
            .append("title");

        var resourceNameGroup = newNodesContainer
            .append("g")
            .attr("transform", "translate(0,71)")
            .attr("class", "resource-name");
        resourceNameGroup
            .append("text")
            .text(n => trimText(n.label, 30));
        resourceNameGroup
            .append("title")
            .text(n => n.label);

        // Context menu affordance. A cog positioned on the circle rim directly below the status badge.
        // The status badge sits at the top-right via "scale(1.6) translate(14,-34)" (center ~(35,-42)),
        // so mirroring it vertically puts the cog at the bottom-right ~(35,43). Hidden until the node is
        // hovered (see .resource-menu-cog CSS); it makes the node's interactivity discoverable by opening
        // the same context menu as right-clicking the node.
        var cogGroup = newNodesContainer
            .append("g")
            .attr("class", "resource-menu-cog")
            .attr("id", n => `resource-menu-cog-${n.id}`)
            .attr("transform", "translate(35,43)")
            .attr("role", "button")
            .attr("tabindex", 0)
            .attr("aria-label", n => this.getResourceMenuLabel(n))
            .attr("aria-haspopup", "menu")
            .attr("aria-expanded", "false")
            // D3's drag handler is attached to the ancestor resource group. Stop drag-start
            // events here so an imprecise cog click can never move the resource node.
            .on('mousedown touchstart', event => event.stopPropagation())
            .on('keydown', this.cogMenuKeyDown)
            .on('click', this.cogMenuClick);
        cogGroup
            .append("circle")
            .attr("r", 14)
            .attr("class", "resource-menu-cog-background");
        cogGroup
            .append("title")
            .text(n => this.getResourceMenuLabel(n));
        if (this.menuIcon) {
            var cogIcon = cogGroup
                .append("path")
                .attr("class", "resource-menu-cog-icon")
                .attr("d", this.menuIcon.path);

            // Scale and center the icon inside the cog background, matching how resource icons are sized.
            cogIcon.each(function () {
                const iconSize = 16;
                const path = d3.select(this);
                const bbox = this.getBBox();
                const scale = iconSize / Math.max(bbox.width, bbox.height);
                const cx = bbox.x + bbox.width / 2;
                const cy = bbox.y + bbox.height / 2;

                path.attr("transform", `scale(${scale}) translate(${-cx},${-cy})`);
            });
        }

        newNodes.transition()
            .attr("opacity", 1);

        this.nodeElements = newNodes.merge(this.nodeElements);

        // Set resource values that change.
        this.nodeElementsG
            .selectAll(".resource-group")
            .attr("data-health", n => n.healthState);
        this.nodeElementsG
            .selectAll(".resource-group")
            .select(".resource-menu-cog")
            .attr("aria-label", n => this.getResourceMenuLabel(n))
            .select("title")
            .text(n => this.getResourceMenuLabel(n));
        this.nodeElementsG
            .selectAll(".resource-group")
            .select(".resource-endpoint")
            .style("display", n => n.endpointText ? null : "none")
            .select("text")
            .text(n => trimText(n.endpointText, 15));
        this.nodeElementsG
            .selectAll(".resource-group")
            .select(".resource-endpoint")
            .select("title")
            .text(n => n.endpointText || "");
        this.nodeElementsG
            .selectAll(".resource-group")
            .select(".resource-status-circle")
            .select("title")
            .text(n => n.stateIcon.tooltip);
        this.nodeElementsG
            .selectAll(".resource-group")
            .select(".resource-status-path")
            .attr("d", n => n.stateIcon.path)
            .attr("fill", n => n.stateIcon.color)
            .select("title")
            .text(n => n.stateIcon.tooltip);

        // Update links
        this.linkElements = this.linkElementsG
            .selectAll("line")
            .data(this.links, (d) => { return d.id; });

        this.linkElements
            .exit()
            .transition()
            .attr("opacity", 0)
            .remove();

        var newLinks = this.linkElements
            .enter().append("line")
            .attr("opacity", 0)
            .attr("class", "resource-link");

        newLinks.transition()
            .attr("opacity", 1);

        this.linkElements = newLinks.merge(this.linkElements);

        // Health is refreshed on every update because a resource can change state without the shape of the
        // graph changing at all.
        this.linkElements.attr("data-health", l => l.healthState);

        this.updateNodePinnedState();

        this.simulation
            .nodes(this.nodes)
            .on('tick', this.onTick);

        this.simulation.force("link").links(this.links);
        if (hasStructureChanged) {
            this.simulation.stop();

            // Set alpha (give energy) and simulate the graph before rendering.
            // This prevents the graph from jumping around when loaded or changed.
            this.simulation.alpha(1);
            for (let i = 0; i < 300; i++) {
                this.simulation.tick();
            }

            this.onTick();
            this.fitToView();
        }

        this.simulation.restart();

        function trimText(text, maxLength) {
            if (!text) {
                return "";
            }
            if (text.length > maxLength) {
                return text.slice(0, maxLength) + "\u2026";
            }
            return text;
        }
    }

    onTick = () => {
        this.nodeElements.attr("transform", function (d) { return "translate(" + d.x + "," + d.y + ")"; });
        this.linkElements
            .attr('x1', function (link) { return link.source.x })
            .attr('y1', function (link) { return link.source.y })
            .attr('x2', function (link) { return link.target.x })
            .attr('y2', function (link) { return link.target.y });
    }

    getNeighbors(node) {
        return this.links.reduce(function (neighbors, link) {
            if (link.target.id === node.id) {
                neighbors.push(link.source.id);
            } else if (link.source.id === node.id) {
                neighbors.push(link.target.id);
            }
            return neighbors;
        },
            [node.id]);
    }

    getResourceMenuLabel(node) {
        return this.menuIcon ? this.menuIcon.labelFormat.split('{0}').join(node.label) : "";
    }

    isNeighborLink(node, link) {
        return link.target.id === node.id || link.source.id === node.id
    }

    getLinkClass(nodes, selectedNode, link) {
        if (nodes.find(n => this.isNeighborLink(n, link))) {
            if (this.nodeEquals(selectedNode, link.target)) {
                return 'resource-link-highlight-expand';
            }
            return 'resource-link-highlight';
        }
        return 'resource-link';
    }

    nodeContextMenu = async (event) => {
        var data = event.target.__data__;

        // Prevent default browser context menu.
        event.preventDefault();

        await this.openResourceContextMenu(data.id, event.clientX, event.clientY, null, null);
    };

    cogMenuClick = async (event) => {
        // currentTarget is the cog group the handler is attached to. Its datum is inherited from the
        // node group (d3 propagates data to appended children).
        var data = event.currentTarget.__data__;

        // Stop the click from also reaching the node's click handler (which would select/deselect it).
        event.preventDefault();
        event.stopPropagation();

        await this.openResourceContextMenu(data.id, event.clientX, event.clientY, event.currentTarget, null);
    };

    cogMenuKeyDown = async (event) => {
        if (event.repeat || (event.key !== 'Enter' && event.key !== ' ')) {
            return;
        }

        event.preventDefault();
        event.stopPropagation();

        var data = event.currentTarget.__data__;
        var bounds = event.currentTarget.getBoundingClientRect();
        await this.openResourceContextMenu(
            data.id,
            Math.round(bounds.left + bounds.width / 2),
            Math.round(bounds.top + bounds.height / 2),
            event.currentTarget,
            event.currentTarget.id);
    };

    openResourceContextMenu = async (id, clientX, clientY, trigger, focusElementId) => {
        this.openContextMenu = true;
        trigger?.setAttribute("aria-expanded", "true");

        try {
            // Wait for method completion. It completes when the context menu is closed.
            await this.resourcesInterop.invokeMethodAsync('ResourceContextMenu', id, window.innerWidth, window.innerHeight, clientX, clientY, focusElementId);
        } finally {
            this.openContextMenu = false;
            trigger?.setAttribute("aria-expanded", "false");

            // Unselect the node when the context menu is closed to reset mouseover state.
            this.updateNodeHighlights(null);
        }
    };

    selectNode = (event) => {
        var data = event.target.__data__;

        // Always send the clicked on resource to the server. It will clear the selection if the same resource is clicked again.
        this.resourcesInterop.invokeMethodAsync('SelectResource', data.id);

        // Unscale the previous selected node.
        if (this.selectedNode) {
            changeScale(this, this.selectedNode.id, 1);
        }

        // Scale selected node if it is not the same as the previous selected node.
        var clearSelection = this.nodeEquals(data, this.selectedNode);
        if (!clearSelection) {
            changeScale(this, data.id, 1.2);
        }

        this.selectedNode = data;

        function changeScale(self, id, scale) {
            let match = self.nodeElementsG
                .selectAll(".resource-group")
                .filter(function (d) {
                    return d.id == id;
                });

            match
                .select(".resource-scale")
                .transition()
                .duration(300)
                .style("transform", `scale(${scale})`)
                .on("end", s => {
                    match.select(".resource-scale").style("transform", null);
                    self.updateNodeHighlights(null);
                });
        }
    }

    hoverNode = (event) => {
        var mouseoverNode = event.target.__data__;

        this.updateNodeHighlights(mouseoverNode);
    }

    // Releases a node pinned by dragging so the simulation can lay it out again.
    unpinNode = (event) => {
        // Child elements keep the datum they were appended with, and updateNodes replaces node objects on
        // every refresh, so the datum reachable from the event can be a stale copy. Only the id is stable,
        // so the live node is looked up from the simulation's own array.
        var id = event.target.__data__?.id;
        var node = id ? this.nodes.find(n => n.id === id) : null;
        if (!node || !node.pinned) {
            return;
        }

        // The zoom behavior also handles dblclick. Without this the graph would zoom in while unpinning.
        event.preventDefault();
        event.stopPropagation();

        node.pinned = false;
        node.fx = null;
        node.fy = null;

        this.updateNodePinnedState();
        this.simulation.alpha(0.3).restart();
    }

    unHoverNode = (event) => {
        // Don't unhover the selected node when the context menu is open.
        // This is done to keep the node selected until the context menu is closed.
        if (!this.openContextMenu) {
            this.updateNodeHighlights(null);
        }
    };

    nodeEquals(resource1, resource2) {
        if (!resource1 || !resource2) {
            return false;
        }
        return resource1.id === resource2.id;
    }

    updateNodeHighlights = (mouseoverNode) => {
        var mouseoverNeighbors = mouseoverNode ? this.getNeighbors(mouseoverNode) : [];
        var selectNeighbors = this.selectedNode ? this.getNeighbors(this.selectedNode) : [];
        var neighbors = [...mouseoverNeighbors, ...selectNeighbors];

        // we modify the styles to highlight selected nodes
        this.nodeElements.attr('class', (node) => {
            var classNames = ['resource-group'];
            if (this.nodeEquals(node, mouseoverNode)) {
                classNames.push('resource-group-hover');
            }
            if (this.nodeEquals(node, this.selectedNode)) {
                classNames.push('resource-group-selected');
            }
            if (neighbors.indexOf(node.id) > -1) {
                classNames.push('resource-group-highlight');
            }
            // The class attribute is rebuilt from scratch here, so the pinned marker has to be reapplied
            // or dragging a node and then hovering any node would silently unpin it visually.
            if (node.pinned) {
                classNames.push('resource-group-pinned');
            }
            return classNames.join(' ');
        });
        this.linkElements.attr('class', (link) => {
            var nodes = [];
            if (mouseoverNode) {
                nodes.push(mouseoverNode);
            }
            if (this.selectedNode) {
                nodes.push(this.selectedNode);
            }
            return this.getLinkClass(nodes, this.selectedNode, link);
        });
    };
};
