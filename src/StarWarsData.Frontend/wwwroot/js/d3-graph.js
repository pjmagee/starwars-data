// D3.js force-directed relationship graph renderer
// Supports in-place node expansion, navigation history, fullscreen

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
        .attr('refX', 32).attr('refY', 0)
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

    const simulation = d3.forceSimulation(nodesData)
        .force('link', d3.forceLink(linksData).id(d => d.id).distance(160).strength(0.3))
        .force('charge', d3.forceManyBody().strength(-350).distanceMax(600))
        .force('center', d3.forceCenter(width / 2, height / 2).strength(0.05))
        .force('collision', d3.forceCollide().radius(55))
        .force('x', d3.forceX(width / 2).strength(0.03))
        .force('y', d3.forceY(height / 2).strength(0.03));

    simulation.on('tick', tick);

    _state = {
        container, svg, defs, zoomGroup, zoomBehavior, linkGroup, nodeGroup,
        simulation, nodeMap, edgeSet, nodesData, linksData, dotnetRef,
        width, height, currentRootId: data.rootId,
    };

    updateVisuals();

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
        .attr('font-size', '9px')
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
    const DRAG_THRESHOLD = 5; // pixels — must move this far before it counts as a drag

    const enter = node.enter().append('g')
        .attr('class', 'node')
        .attr('cursor', 'pointer')
        .call(d3.drag()
            .on('start', (event, d) => {
                dragMoved = false;
                dragStartX = event.x;
                dragStartY = event.y;
                if (!event.active) simulation.alphaTarget(0.1).restart();
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
                if (!event.active) simulation.alphaTarget(0);
                if (!dragMoved) {
                    // Didn't actually drag — keep node where it was
                    d.fx = null; d.fy = null;
                } else {
                    d.fx = null; d.fy = null;
                }
                d3.select(event.currentTarget).attr('cursor', 'pointer');
            }));

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
        .attr('r', d => d.isRoot ? 28 : 22)
        .attr('fill', '#1e1e38')
        .attr('stroke', d => d.isRoot ? '#c8a832' : getTypeColor(d.type))
        .attr('stroke-width', d => d.isRoot ? 3 : 2);

    // Image or fallback
    enter.each(function (d) {
        const g = d3.select(this);
        const r = d.isRoot ? 22 : 16;
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
                        .attr('fill', '#555580').attr('font-size', '18px').text(getTypeIcon(d.type));
                });
        } else {
            g.append('text').attr('text-anchor', 'middle').attr('dy', '0.35em')
                .attr('fill', '#555580').attr('font-size', '16px').text(getTypeIcon(d.type));
        }
    });

    // Name label
    enter.append('text')
        .attr('dy', d => (d.isRoot ? 28 : 22) + 14)
        .attr('text-anchor', 'middle')
        .attr('fill', d => d.isRoot ? '#ffd866' : '#e0e0f0')
        .attr('font-size', '11px')
        .attr('font-weight', d => d.isRoot ? '700' : '500')
        .style('pointer-events', 'none').style('user-select', 'none')
        .text(d => truncate(d.name, 22));

    // Type badge
    enter.append('text')
        .attr('dy', d => (d.isRoot ? 28 : 22) + 25)
        .attr('text-anchor', 'middle')
        .attr('fill', d => getTypeColor(d.type))
        .attr('font-size', '8px')
        .style('pointer-events', 'none').style('user-select', 'none')
        .text(d => d.type || '');

    // Expand indicator (+) for unexpanded nodes
    enter.filter(d => !d.expanded && !d.isRoot)
        .append('text')
        .attr('class', 'expand-hint')
        .attr('x', 16).attr('y', -12)
        .attr('fill', '#ffd866').attr('font-size', '14px').attr('font-weight', '700')
        .style('pointer-events', 'none')
        .text('+');

    // Restart simulation
    simulation.nodes(nodesData);
    simulation.force('link').links(linksData);
    simulation.alpha(0.5).restart();
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
        .attr('stroke-width', d => d.id === id ? 4 : (d.isRoot ? 3 : 2));
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

export function destroyGraph(containerId) {
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
