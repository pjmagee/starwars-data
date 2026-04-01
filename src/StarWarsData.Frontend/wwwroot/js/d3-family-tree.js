// D3.js hierarchical family tree renderer
// Computes generation from parent/child/partner relationships
// Partners placed side-by-side, parent→child with elbow connectors

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

const ROW_HEIGHT = 200;
const NODE_RADIUS = 28;
const NODE_SPACING = 180;
const PARTNER_GAP = 80;

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

    // Build node lookup
    const nodeMap = new Map();
    for (const n of data.nodes) {
        nodeMap.set(n.id, {
            ...n,
            isRoot: n.id === data.rootId,
            _gen: null, _x: null, _y: null,
        });
    }

    // ── Compute generations from parent/child/partner relationships ──
    // Root = generation 0. Parents = -1, grandparents = -2, children = +1, etc.
    // Partners share the same generation as their partner.

    const root = nodeMap.get(data.rootId);
    if (root) root._gen = 0;

    // BFS from root using parent/child/partner edges
    const queue = root ? [root] : [];
    const visited = new Set(root ? [root.id] : []);

    while (queue.length > 0) {
        const node = queue.shift();
        const gen = node._gen;

        // Parents are one generation up
        for (const pid of (node.parents || [])) {
            const p = nodeMap.get(pid);
            if (p && !visited.has(pid)) {
                p._gen = gen - 1;
                visited.add(pid);
                queue.push(p);
            }
        }

        // Children are one generation down
        for (const cid of (node.children || [])) {
            const c = nodeMap.get(cid);
            if (c && !visited.has(cid)) {
                c._gen = gen + 1;
                visited.add(cid);
                queue.push(c);
            }
        }

        // Partners share the same generation
        for (const pid of (node.partners || [])) {
            const p = nodeMap.get(pid);
            if (p && !visited.has(pid)) {
                p._gen = gen;
                visited.add(pid);
                queue.push(p);
            }
        }

        // Siblings share the same generation
        for (const sid of (node.siblings || [])) {
            const s = nodeMap.get(sid);
            if (s && !visited.has(sid)) {
                s._gen = gen;
                visited.add(sid);
                queue.push(s);
            }
        }
    }

    // Assign gen=0 to any unvisited nodes
    for (const node of nodeMap.values()) {
        if (node._gen === null) node._gen = 0;
    }

    // ── Group by generation and identify partner pairs ──
    const genMap = new Map();
    for (const node of nodeMap.values()) {
        if (!genMap.has(node._gen)) genMap.set(node._gen, []);
        genMap.get(node._gen).push(node);
    }

    const sortedGens = [...genMap.keys()].sort((a, b) => a - b);
    const centerX = width / 2;
    const centerY = height / 2;

    // Layout each generation row
    for (const gen of sortedGens) {
        const nodes = genMap.get(gen);
        const placed = new Set();
        const units = []; // each unit is a partner group [node] or [nodeA, nodeB]

        // First pass: group partners
        for (const node of nodes) {
            if (placed.has(node.id)) continue;
            placed.add(node.id);

            const group = [node];
            for (const pid of (node.partners || [])) {
                const partner = nodeMap.get(pid);
                if (partner && !placed.has(pid) && partner._gen === gen) {
                    group.push(partner);
                    placed.add(pid);
                }
            }
            units.push(group);
        }

        // Calculate total width needed
        let totalWidth = 0;
        for (const unit of units) {
            totalWidth += unit.length === 1 ? NODE_SPACING : (unit.length * NODE_SPACING + PARTNER_GAP);
        }
        totalWidth += (units.length - 1) * NODE_SPACING * 0.5;

        // Position units
        let x = centerX - totalWidth / 2;
        const y = centerY + gen * ROW_HEIGHT;

        for (const unit of units) {
            if (unit.length === 1) {
                unit[0]._x = x + NODE_SPACING / 2;
                unit[0]._y = y;
                x += NODE_SPACING;
            } else {
                // Partner group: place side-by-side
                const groupWidth = (unit.length - 1) * PARTNER_GAP + NODE_SPACING;
                const startX = x;
                for (let i = 0; i < unit.length; i++) {
                    unit[i]._x = startX + NODE_SPACING / 2 + i * PARTNER_GAP;
                    unit[i]._y = y;
                }

                // Draw partner connector (dashed line)
                for (let i = 0; i < unit.length - 1; i++) {
                    const a = unit[i], b = unit[i + 1];
                    edgeGroup.append('line')
                        .attr('x1', a._x + NODE_RADIUS + 4)
                        .attr('y1', a._y)
                        .attr('x2', b._x - NODE_RADIUS - 4)
                        .attr('y2', b._y)
                        .attr('stroke', '#ff6b9d')
                        .attr('stroke-width', 2)
                        .attr('stroke-dasharray', '6,4');

                    // Heart/ring symbol at midpoint
                    const mx = (a._x + b._x) / 2;
                    edgeGroup.append('text')
                        .attr('x', mx).attr('y', a._y + 4)
                        .attr('text-anchor', 'middle')
                        .attr('fill', '#ff6b9d')
                        .attr('font-size', '12px')
                        .style('pointer-events', 'none')
                        .text('\u2764');
                }

                x += groupWidth + NODE_SPACING * 0.5;
            }
            x += NODE_SPACING * 0.3;
        }
    }

    // ── Draw parent→child edges (elbow connectors) ──
    // Find all parent→child relationships from node data
    const drawnEdges = new Set();

    for (const node of nodeMap.values()) {
        if (node._x == null) continue;
        for (const cid of (node.children || [])) {
            const child = nodeMap.get(cid);
            if (!child || child._x == null) continue;
            const key = `${node.id}-${cid}`;
            if (drawnEdges.has(key)) continue;
            drawnEdges.add(key);

            // Find partner group midpoint for the "from" position
            // (if node has a partner, draw from the midpoint between them)
            let fromX = node._x;
            const partnerInSameGen = (node.partners || [])
                .map(pid => nodeMap.get(pid))
                .find(p => p && p._gen === node._gen && p._x != null);
            if (partnerInSameGen) {
                fromX = (node._x + partnerInSameGen._x) / 2;
            }

            const fromY = node._y + NODE_RADIUS + 6;
            const toY = child._y - NODE_RADIUS - 6;
            const midY = fromY + (toY - fromY) * 0.4;

            edgeGroup.append('path')
                .attr('d', `M ${fromX} ${fromY} L ${fromX} ${midY} L ${child._x} ${midY} L ${child._x} ${toY}`)
                .attr('fill', 'none')
                .attr('stroke', '#4a4a7a')
                .attr('stroke-width', 1.5)
                .attr('stroke-opacity', 0.7);
        }
    }

    // ── Draw sibling brackets (thin dotted line at the top) ──
    for (const node of nodeMap.values()) {
        if (node._x == null) continue;
        for (const sid of (node.siblings || [])) {
            const sib = nodeMap.get(sid);
            if (!sib || sib._x == null || sib.id <= node.id) continue; // avoid duplication
            const key = `sib-${Math.min(node.id, sib.id)}-${Math.max(node.id, sib.id)}`;
            if (drawnEdges.has(key)) continue;
            drawnEdges.add(key);

            edgeGroup.append('line')
                .attr('x1', node._x).attr('y1', node._y - NODE_RADIUS - 8)
                .attr('x2', sib._x).attr('y2', sib._y - NODE_RADIUS - 8)
                .attr('stroke', '#5a9a5a')
                .attr('stroke-width', 1)
                .attr('stroke-dasharray', '3,3')
                .attr('stroke-opacity', 0.5);
        }
    }

    // ── Draw nodes ──
    for (const node of nodeMap.values()) {
        if (node._x == null) continue;

        const r = node.isRoot ? 34 : NODE_RADIUS;
        const g = nodeGroup.append('g')
            .attr('transform', `translate(${node._x}, ${node._y})`)
            .attr('cursor', 'pointer')
            .on('click', (event) => {
                event.stopPropagation();
                highlight(node.id);
                if (dotnetRef) dotnetRef.invokeMethodAsync('OnNodeClicked', node.id, node.name, node.type || 'Character');
            });

        // Store ref for highlighting
        node._gEl = g.node();

        // Circle
        g.append('circle')
            .attr('r', r)
            .attr('fill', '#1e1e38')
            .attr('stroke', node.isRoot ? '#c8a832' : getTypeColor(node.type))
            .attr('stroke-width', node.isRoot ? 3 : 2);

        // Image
        const thumbUrl = WIKI_THUMB(node.imageUrl);
        const imgR = node.isRoot ? 28 : 22;
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

        // Name
        g.append('text')
            .attr('dy', r + 16)
            .attr('text-anchor', 'middle')
            .attr('fill', node.isRoot ? '#ffd866' : '#e0e0f0')
            .attr('font-size', '13px')
            .attr('font-weight', node.isRoot ? '700' : '500')
            .style('pointer-events', 'none')
            .text(truncate(node.name, 22));

        // Born/Died — truncated
        const born = shortDate(node.born);
        const died = shortDate(node.died);
        const lifespan = [born, died].filter(Boolean).join(' \u2013 ');
        if (lifespan) {
            g.append('text')
                .attr('dy', r + 30)
                .attr('text-anchor', 'middle')
                .attr('fill', '#6a6a9a')
                .attr('font-size', '10px')
                .style('pointer-events', 'none')
                .text(truncate(lifespan, 30));
        }
    }

    _state = { container, svg, zoomGroup, zoomBehavior, nodeGroup, edgeGroup, nodeMap, dotnetRef, width, height, defs };

    setTimeout(() => fitToScreen(), 150);
}

