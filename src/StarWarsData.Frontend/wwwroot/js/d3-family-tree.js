// D3.js hierarchical family tree renderer
// Uses node.generation for vertical layout (negative=ancestors, 0=root, positive=descendants)
// Partners placed side-by-side, parent→child connected with elbow connectors

const TYPE_COLORS = {
    Character: '#7e6fff', Organization: '#ff6f91', CelestialBody: '#4ecdc4',
    Planet: '#4ecdc4', System: '#3dc1d3', Species: '#ffd93d', Starship: '#6bcb77',
    StarshipClass: '#6bcb77', Battle: '#ff6b6b', War: '#ff4757', Droid: '#a29bfe',
    Government: '#e056fd', Military_unit: '#7bed9f', Fleet: '#70a1ff',
};

function getTypeColor(type) { return TYPE_COLORS[type] || '#7a7a9e'; }

const WIKI_THUMB = (url) => {
    if (!url) return null;
    return url.split('/revision')[0] + '/revision/latest/scale-to-width-down/50';
};

const ROW_HEIGHT = 220;
const NODE_RADIUS = 28;
const NODE_SPACING = 140;
const PARTNER_GAP = 60;

let _state = null;

export function renderFamilyTree(containerId, data, dotnetRef) {
    const container = document.getElementById(containerId);
    if (!container) return;
    while (container.firstChild) container.removeChild(container.firstChild);

    const width = container.clientWidth || 900;
    const height = container.clientHeight || 650;

    const svg = d3.select(container)
        .append('svg')
        .attr('width', '100%')
        .attr('height', '100%')
        .attr('viewBox', [0, 0, width, height]);

    const defs = svg.append('defs');

    const zoomGroup = svg.append('g');
    const zoomBehavior = d3.zoom()
        .scaleExtent([0.1, 4])
        .on('zoom', (event) => zoomGroup.attr('transform', event.transform));
    svg.call(zoomBehavior);

    const edgeGroup = zoomGroup.append('g').attr('class', 'edges');
    const nodeGroup = zoomGroup.append('g').attr('class', 'nodes');

    // Build node map
    const nodeMap = new Map();
    for (const n of data.nodes) {
        nodeMap.set(n.id, { ...n, isRoot: n.id === data.rootId });
    }

    // Group by generation
    const genMap = new Map();
    for (const node of nodeMap.values()) {
        const gen = node.generation ?? 0;
        if (!genMap.has(gen)) genMap.set(gen, []);
        genMap.get(gen).push(node);
    }

    // Sort generations
    const sortedGens = [...genMap.keys()].sort((a, b) => a - b);

    // Layout: assign x,y to each node
    // Within each generation, group partners together
    const centerX = width / 2;
    const centerY = height / 2;

    for (const gen of sortedGens) {
        const nodes = genMap.get(gen);
        const placed = new Set();
        const groups = []; // array of arrays (partner groups)

        for (const node of nodes) {
            if (placed.has(node.id)) continue;
            placed.add(node.id);

            // Find partners in the same generation
            const group = [node];
            for (const pid of (node.partners || [])) {
                const partner = nodeMap.get(pid);
                if (partner && !placed.has(pid) && (partner.generation ?? 0) === gen) {
                    group.push(partner);
                    placed.add(pid);
                }
            }
            groups.push(group);
        }

        // Position groups centered horizontally
        const totalWidth = groups.reduce((sum, g) =>
            sum + g.length * NODE_SPACING + (g.length > 1 ? (g.length - 1) * PARTNER_GAP : 0), 0)
            + (groups.length - 1) * NODE_SPACING;

        let x = centerX - totalWidth / 2;
        const y = centerY + gen * ROW_HEIGHT;

        for (const group of groups) {
            const groupWidth = group.length * NODE_SPACING;
            const groupStartX = x + (groupWidth - NODE_SPACING) / 2 - (group.length - 1) * NODE_SPACING / 2;

            for (let i = 0; i < group.length; i++) {
                group[i]._x = groupStartX + i * (NODE_SPACING + PARTNER_GAP / 2);
                group[i]._y = y;
            }

            // Draw partner connectors
            if (group.length > 1) {
                for (let i = 0; i < group.length - 1; i++) {
                    const a = group[i], b = group[i + 1];
                    edgeGroup.append('line')
                        .attr('x1', a._x + NODE_RADIUS + 4)
                        .attr('y1', a._y)
                        .attr('x2', b._x - NODE_RADIUS - 4)
                        .attr('y2', b._y)
                        .attr('stroke', '#ff6b9d')
                        .attr('stroke-width', 2)
                        .attr('stroke-dasharray', '6,4');
                }
            }

            x += groupWidth + NODE_SPACING;
        }
    }

    // Draw parent-child edges (elbow connectors)
    for (const edge of data.edges) {
        const from = nodeMap.get(edge.fromId);
        const to = nodeMap.get(edge.toId);
        if (!from || !to || from._x == null || to._x == null) continue;

        const fromGen = from.generation ?? 0;
        const toGen = to.generation ?? 0;

        // Only draw downward edges (parent→child)
        if (toGen <= fromGen && fromGen <= toGen) continue;

        const midY = (from._y + to._y) / 2;

        edgeGroup.append('path')
            .attr('d', `M ${from._x} ${from._y + NODE_RADIUS + 2}
                         L ${from._x} ${midY}
                         L ${to._x} ${midY}
                         L ${to._x} ${to._y - NODE_RADIUS - 2}`)
            .attr('fill', 'none')
            .attr('stroke', '#4a4a7a')
            .attr('stroke-width', 1.5)
            .attr('stroke-opacity', 0.6);

        // Edge label
        edgeGroup.append('text')
            .attr('x', (from._x + to._x) / 2)
            .attr('y', midY - 6)
            .attr('text-anchor', 'middle')
            .attr('fill', '#6a6a9a')
            .attr('font-size', '10px')
            .style('pointer-events', 'none')
            .text(formatLabel(edge.label));
    }

    // Draw nodes
    for (const node of nodeMap.values()) {
        if (node._x == null) continue;

        const g = nodeGroup.append('g')
            .attr('transform', `translate(${node._x}, ${node._y})`)
            .attr('cursor', 'pointer')
            .on('click', (event) => {
                event.stopPropagation();
                highlightNode(node.id);
                if (dotnetRef) dotnetRef.invokeMethodAsync('OnNodeClicked', node.id, node.name, node.type || 'Character');
            });

        // Circle
        const r = node.isRoot ? 32 : NODE_RADIUS;
        g.append('circle')
            .attr('r', r)
            .attr('fill', '#1e1e38')
            .attr('stroke', node.isRoot ? '#c8a832' : getTypeColor(node.type))
            .attr('stroke-width', node.isRoot ? 3 : 2);

        // Image or fallback
        const thumbUrl = WIKI_THUMB(node.imageUrl);
        const imgR = node.isRoot ? 26 : 22;
        if (thumbUrl) {
            const clipId = `ftclip-${node.id}`;
            defs.append('clipPath').attr('id', clipId)
                .append('circle').attr('r', imgR);
            g.append('image')
                .attr('xlink:href', thumbUrl)
                .attr('x', -imgR).attr('y', -imgR)
                .attr('width', imgR * 2).attr('height', imgR * 2)
                .attr('clip-path', `url(#${clipId})`)
                .attr('preserveAspectRatio', 'xMidYMid slice')
                .on('error', function () {
                    d3.select(this).remove();
                    g.append('text').attr('text-anchor', 'middle').attr('dy', '0.35em')
                        .attr('fill', '#555580').attr('font-size', '20px').text('\u{1F464}');
                });
        } else {
            g.append('text').attr('text-anchor', 'middle').attr('dy', '0.35em')
                .attr('fill', '#555580').attr('font-size', '20px').text('\u{1F464}');
        }

        // Name label
        g.append('text')
            .attr('dy', r + 16)
            .attr('text-anchor', 'middle')
            .attr('fill', node.isRoot ? '#ffd866' : '#e0e0f0')
            .attr('font-size', '13px')
            .attr('font-weight', node.isRoot ? '700' : '500')
            .style('pointer-events', 'none')
            .text(truncate(node.name, 20));

        // Born/Died subtitle
        const lifespan = [node.born, node.died].filter(Boolean).join(' – ');
        if (lifespan) {
            g.append('text')
                .attr('dy', r + 30)
                .attr('text-anchor', 'middle')
                .attr('fill', '#6a6a9a')
                .attr('font-size', '10px')
                .style('pointer-events', 'none')
                .text(lifespan);
        }
    }

    _state = { container, svg, zoomGroup, zoomBehavior, nodeGroup, edgeGroup, nodeMap, dotnetRef, width, height, defs };

    // Auto-fit after initial render
    setTimeout(() => fitToScreen(), 100);
}

