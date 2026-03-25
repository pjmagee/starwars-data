// D3.js force-directed relationship graph renderer
// Called from Blazor via JS interop

const TYPE_COLORS = {
    Character: '#7e6fff',
    Organization: '#ff6f91',
    CelestialBody: '#4ecdc4',
    Planet: '#4ecdc4',
    Species: '#ffd93d',
    Starship: '#6bcb77',
    Vehicle: '#6bcb77',
    Battle: '#ff6b6b',
    War: '#ff4757',
    Weapon: '#ff9f43',
    Droid: '#a29bfe',
    Event: '#fd79a8',
    Food: '#fdcb6e',
    ForcePower: '#a29bfe',
};

function getTypeColor(type) {
    return TYPE_COLORS[type] || '#7a7a9e';
}

const WIKI_THUMB = (url) => {
    if (!url) return null;
    const base = url.split('/revision')[0];
    return base + '/revision/latest/scale-to-width-down/50';
};

/**
 * Render a force-directed graph into the given container element.
 * @param {string} containerId - DOM id of the container div
 * @param {object} data - { nodes: [...], edges: [...], rootId: number }
 *   nodes: [{ id, name, type, imageUrl, born, died }]
 *   edges: [{ fromId, toId, label, weight? }]
 * @param {object} [dotnetRef] - optional DotNetObjectReference for click callbacks
 */