function highlight(id) {
    if (!_state) return;
    _state.nodeMap.forEach((node) => {
        if (!node._gEl) return;
        const circle = d3.select(node._gEl).select('circle');
        if (node.id === id) {
            circle.attr('stroke', '#ffffff').attr('stroke-width', 4);
        } else {
            circle.attr('stroke', node.isRoot ? '#c8a832' : getTypeColor(node.type))
                  .attr('stroke-width', node.isRoot ? 3 : 2);
        }
    });
}

// Extract just the year portion from a date string like "41 BBY, Tatooine (disputed)"
function shortDate(str) {
    if (!str) return '';
    // Match patterns like "41 BBY", "19 BBY", "4 ABY", "Before 66 BBY"
    const match = str.match(/(?:(?:Before|By|After|circa|c\.)?\s*\d+(?:\.\d+)?\s*(?:BBY|ABY))/i);
    return match ? match[0].trim() : truncate(str, 15);
}

export function fitToScreen() {
    if (!_state) return;
    const { svg, zoomBehavior, nodeMap, width, height } = _state;
    const xs = [], ys = [];
    nodeMap.forEach(n => { if (n._x != null) { xs.push(n._x); ys.push(n._y); } });
    if (xs.length === 0) return;
    const pad = 80;
    const x0 = Math.min(...xs) - pad, x1 = Math.max(...xs) + pad;
    const y0 = Math.min(...ys) - pad, y1 = Math.max(...ys) + pad;
    const scale = Math.min(width / (x1 - x0), height / (y1 - y0), 1.5);
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

export function expandNode() { /* tree layout doesn't support in-place expansion */ }
export function registerKeyboardShortcuts() { }
export function unregisterKeyboardShortcuts() { }

export function destroyGraph(containerId) {
    const container = document.getElementById(containerId);
    if (container) while (container.firstChild) container.removeChild(container.firstChild);
    _state = null;
}

function truncate(str, max) {
    if (!str) return '';
    return str.length > max ? str.substring(0, max - 1) + '\u2026' : str;
}
