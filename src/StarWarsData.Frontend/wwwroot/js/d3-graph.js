// D3.js unified graph renderer — force-directed (default) and hierarchical tree layouts
// Supports in-place node expansion, navigation history, fullscreen
// Tree layout: infers hierarchy from BFS depth — works with any edge labels

const TYPE_COLORS = {
    Character: '#7e6fff',
    Organization: '#ff6f91',
    CelestialBody: '#4ecdc4',
    Planet: '#4ecdc4',
    System: '#3dc1d3',
    Sector: '#2d98da',
    Region: '#1e90ff',
    Species: '#ffd93d',
    Starship: '#6bcb77',
    StarshipClass: '#6bcb77',
    Vehicle: '#6bcb77',
    Battle: '#ff6b6b',
    War: '#ff4757',
    Campaign: '#ff6348',
    Weapon: '#ff9f43',
    Droid: '#a29bfe',
    DroidSeries: '#a29bfe',
    Event: '#fd79a8',
    Food: '#fdcb6e',
    ForcePower: '#a29bfe',
    Government: '#e056fd',
    Military_unit: '#7bed9f',
    Fleet: '#70a1ff',
    City: '#ffa502',
    Structure: '#747d8c',
    Location: '#2ed573',
    Company: '#ff6348',
    IndividualShip: '#6bcb77',
};

function getTypeColor(type) {
    return TYPE_COLORS[type] || '#7a7a9e';
}

const WIKI_THUMB = (url) => {
    if (!url) return null;
    const base = url.split('/revision')[0];
    return base + '/revision/latest/scale-to-width-down/50';
};

// Module-level state for the active graph
let _state = null;

/**
 * Render a force-directed graph into the given container element.
 * Supports incremental expansion via expandNode().
 */