export function renderForceGraph(containerId, data, dotnetRef) {
    const container = document.getElementById(containerId);
    if (!container) return;

    // Clear previous
    container.innerHTML = '';

    const width = container.clientWidth || 900;
    const height = container.clientHeight || 650;

    const svg = d3.select(container)
        .append('svg')
        .attr('width', width)
        .attr('height', height)
        .attr('viewBox', [0, 0, width, height]);

    // Defs for arrow markers and image clip paths
    const defs = svg.append('defs');

    // Arrow marker
    defs.append('marker')
        .attr('id', 'arrowhead')
        .attr('viewBox', '0 -5 10 10')
        .attr('refX', 32)
        .attr('refY', 0)
        .attr('markerWidth', 8)
        .attr('markerHeight', 8)
        .attr('orient', 'auto')
        .append('path')
        .attr('d', 'M0,-5L10,0L0,5')
        .attr('fill', '#5a5a8a');

    // Build node/link data
    const nodeMap = new Map();
    for (const n of data.nodes) {
        nodeMap.set(n.id, {
            ...n,
            isRoot: n.id === data.rootId,
        });
    }

    const nodes = Array.from(nodeMap.values());

    const links = data.edges
        .filter(e => nodeMap.has(e.fromId) && nodeMap.has(e.toId))
        .map(e => ({
            source: e.fromId,
            target: e.toId,
            label: e.label || '',
            weight: e.weight || 0.5,
        }));

    // Force simulation
    const simulation = d3.forceSimulation(nodes)
        .force('link', d3.forceLink(links)
            .id(d => d.id)
            .distance(180)
            .strength(0.4))
        .force('charge', d3.forceManyBody()
            .strength(-400)
            .distanceMax(500))
        .force('center', d3.forceCenter(width / 2, height / 2))
        .force('collision', d3.forceCollide().radius(60))
        .force('x', d3.forceX(width / 2).strength(0.05))
        .force('y', d3.forceY(height / 2).strength(0.05));

    // Zoom behavior
    const zoomGroup = svg.append('g');

    svg.call(d3.zoom()
        .scaleExtent([0.2, 4])
        .on('zoom', (event) => {
            zoomGroup.attr('transform', event.transform);
        }));

    // Links (edges)
    const linkGroup = zoomGroup.append('g').attr('class', 'links');

    const link = linkGroup.selectAll('line')
        .data(links)
        .join('line')
        .attr('stroke', '#3a3a6a')
        .attr('stroke-width', d => 1 + d.weight)
        .attr('stroke-opacity', 0.6)
        .attr('marker-end', 'url(#arrowhead)');

    // Edge labels
    const edgeLabels = linkGroup.selectAll('text')
        .data(links)
        .join('text')
        .text(d => formatLabel(d.label))
        .attr('font-size', '10px')
        .attr('fill', '#8a8ab0')
        .attr('text-anchor', 'middle')
        .attr('dy', -6)
        .style('pointer-events', 'none')
        .style('user-select', 'none');

    // Nodes
    const nodeGroup = zoomGroup.append('g').attr('class', 'nodes');

    const node = nodeGroup.selectAll('g')
        .data(nodes)
        .join('g')
        .attr('cursor', 'grab')
        .call(d3.drag()
            .on('start', dragstarted)
            .on('drag', dragged)
            .on('end', dragended));

    // Click handler: invoke Blazor callback when a node is clicked (not dragged)
    let dragMoved = false;
    node.on('click', (event, d) => {
        if (dragMoved) return; // Ignore drag-end clicks
        event.stopPropagation();

        // Highlight selected node
        node.select('circle')
            .attr('stroke', n => n.id === d.id ? '#ffffff' : (n.isRoot ? '#c8a832' : getTypeColor(n.type)))
            .attr('stroke-width', n => n.id === d.id ? 4 : (n.isRoot ? 3 : 2));

        if (dotnetRef) {
            dotnetRef.invokeMethodAsync('OnNodeClicked', d.id, d.name, d.type);
        }
    });

    // Node circle (outer ring with type color)
    node.append('circle')
        .attr('r', d => d.isRoot ? 28 : 24)
        .attr('fill', '#1e1e38')
        .attr('stroke', d => d.isRoot ? '#c8a832' : getTypeColor(d.type))
        .attr('stroke-width', d => d.isRoot ? 3 : 2);

    // Node image (clipped circle) or fallback icon
    node.each(function (d) {
        const g = d3.select(this);
        const r = d.isRoot ? 22 : 18;
        const thumbUrl = WIKI_THUMB(d.imageUrl);

        if (thumbUrl) {
            // Clip path for this node
            const clipId = `clip-${d.id}`;
            defs.append('clipPath')
                .attr('id', clipId)
                .append('circle')
                .attr('r', r);

            g.append('image')
                .attr('xlink:href', thumbUrl)
                .attr('x', -r)
                .attr('y', -r)
                .attr('width', r * 2)
                .attr('height', r * 2)
                .attr('clip-path', `url(#${clipId})`)
                .attr('preserveAspectRatio', 'xMidYMid slice')
                .on('error', function () {
                    // Fallback on image load error
                    d3.select(this).remove();
                    g.append('text')
                        .attr('text-anchor', 'middle')
                        .attr('dy', '0.35em')
                        .attr('fill', '#555580')
                        .attr('font-size', '20px')
                        .text('\u{1F464}');
                });
        } else {
            // Fallback icon
            g.append('text')
                .attr('text-anchor', 'middle')
                .attr('dy', '0.35em')
                .attr('fill', '#555580')
                .attr('font-size', '18px')
                .text(getTypeIcon(d.type));
        }
    });

    // Node name label
    node.append('text')
        .attr('dy', d => (d.isRoot ? 28 : 24) + 14)
        .attr('text-anchor', 'middle')
        .attr('fill', d => d.isRoot ? '#ffd866' : '#e0e0f0')
        .attr('font-size', '11px')
        .attr('font-weight', d => d.isRoot ? '700' : '500')
        .style('pointer-events', 'none')
        .style('user-select', 'none')
        .text(d => truncate(d.name, 22));

    // Type badge (small text below name)
    node.append('text')
        .attr('dy', d => (d.isRoot ? 28 : 24) + 26)
        .attr('text-anchor', 'middle')
        .attr('fill', d => getTypeColor(d.type))
        .attr('font-size', '9px')
        .style('pointer-events', 'none')
        .style('user-select', 'none')
        .text(d => d.type || '');

    // Tooltip on hover
    node.append('title')
        .text(d => {
            let t = d.name;
            if (d.type) t += ` (${d.type})`;
            if (d.born) t += `\nBorn: ${d.born}`;
            if (d.died) t += `\nDied: ${d.died}`;
            return t;
        });

    // Tick update
    simulation.on('tick', () => {
        link
            .attr('x1', d => d.source.x)
            .attr('y1', d => d.source.y)
            .attr('x2', d => d.target.x)
            .attr('y2', d => d.target.y);

        edgeLabels
            .attr('x', d => (d.source.x + d.target.x) / 2)
            .attr('y', d => (d.source.y + d.target.y) / 2);

        node.attr('transform', d => `translate(${d.x},${d.y})`);
    });

    // Drag handlers
    function dragstarted(event, d) {
        dragMoved = false;
        if (!event.active) simulation.alphaTarget(0.3).restart();
        d.fx = d.x;
        d.fy = d.y;
        d3.select(this).attr('cursor', 'grabbing');
    }

    function dragged(event, d) {
        dragMoved = true;
        d.fx = event.x;
        d.fy = event.y;
    }

    function dragended(event, d) {
        if (!event.active) simulation.alphaTarget(0);
        d.fx = null;
        d.fy = null;
        d3.select(this).attr('cursor', 'grab');
    }
}

export function destroyGraph(containerId) {
    const container = document.getElementById(containerId);
    if (container) container.innerHTML = '';
}

function formatLabel(label) {
    if (!label) return '';
    return label.replace(/_/g, ' ').replace(/\b\w/g, c => c.toUpperCase());
}

function truncate(str, max) {
    if (!str) return '';
    return str.length > max ? str.substring(0, max - 1) + '\u2026' : str;
}

function getTypeIcon(type) {
    const icons = {
        Character: '\u{1F464}',
        Organization: '\u{1F3E2}',
        CelestialBody: '\u{1F30D}',
        Planet: '\u{1F30D}',
        Species: '\u{1F9EC}',
        Starship: '\u{1F680}',
        Vehicle: '\u{1F680}',
        Battle: '\u2694\uFE0F',
        War: '\u2694\uFE0F',
        Weapon: '\u{1F5E1}',
        Droid: '\u{1F916}',
    };
    return icons[type] || '\u25CF';
}