function highlightNode(id) {
    if (!_state) return;
    _state.nodeGroup.selectAll('g circle')
        .attr('stroke', function () {
            const node = d3.select(this.parentNode).datum();
            // No datum in this approach, use data attribute approach instead
            return '#ffffff';
        });
    // Simpler approach: reset all then highlight
    _state.nodeGroup.selectAll('g').each(function () {
        const circle = d3.select(this).select('circle');
        circle.attr('stroke-width', 2);
    });
    // Find and highlight the clicked node
    _state.nodeMap.forEach((node) => {
        if (node.id === id && node._gEl) {
            d3.select(node._gEl).select('circle').attr('stroke', '#ffffff').attr('stroke-width', 5);
        }
    });
}

export function fitToScreen() {
    if (!_state) return;
    const { svg, zoomBehavior, nodeMap, width, height } = _state;
    const xs = [], ys = [];
    nodeMap.forEach(n => { if (n._x != null) { xs.push(n._x); ys.push(n._y); } });
    if (xs.length === 0) return;
    const pad = 100;
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
    return { nodes: _state.nodeMap.size, edges: 0 };
}

export function expandNode() { /* no-op for tree layout */ }
export function registerKeyboardShortcuts() { }
export function unregisterKeyboardShortcuts() { }

export function destroyGraph(containerId) {
    const container = document.getElementById(containerId);
    if (container) while (container.firstChild) container.removeChild(container.firstChild);
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