export function renderForceGraph(containerId, data, dotnetRef) {
    const container = document.getElementById(containerId);
    if (!container) return;

    // Clear previous using DOM methods (safe)
    while (container.firstChild) container.removeChild(container.firstChild);

    const width = container.clientWidth || 900;
    const height = container.clientHeight || 650;

    const svg = d3.select(container)
        .append('svg')
        .attr('width', '100%')
        .attr('height', '100%')
        .attr('viewBox', [0, 0, width, height]);

    const defs = svg.append('defs');
    defs.append('marker')
        .attr('id', 'arrowhead')
        .attr('viewBox', '0 -5 10 10')
        .attr('refX', 36).attr('refY', 0)
        .attr('markerWidth', 8).attr('markerHeight', 8)
        .attr('orient', 'auto')
        .append('path')
        .attr('d', 'M0,-5L10,0L0,5')
        .attr('fill', '#5a5a8a');

    const zoomGroup = svg.append('g');
    const zoomBehavior = d3.zoom()
        .scaleExtent([0.1, 6])
        .on('zoom', (event) => zoomGroup.attr('transform', event.transform));
    svg.call(zoomBehavior);

    const linkGroup = zoomGroup.append('g').attr('class', 'links');
    const nodeGroup = zoomGroup.append('g').attr('class', 'nodes');

    // State
    const nodeMap = new Map();
    const edgeSet = new Set();
    const nodesData = [];
    const linksData = [];

    addData(data);

    const isTreeLayout = data.layoutMode === 'tree';
    let simulation = null;

    if (isTreeLayout) {
        // ── Tree layout: deterministic positions from BFS depth ──
        computeBfsDepths(nodesData, linksData, data.rootId);
        layoutTreePositions(nodesData, width, height);
    } else {
        // ── Force layout: physics simulation ──
        simulation = d3.forceSimulation(nodesData)
            .force('link', d3.forceLink(linksData).id(d => d.id).distance(220).strength(0.2))
            .force('charge', d3.forceManyBody().strength(-600).distanceMax(800))
            .force('center', d3.forceCenter(width / 2, height / 2).strength(0.03))
            .force('collision', d3.forceCollide().radius(80))
            .force('x', d3.forceX(width / 2).strength(0.02))
            .force('y', d3.forceY(height / 2).strength(0.02));

        simulation.on('tick', tick);
    }

    _state = {
        container, svg, defs, zoomGroup, zoomBehavior, linkGroup, nodeGroup,
        simulation, nodeMap, edgeSet, nodesData, linksData, dotnetRef,
        width, height, currentRootId: data.rootId, isTreeLayout,
    };

    updateVisuals();

    if (isTreeLayout) {
        // For tree layout, position everything immediately then fit to screen
        drawTreeEdges();
        nodeGroup.selectAll('g.node')
            .attr('transform', d => `translate(${d.x},${d.y})`);
        setTimeout(() => fitToScreen(), 100);
    }

    function addData(data) {
        for (const n of data.nodes) {
            if (!nodeMap.has(n.id)) {
                const node = { ...n, isRoot: n.id === data.rootId, expanded: n.id === data.rootId };
                nodeMap.set(n.id, node);
                nodesData.push(node);
            }
        }
        for (const e of data.edges) {
            const key = `${e.fromId}-${e.toId}-${e.label}`;
            if (!edgeSet.has(key) && nodeMap.has(e.fromId) && nodeMap.has(e.toId)) {
                edgeSet.add(key);
                linksData.push({
                    source: e.fromId, target: e.toId,
                    label: e.label || '', weight: e.weight || 0.5,
                    fromYear: e.fromYear ?? null, toYear: e.toYear ?? null,
                });
            }
        }
    }

    function tick() {
        linkGroup.selectAll('line')
            .attr('x1', d => d.source.x).attr('y1', d => d.source.y)
            .attr('x2', d => d.target.x).attr('y2', d => d.target.y);
        linkGroup.selectAll('text')
            .attr('x', d => (d.source.x + d.target.x) / 2)
            .attr('y', d => (d.source.y + d.target.y) / 2);
        nodeGroup.selectAll('g.node')
            .attr('transform', d => `translate(${d.x},${d.y})`);
    }

    function drawTreeEdges() {
        // Replace straight lines with elbow connectors for tree layout
        linkGroup.selectAll('line').remove();
        linkGroup.selectAll('text').remove();

        linkGroup.selectAll('path.tree-edge')
            .data(linksData, d => `${d.source.id ?? d.source}-${d.target.id ?? d.target}-${d.label}`)
            .enter()
            .append('path')
            .attr('class', 'tree-edge')
            .attr('d', d => {
                const src = typeof d.source === 'object' ? d.source : nodeMap.get(d.source);
                const tgt = typeof d.target === 'object' ? d.target : nodeMap.get(d.target);
                if (!src || !tgt) return '';
                const midY = (src.y + tgt.y) / 2;
                return `M${src.x},${src.y + 30} L${src.x},${midY} L${tgt.x},${midY} L${tgt.x},${tgt.y - 30}`;
            })
            .attr('fill', 'none')
            .attr('stroke', '#3a3a6a')
            .attr('stroke-width', 1.5)
            .attr('stroke-opacity', 0.6)
            .attr('marker-end', 'url(#arrowhead)');

        // Edge labels — position above each target node
        linkGroup.selectAll('text.tree-label')
            .data(linksData, d => `${d.source.id ?? d.source}-${d.target.id ?? d.target}-${d.label}`)
            .enter()
            .append('text')
            .attr('class', 'tree-label')
            .attr('x', d => {
                const tgt = typeof d.target === 'object' ? d.target : nodeMap.get(d.target);
                return tgt ? tgt.x : 0;
            })
            .attr('y', d => {
                const tgt = typeof d.target === 'object' ? d.target : nodeMap.get(d.target);
                return tgt ? tgt.y - 42 : 0;
            })
            .text(d => formatLabel(d.label))
            .attr('font-size', '9px')
            .attr('fill', '#8a8ab0')
            .attr('text-anchor', 'middle')
            .style('pointer-events', 'none');
    }
}

/**
 * Compute BFS depths from root through edges.
 * Assigns _depth to each node (root=0, connections=1, etc.)
 */
function computeBfsDepths(nodes, links, rootId) {
    const adj = new Map();
    for (const n of nodes) adj.set(n.id, []);

    for (const link of links) {
        const srcId = link.source.id ?? link.source;
        const tgtId = link.target.id ?? link.target;
        if (adj.has(srcId)) adj.get(srcId).push(tgtId);
        if (adj.has(tgtId)) adj.get(tgtId).push(srcId);
    }

    const visited = new Set();
    const queue = [{ id: rootId, depth: 0 }];
    const depthMap = new Map();
    visited.add(rootId);
    depthMap.set(rootId, 0);

    while (queue.length > 0) {
        const { id, depth } = queue.shift();
        for (const neighbor of (adj.get(id) || [])) {
            if (!visited.has(neighbor)) {
                visited.add(neighbor);
                depthMap.set(neighbor, depth + 1);
                queue.push({ id: neighbor, depth: depth + 1 });
            }
        }
    }

    for (const n of nodes) {
        n._depth = depthMap.get(n.id) ?? 0;
    }
}

/**
 * Position nodes in rows by BFS depth for tree layout.
 */
function layoutTreePositions(nodes, width, height) {
    const ROW_HEIGHT = 180;
    const MIN_NODE_SPACING = 160;

    // Group by depth
    const byDepth = new Map();
    for (const n of nodes) {
        const d = n._depth ?? 0;
        if (!byDepth.has(d)) byDepth.set(d, []);
        byDepth.get(d).push(n);
    }

    const maxDepth = Math.max(...byDepth.keys(), 0);
    const totalHeight = (maxDepth + 1) * ROW_HEIGHT;
    const startY = Math.max(80, (height - totalHeight) / 2);

    for (const [depth, row] of byDepth) {
        const rowWidth = row.length * MIN_NODE_SPACING;
        const startX = (width - rowWidth) / 2 + MIN_NODE_SPACING / 2;

        row.forEach((n, i) => {
            n.x = startX + i * MIN_NODE_SPACING;
            n.y = startY + depth * ROW_HEIGHT;
            // Fix positions so force sim (if ever mixed) doesn't move them
            n.fx = n.x;
            n.fy = n.y;
        });
    }
}

function updateVisuals() {
    if (!_state) return;
    const { linkGroup, nodeGroup, simulation, linksData, nodesData, defs, dotnetRef } = _state;

    // Links
    const link = linkGroup.selectAll('line').data(linksData, d => `${d.source.id ?? d.source}-${d.target.id ?? d.target}-${d.label}`);
    link.exit().remove();
    link.enter().append('line')
        .attr('stroke', '#3a3a6a')
        .attr('stroke-width', d => 1 + (d.weight || 0.5))
        .attr('stroke-opacity', 0.6)
        .attr('marker-end', 'url(#arrowhead)');

    // Edge labels
    const edgeLabel = linkGroup.selectAll('text').data(linksData, d => `${d.source.id ?? d.source}-${d.target.id ?? d.target}-${d.label}`);
    edgeLabel.exit().remove();
    edgeLabel.enter().append('text')
        .text(d => formatLabel(d.label))
        .attr('font-size', '11px')
        .attr('fill', '#8a8ab0')
        .attr('text-anchor', 'middle')
        .attr('dy', -6)
        .style('pointer-events', 'none')
        .style('user-select', 'none');

    // Nodes
    const node = nodeGroup.selectAll('g.node').data(nodesData, d => d.id);
    node.exit().remove();

    let dragMoved = false;
    let dragStartX = 0, dragStartY = 0;
    const DRAG_THRESHOLD = 5;
    const isTree = _state?.isTreeLayout;

    const enter = node.enter().append('g')
        .attr('class', 'node')
        .attr('cursor', 'pointer');

    // Only enable drag for force layout (not tree)
    if (!isTree) {
        enter.call(d3.drag()
            .on('start', (event, d) => {
                dragMoved = false;
                dragStartX = event.x;
                dragStartY = event.y;
                if (!event.active && simulation) simulation.alphaTarget(0.1).restart();
                d.fx = d.x; d.fy = d.y;
            })
            .on('drag', (event, d) => {
                const dx = event.x - dragStartX;
                const dy = event.y - dragStartY;
                if (Math.sqrt(dx * dx + dy * dy) > DRAG_THRESHOLD) {
                    dragMoved = true;
                    d3.select(event.currentTarget).attr('cursor', 'grabbing');
                }
                if (dragMoved) {
                    d.fx = event.x; d.fy = event.y;
                }
            })
            .on('end', (event, d) => {
                if (!event.active && simulation) simulation.alphaTarget(0);
                d.fx = null; d.fy = null;
                d3.select(event.currentTarget).attr('cursor', 'pointer');
            }));
    }

    // Single click: info panel
    enter.on('click', (event, d) => {
        if (dragMoved) return;
        event.stopPropagation();
        highlightNode(d.id);
        if (dotnetRef) dotnetRef.invokeMethodAsync('OnNodeClicked', d.id, d.name, d.type);
    });

    // Double click: expand in-place
    enter.on('dblclick', (event, d) => {
        event.stopPropagation();
        event.preventDefault();
        if (dotnetRef) {
            d.expanded = true;
            dotnetRef.invokeMethodAsync('OnNodeExpand', d.id, d.name);
        }
    });

    // Circle
    enter.append('circle')
        .attr('r', d => d.isRoot ? 32 : 26)
        .attr('fill', '#1e1e38')
        .attr('stroke', d => d.isRoot ? '#c8a832' : getTypeColor(d.type))
        .attr('stroke-width', d => d.isRoot ? 3 : 2);

    // Image or fallback
    enter.each(function (d) {
        const g = d3.select(this);
        const r = d.isRoot ? 26 : 20;
        const thumbUrl = WIKI_THUMB(d.imageUrl);
        if (thumbUrl) {
            const clipId = `clip-${d.id}`;
            defs.append('clipPath').attr('id', clipId)
                .append('circle').attr('r', r);
            g.append('image')
                .attr('xlink:href', thumbUrl)
                .attr('x', -r).attr('y', -r)
                .attr('width', r * 2).attr('height', r * 2)
                .attr('clip-path', `url(#${clipId})`)
                .attr('preserveAspectRatio', 'xMidYMid slice')
                .on('error', function () {
                    d3.select(this).remove();
                    g.append('text').attr('text-anchor', 'middle').attr('dy', '0.35em')
                        .attr('fill', '#555580').attr('font-size', '22px').text(getTypeIcon(d.type));
                });
        } else {
            g.append('text').attr('text-anchor', 'middle').attr('dy', '0.35em')
                .attr('fill', '#555580').attr('font-size', '20px').text(getTypeIcon(d.type));
        }
    });

    // Name label
    enter.append('text')
        .attr('dy', d => (d.isRoot ? 32 : 26) + 16)
        .attr('text-anchor', 'middle')
        .attr('fill', d => d.isRoot ? '#ffd866' : '#e0e0f0')
        .attr('font-size', '13px')
        .attr('font-weight', d => d.isRoot ? '700' : '500')
        .style('pointer-events', 'none').style('user-select', 'none')
        .text(d => truncate(d.name, 24));

    // Type badge
    enter.append('text')
        .attr('dy', d => (d.isRoot ? 32 : 26) + 29)
        .attr('text-anchor', 'middle')
        .attr('fill', d => getTypeColor(d.type))
        .attr('font-size', '10px')
        .style('pointer-events', 'none').style('user-select', 'none')
        .text(d => formatType(d.type));

    // Expand indicator (+) for unexpanded nodes
    enter.filter(d => !d.expanded && !d.isRoot)
        .append('text')
        .attr('class', 'expand-hint')
        .attr('x', 18).attr('y', -14)
        .attr('fill', '#ffd866').attr('font-size', '16px').attr('font-weight', '700')
        .style('pointer-events', 'none')
        .text('+');

    // Restart simulation (force layout only)
    if (simulation) {
        simulation.nodes(nodesData);
        simulation.force('link').links(linksData);
        simulation.alpha(0.5).restart();
    }
}

/**
 * Expand a node in-place: replace the current view with this node's relationships.
 * Hides nodes not directly connected to the expanded node for a clean view.
 */
export function expandNode(data) {
    if (!_state) return;
    const { nodeMap, edgeSet, nodesData, linksData, nodeGroup } = _state;

    // Add new data
    for (const n of data.nodes) {
        if (!nodeMap.has(n.id)) {
            const node = { ...n, isRoot: false, expanded: false };
            nodeMap.set(n.id, node);
            nodesData.push(node);
        }
    }
    for (const e of data.edges) {
        const key = `${e.fromId}-${e.toId}-${e.label}`;
        if (!edgeSet.has(key) && nodeMap.has(e.fromId) && nodeMap.has(e.toId)) {
            edgeSet.add(key);
            linksData.push({
                source: e.fromId, target: e.toId,
                label: e.label || '', weight: e.weight || 0.5,
            });
        }
    }

    // Mark expanded
    const expandedNode = nodeMap.get(data.rootId);
    if (expandedNode) {
        expandedNode.expanded = true;
        expandedNode.isRoot = true;
        nodeGroup.selectAll('g.node')
            .filter(d => d.id === data.rootId)
            .selectAll('.expand-hint').remove();
    }

    // Focus: determine which nodes are relevant to the expanded node
    const focusId = data.rootId;
    const visibleIds = new Set([focusId]);

    // Include all nodes directly connected to the focus node
    for (const link of linksData) {
        const srcId = link.source.id ?? link.source;
        const tgtId = link.target.id ?? link.target;
        if (srcId === focusId) visibleIds.add(tgtId);
        if (tgtId === focusId) visibleIds.add(srcId);
    }

    // Unmark old root
    for (const n of nodesData) {
        if (n.id !== focusId) n.isRoot = false;
    }

    // Hide/show nodes and links
    _state.focusSet = visibleIds;
    applyFocus();
    updateVisuals();

    // Center on the focus node after a short delay
    setTimeout(() => {
        const focusNode = nodeMap.get(focusId);
        if (focusNode && focusNode.x != null) {
            const { svg, zoomBehavior, width, height } = _state;
            svg.transition().duration(500)
                .call(zoomBehavior.transform, d3.zoomIdentity
                    .translate(width / 2, height / 2)
                    .scale(1.2)
                    .translate(-(focusNode.x || 0), -(focusNode.y || 0)));
        }
    }, 300);
}

/**
 * Show all nodes (remove focus filter).
 */
export function showAll() {
    if (!_state) return;
    _state.focusSet = null;
    applyFocus();
}

function applyFocus() {
    if (!_state) return;
    const { nodeGroup, linkGroup, focusSet } = _state;

    if (!focusSet) {
        // Show everything
        nodeGroup.selectAll('g.node').style('display', null).style('opacity', 1);
        linkGroup.selectAll('line').style('display', null).style('opacity', 0.6);
        linkGroup.selectAll('text').style('display', null);
        return;
    }

    nodeGroup.selectAll('g.node')
        .style('display', d => focusSet.has(d.id) ? null : 'none')
        .style('opacity', d => focusSet.has(d.id) ? 1 : 0);

    linkGroup.selectAll('line')
        .style('display', d => {
            const srcId = d.source.id ?? d.source;
            const tgtId = d.target.id ?? d.target;
            return focusSet.has(srcId) && focusSet.has(tgtId) ? null : 'none';
        });

    linkGroup.selectAll('text')
        .style('display', d => {
            const srcId = d.source.id ?? d.source;
            const tgtId = d.target.id ?? d.target;
            return focusSet.has(srcId) && focusSet.has(tgtId) ? null : 'none';
        });
}

function highlightNode(id) {
    if (!_state) return;
    _state.nodeGroup.selectAll('g.node circle')
        .attr('stroke', d => d.id === id ? '#ffffff' : (d.isRoot ? '#c8a832' : getTypeColor(d.type)))
        .attr('stroke-width', d => d.id === id ? 5 : (d.isRoot ? 3 : 2));
}

/**
 * Filter graph visibility by year. Edges with temporal bounds outside the year are hidden.
 * Nodes only connected by hidden edges are also hidden.
 * Pass null to show all.
 */
export function filterByYear(year) {
    if (!_state) return;
    const { nodeGroup, linkGroup, linksData, nodesData, nodeMap, isTreeLayout } = _state;

    if (year === null || year === undefined) {
        // Show all
        nodeGroup.selectAll('g.node').style('opacity', 1).style('display', null);
        linkGroup.selectAll('line').style('opacity', 0.6).style('display', null);
        linkGroup.selectAll('text').style('display', null);
        linkGroup.selectAll('path.tree-edge').style('opacity', 0.6).style('display', null);
        linkGroup.selectAll('text.tree-label').style('display', null);
        return;
    }

    // Determine which edges are active at this year
    const activeEdges = new Set();
    const activeNodeIds = new Set();

    // Always show root
    if (_state.currentRootId) activeNodeIds.add(_state.currentRootId);

    for (const link of linksData) {
        const fromYear = link.fromYear ?? null;
        const toYear = link.toYear ?? null;

        // Edge is active if: no temporal data (always show) OR year is within [fromYear, toYear]
        const active = (fromYear === null) ||
            (year >= fromYear && (toYear === null || year <= toYear));

        if (active) {
            const srcId = link.source.id ?? link.source;
            const tgtId = link.target.id ?? link.target;
            activeEdges.add(`${srcId}-${tgtId}-${link.label}`);
            activeNodeIds.add(srcId);
            activeNodeIds.add(tgtId);
        }
    }

    // Apply visibility
    nodeGroup.selectAll('g.node')
        .style('opacity', d => activeNodeIds.has(d.id) ? 1 : 0.1)
        .style('display', null);

    if (isTreeLayout) {
        linkGroup.selectAll('path.tree-edge')
            .style('opacity', d => {
                const src = typeof d.source === 'object' ? d.source : nodeMap.get(d.source);
                const tgt = typeof d.target === 'object' ? d.target : nodeMap.get(d.target);
                if (!src || !tgt) return 0.1;
                const key = `${src.id ?? d.source}-${tgt.id ?? d.target}-${d.label}`;
                return activeEdges.has(key) ? 0.6 : 0.05;
            });
        linkGroup.selectAll('text.tree-label')
            .style('opacity', d => {
                const src = typeof d.source === 'object' ? d.source : nodeMap.get(d.source);
                const tgt = typeof d.target === 'object' ? d.target : nodeMap.get(d.target);
                if (!src || !tgt) return 0;
                const key = `${src.id ?? d.source}-${tgt.id ?? d.target}-${d.label}`;
                return activeEdges.has(key) ? 1 : 0;
            });
    } else {
        linkGroup.selectAll('line')
            .style('opacity', d => {
                const srcId = d.source.id ?? d.source;
                const tgtId = d.target.id ?? d.target;
                return activeEdges.has(`${srcId}-${tgtId}-${d.label}`) ? 0.6 : 0.05;
            });
        linkGroup.selectAll('text')
            .style('opacity', d => {
                const srcId = d.source.id ?? d.source;
                const tgtId = d.target.id ?? d.target;
                return activeEdges.has(`${srcId}-${tgtId}-${d.label}`) ? 1 : 0;
            });
    }
}

/**
 * Highlight nodes whose name matches the search query (case-insensitive).
 * Matching nodes pulse with a bright ring; non-matching nodes dim.
 * Pass empty string to clear. Returns the count of matching nodes.
 */
export function highlightNodes(query) {
    if (!_state) return 0;
    const { nodeGroup } = _state;

    if (!query) {
        // Clear: restore all nodes to normal
        nodeGroup.selectAll('g.node').style('opacity', 1);
        nodeGroup.selectAll('g.node circle')
            .attr('stroke', d => d.isRoot ? '#c8a832' : getTypeColor(d.type))
            .attr('stroke-width', d => d.isRoot ? 3 : 2);
        return 0;
    }

    const q = query.toLowerCase();
    let matchCount = 0;

    nodeGroup.selectAll('g.node').each(function(d) {
        const matches = d.name && d.name.toLowerCase().includes(q);
        if (matches) matchCount++;
        d3.select(this).style('opacity', matches ? 1 : 0.15);
        d3.select(this).select('circle')
            .attr('stroke', matches ? '#ffd700' : (d.isRoot ? '#c8a832' : getTypeColor(d.type)))
            .attr('stroke-width', matches ? 5 : (d.isRoot ? 3 : 2));
    });

    return matchCount;
}

export function fitToScreen() {
    if (!_state) return;
    const { svg, zoomBehavior, nodesData, width, height } = _state;
    if (nodesData.length === 0) return;
    const xs = nodesData.map(d => d.x || 0);
    const ys = nodesData.map(d => d.y || 0);
    const pad = 80;
    const x0 = Math.min(...xs) - pad, x1 = Math.max(...xs) + pad;
    const y0 = Math.min(...ys) - pad, y1 = Math.max(...ys) + pad;
    const scale = Math.min(width / (x1 - x0), height / (y1 - y0), 2);
    const cx = (x0 + x1) / 2, cy = (y0 + y1) / 2;
    svg.transition().duration(500)
        .call(zoomBehavior.transform, d3.zoomIdentity
            .translate(width / 2, height / 2).scale(scale).translate(-cx, -cy));
}

export function zoomIn() {
    if (!_state) return;
    _state.svg.transition().duration(300).call(_state.zoomBehavior.scaleBy, 1.3);
}

export function zoomOut() {
    if (!_state) return;
    _state.svg.transition().duration(300).call(_state.zoomBehavior.scaleBy, 0.7);
}

export function getStats() {
    if (!_state) return { nodes: 0, edges: 0 };
    return { nodes: _state.nodesData.length, edges: _state.linksData.length };
}

let _keyHandler = null;

export function registerKeyboardShortcuts(dotnetRef) {
    if (_keyHandler) document.removeEventListener('keydown', _keyHandler);
    _keyHandler = (e) => {
        // Don't capture when typing in inputs
        if (e.target.tagName === 'INPUT' || e.target.tagName === 'TEXTAREA' || e.target.isContentEditable) return;

        switch (e.key) {
            case 'Backspace':
                e.preventDefault();
                dotnetRef.invokeMethodAsync('OnKeyAction', 'back');
                break;
            case 'f': case 'F':
                if (!e.ctrlKey && !e.metaKey) {
                    dotnetRef.invokeMethodAsync('OnKeyAction', 'fullscreen');
                }
                break;
            case '+': case '=':
                zoomIn();
                break;
            case '-':
                zoomOut();
                break;
            case '0':
                fitToScreen();
                break;
            case 'Escape':
                dotnetRef.invokeMethodAsync('OnKeyAction', 'escape');
                break;
        }
    };
    document.addEventListener('keydown', _keyHandler);
}

export function unregisterKeyboardShortcuts() {
    if (_keyHandler) {
        document.removeEventListener('keydown', _keyHandler);
        _keyHandler = null;
    }
}

export function destroyGraph(containerId) {
    unregisterKeyboardShortcuts();
    const container = document.getElementById(containerId);
    if (container) {
        while (container.firstChild) container.removeChild(container.firstChild);
    }
    _state = null;
}

function formatLabel(label) {
    if (!label) return '';
    return label.replace(/_/g, ' ');
}

function formatType(type) {
    if (!type) return '';
    return type.replace(/_/g, ' ').replace(/([a-z])([A-Z])/g, '$1 $2');
}

function truncate(str, max) {
    if (!str) return '';
    return str.length > max ? str.substring(0, max - 1) + '\u2026' : str;
}

function getTypeIcon(type) {
    const icons = {
        Character: '\u{1F464}', Organization: '\u{1F3E2}',
        CelestialBody: '\u{1F30D}', Planet: '\u{1F30D}',
        Species: '\u{1F9EC}', Starship: '\u{1F680}', StarshipClass: '\u{1F680}',
        Vehicle: '\u{1F680}', Battle: '\u2694\uFE0F', War: '\u2694\uFE0F',
        Weapon: '\u{1F5E1}', Droid: '\u{1F916}', Government: '\u{1F3DB}',
        City: '\u{1F3D9}', Company: '\u{1F3ED}', Fleet: '\u26F5',
    };
    return icons[type] || '\u25CF';
}
