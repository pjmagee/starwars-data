// Unified Galaxy Map - standalone D3.js visualization
// Based on Galactic Map V2 with added temporal layers (territory control + event heatmap)
// Level 1: Galaxy overview (regions, trade routes, clickable grid cells)
// Level 1b: Region focus (zoom to region bounding box, show systems)
// Level 2: Grid cell (systems within that cell)
// Level 3: System (orbiting celestial bodies)
// + Territory control fills (faction-colored cells per year)
// + Event heatmap circles (event density per grid cell per year)

const CATEGORY_ICONS = {
    'Battle': '\u2694', 'War': '\uD83D\uDEE1', 'Campaign': '\uD83C\uDFAF',
    'Government': '\uD83C\uDFDB', 'Treaty': '\uD83E\uDD1D', 'Event': '\u2B50',
};

function getLensColor(lens) {
    let hash = 0;
    for (const c of lens) hash = (hash * 31 + c.charCodeAt(0)) | 0;
    const hue = ((hash % 360) + 360) % 360;
    return `hsl(${hue}, 75%, 55%)`;
}

const REGION_PALETTE = [
    '#4ecdc4', '#ff6f91', '#7e6fff', '#ffd93d', '#6bcb77',
    '#ff6b6b', '#a29bfe', '#fd79a8', '#fdcb6e', '#00cec9',
    '#e17055', '#0984e3', '#6c5ce7', '#00b894', '#e84393',
    '#fab1a0', '#74b9ff', '#55efc4', '#ff7675', '#dfe6e9',
];
const regionColorMap = {};
function getRegionColor(name, regions) {
    if (!regionColorMap[name]) {
        const idx = regions.findIndex(r => r.name === name);
        regionColorMap[name] = REGION_PALETTE[(idx >= 0 ? idx : Object.keys(regionColorMap).length) % REGION_PALETTE.length];
    }
    return regionColorMap[name];
}

const BG_URL = '/galaxy.png';

let _state = null;

export function initialize(containerId, overview, dotNetRef) {
    const container = document.getElementById(containerId);
    if (!container) return;
    container.querySelectorAll(':scope > svg, :scope > .galaxy-tooltip').forEach(el => el.remove());

    const width = container.clientWidth || 1200;
    const height = container.clientHeight || 800;
    const cols = overview.gridColumns;
    const rows = overview.gridRows;
    const startCol = overview.gridStartCol ?? 0;
    const startRow = overview.gridStartRow ?? 0;

    // World coordinates — cell size fixed at 100px, world scales to fit grid
    const cellSize = 100;
    const worldW = cols * cellSize;
    const worldH = rows * cellSize;
    const cellW = cellSize;
    const cellH = cellSize;

    // Convert absolute grid coordinates to local pixel position
    const colX = (col) => (col - startCol) * cellW;
    const rowY = (row) => (row - startRow) * cellH;

    const svg = d3.select(container)
        .append('svg')
        .attr('width', width)
        .attr('height', height)
        .style('background', '#0a0a1a');

    const defs = svg.append('defs');
    const glow = defs.append('filter').attr('id', 'glow');
    glow.append('feGaussianBlur').attr('stdDeviation', '2').attr('result', 'coloredBlur');
    const fm = glow.append('feMerge');
    fm.append('feMergeNode').attr('in', 'coloredBlur');
    fm.append('feMergeNode').attr('in', 'SourceGraphic');

    // Main group for zoom transforms
    const g = svg.append('g');

    // Layers (order = z-order, bottom to top)
    const bgLayer = g.append('g').attr('class', 'layer-bg');
    const regionLayer = g.append('g');
    const gridLayer = g.append('g');
    const territoryLayer = g.append('g').attr('class', 'layer-territory').style('display', 'none');
    const cellLayer = g.append('g');     // transparent grid click targets (below routes/nebulas)
    const routeLayer = g.append('g').attr('class', 'layer-routes');
    const nebulaLayer = g.append('g').attr('class', 'layer-nebulae');
    const contentLayer = g.append('g');  // drill-down content (systems / celestial bodies)
    // Heatmap + markers on top so event circles receive mouse events
    const heatmapLayer = g.append('g').attr('class', 'layer-heatmap').style('display', 'none');
    const markerLayer = g.append('g').attr('class', 'layer-markers').style('display', 'none');

    // Tooltip
    const tooltip = d3.select(container).append('div')
        .attr('class', 'galaxy-tooltip')
        .style('position', 'absolute')
        .style('visibility', 'hidden')
        .style('background', 'rgba(15,15,35,0.95)')
        .style('border', '1px solid rgba(255,255,255,0.2)')
        .style('border-radius', '6px')
        .style('padding', '8px 12px')
        .style('color', '#e0e0f0')
        .style('font-size', '12px')
        .style('pointer-events', 'none')
        .style('z-index', '1000')
        .style('max-width', '280px');

    // Background
    bgLayer.append('image')
        .attr('href', BG_URL)
        .attr('x', 0).attr('y', 0)
        .attr('width', worldW).attr('height', worldH)
        .attr('preserveAspectRatio', 'xMidYMid slice')
        .attr('opacity', 0.5);

    // Grid lines
    for (let c = 0; c <= cols; c++) {
        gridLayer.append('line')
            .attr('x1', c * cellW).attr('y1', 0)
            .attr('x2', c * cellW).attr('y2', worldH)
            .attr('stroke', 'rgba(255,255,255,0.07)')
            .attr('stroke-width', 0.5);
    }
    for (let r = 0; r <= rows; r++) {
        gridLayer.append('line')
            .attr('x1', 0).attr('y1', r * cellH)
            .attr('x2', worldW).attr('y2', r * cellH)
            .attr('stroke', 'rgba(255,255,255,0.07)')
            .attr('stroke-width', 0.5);
    }
    for (let c = 0; c < cols; c++) {
        gridLayer.append('text')
            .attr('x', c * cellW + cellW / 2).attr('y', -8)
            .attr('text-anchor', 'middle').attr('fill', 'rgba(255,255,255,0.35)')
            .attr('font-size', '13px').attr('font-weight', '600').text(String.fromCharCode(65 + startCol + c));
    }
    for (let r = 0; r < rows; r++) {
        gridLayer.append('text')
            .attr('x', -14).attr('y', r * cellH + cellH / 2 + 4)
            .attr('text-anchor', 'middle').attr('fill', 'rgba(255,255,255,0.35)')
            .attr('font-size', '13px').attr('font-weight', '600').text(r + startRow + 1);
    }

    // === REGIONS (cell-based, no overlap) ===
    // Each region is rendered as individual cell rectangles rather than a convex hull,
    // since galactic regions are concentric and hulls would overlap.
    overview.regions.forEach((region) => {
        if (region.cells.length === 0) return;
        const color = getRegionColor(region.name, overview.regions);

        const rg = regionLayer.append('g')
            .attr('class', 'region-group')
            .attr('data-region', region.name)
            .style('cursor', 'pointer');

        // Draw each cell as a colored rectangle
        for (const [col, row] of region.cells) {
            rg.append('rect')
                .attr('class', 'region-cell')
                .attr('x', colX(col)).attr('y', rowY(row))
                .attr('width', cellW).attr('height', cellH)
                .attr('fill', color).attr('fill-opacity', 0.07)
                .attr('stroke', color).attr('stroke-opacity', 0.12).attr('stroke-width', 0.5);
        }

        // Hidden label (kept for CSS selection but not displayed)
        const avgX = region.cells.reduce((s, [c]) => s + c * cellW + cellW / 2, 0) / region.cells.length;
        const avgY = region.cells.reduce((s, [, r]) => s + r * cellH + cellH / 2, 0) / region.cells.length;
        rg.append('text')
            .attr('x', avgX).attr('y', avgY)
            .attr('text-anchor', 'middle').attr('fill', color)
            .attr('fill-opacity', 0).attr('font-size', '0')
            .attr('class', 'region-label')
            .style('pointer-events', 'none')
            .text(region.name);

        // Hover: pulse all cells in the region
        rg.on('mouseover', function () {
            if (currentLevel !== 'overview') return;
            const cells = d3.select(this).selectAll('.region-cell');
            const label = d3.select(this).select('.region-label');
            (function pulse() {
                cells.transition('pulse').duration(1000)
                    .attr('fill-opacity', 0.18).attr('stroke-opacity', 0.4)
                    .transition('pulse').duration(1000)
                    .attr('fill-opacity', 0.07).attr('stroke-opacity', 0.12)
                    .on('end', pulse);
            })();
            label.transition().duration(200).attr('fill-opacity', 0.9);
            dotNetRef.invokeMethodAsync('OnRegionHovered', region.name, region.cells.length, color);
        })
        .on('mouseout', function () {
            if (currentLevel !== 'overview') return;
            const cells = d3.select(this).selectAll('.region-cell');
            const label = d3.select(this).select('.region-label');
            cells.interrupt('pulse').transition().duration(300)
                .attr('fill-opacity', 0.07).attr('stroke-opacity', 0.12);
            label.transition().duration(300).attr('fill-opacity', 0.5);
            dotNetRef.invokeMethodAsync('OnRegionUnhovered');
        })
        .on('click', function (event) {
            if (currentLevel !== 'overview') return;
            event.stopPropagation();
            drillIntoRegion(region.name);
        });
    });

    // Trade routes — with wide invisible hit area for easy hovering/clicking
    const routeLine = d3.line()
        .x(d => colX(d.col) + cellW / 2)
        .y(d => rowY(d.row) + cellH / 2)
        .curve(d3.curveCatmullRom.alpha(0.5));
    overview.tradeRoutes.forEach(route => {
        if (route.waypoints.length < 2) return;
        const rg = routeLayer.append('g')
            .attr('class', 'trade-route-group')
            .style('cursor', 'pointer')
            .datum(route);

        // Wide invisible hit area
        rg.append('path')
            .datum(route.waypoints).attr('d', routeLine)
            .attr('fill', 'none').attr('stroke', 'transparent')
            .attr('stroke-width', 12);

        // Visible route line
        rg.append('path')
            .datum(route.waypoints).attr('d', routeLine)
            .attr('fill', 'none').attr('stroke', 'rgba(255,215,0,0.2)')
            .attr('stroke-width', 1.2).attr('stroke-dasharray', '5,3')
            .attr('class', 'trade-route')
            .style('pointer-events', 'none');

        rg.on('mouseover', function () {
            d3.select(this).select('.trade-route')
                .attr('stroke', 'rgba(255,215,0,0.6)').attr('stroke-width', 2.5);
            tooltip.html(
                `<strong style="color:#ffd700;">${route.name}</strong>` +
                `<br><span style="color:#aaa">Trade Route</span>` +
                `<br><span style="color:#aaa">Waypoints:</span> ${route.waypoints.length}` +
                `<br><span style="color:#888">Click for details</span>`
            ).style('visibility', 'visible');
        })
        .on('mousemove', (event) => {
            tooltip.style('top', (event.offsetY - 10) + 'px').style('left', (event.offsetX + 15) + 'px');
        })
        .on('mouseout', function () {
            d3.select(this).select('.trade-route')
                .attr('stroke', 'rgba(255,215,0,0.2)').attr('stroke-width', 1.2);
            tooltip.style('visibility', 'hidden');
        })
        .on('click', (event) => {
            event.stopPropagation();
            dotNetRef.invokeMethodAsync('OnCelestialBodySelected', route.id, route.name);
        });
    });

    // Nebulas — render across all cells they span, with hover/click
    overview.nebulas.forEach(n => {
        const cells = n.cells && n.cells.length > 0
            ? n.cells
            : [[n.col, n.row]];

        const ng = nebulaLayer.append('g')
            .attr('class', 'nebula-group')
            .style('cursor', 'pointer')
            .datum(n);

        if (cells.length === 1) {
            // Single-cell nebula: ellipse
            const cx = colX(cells[0][0]) + cellW / 2;
            const cy = rowY(cells[0][1]) + cellH / 2;
            ng.append('ellipse')
                .attr('cx', cx).attr('cy', cy)
                .attr('rx', cellW * 0.3).attr('ry', cellH * 0.2)
                .attr('fill', 'rgba(138,43,226,0.12)')
                .attr('stroke', 'rgba(138,43,226,0.25)').attr('stroke-width', 0.5);
        } else {
            // Multi-cell nebula: draw across all cells with a connecting shape
            // Compute bounding box of all cells
            let minC = Infinity, maxC = -Infinity, minR = Infinity, maxR = -Infinity;
            for (const [c, r] of cells) {
                minC = Math.min(minC, c); maxC = Math.max(maxC, c);
                minR = Math.min(minR, r); maxR = Math.max(maxR, r);
            }
            const x1 = colX(minC), y1 = rowY(minR);
            const w = (maxC - minC + 1) * cellW;
            const h = (maxR - minR + 1) * cellH;
            const cx = x1 + w / 2, cy = y1 + h / 2;

            ng.append('ellipse')
                .attr('cx', cx).attr('cy', cy)
                .attr('rx', w / 2 * 0.85).attr('ry', h / 2 * 0.7)
                .attr('fill', 'rgba(138,43,226,0.10)')
                .attr('stroke', 'rgba(138,43,226,0.25)').attr('stroke-width', 0.5)
                .attr('stroke-dasharray', '3,2');
        }

        // Label at centroid
        const avgX = cells.reduce((s, [c]) => s + c * cellW + cellW / 2, 0) / cells.length;
        const avgY = cells.reduce((s, [, r]) => s + r * cellH + cellH / 2, 0) / cells.length;
        ng.append('text')
            .attr('x', avgX).attr('y', avgY + cellH * 0.15)
            .attr('text-anchor', 'middle')
            .attr('fill', 'rgba(138,43,226,0.35)')
            .attr('font-size', '7px').attr('font-style', 'italic')
            .style('pointer-events', 'none')
            .text(n.name.replace(/\/Legends$/i, ''));

        // Hover interaction
        ng.on('mouseover', function (event) {
            d3.select(this).select('ellipse')
                .transition().duration(200)
                .attr('fill', 'rgba(138,43,226,0.25)')
                .attr('stroke', 'rgba(138,43,226,0.6)').attr('stroke-width', 1.5);
            d3.select(this).select('text')
                .transition().duration(200).attr('fill', 'rgba(138,43,226,0.8)');

            let html = `<strong style="color:#9b59b6;">${n.name.replace(/\/Legends$/i, '')}</strong>`;
            html += `<br><span style="color:#aaa">Type:</span> Nebula`;
            html += `<br><span style="color:#aaa">Grid:</span> ${cells.map(([c,r]) => String.fromCharCode(65+c)+'-'+(r+1)).join(', ')}`;
            if (n.region) html += `<br><span style="color:#aaa">Region:</span> ${n.region}`;
            html += `<br><span style="color:#888">Click for details</span>`;
            tooltip.html(html).style('visibility', 'visible');
        })
        .on('mousemove', (event) => {
            tooltip.style('top', (event.offsetY - 10) + 'px').style('left', (event.offsetX + 15) + 'px');
        })
        .on('mouseout', function () {
            d3.select(this).select('ellipse')
                .transition().duration(200)
                .attr('fill', cells.length > 1 ? 'rgba(138,43,226,0.10)' : 'rgba(138,43,226,0.12)')
                .attr('stroke', 'rgba(138,43,226,0.25)').attr('stroke-width', 0.5);
            d3.select(this).select('text')
                .transition().duration(200).attr('fill', 'rgba(138,43,226,0.35)');
            tooltip.style('visibility', 'hidden');
        })
        .on('click', (event) => {
            event.stopPropagation();
            dotNetRef.invokeMethodAsync('OnCelestialBodySelected', n.id, n.name);
        });
    });

    // === CLICKABLE GRID CELLS (full cell area, not just indicator dots) ===
    // Build a lookup of cells with data for tooltip info
    const cellDataMap = {};
    overview.cells.forEach(c => { cellDataMap[`${c.col},${c.row}`] = c; });

    // Render a transparent clickable rect for EVERY grid cell
    for (let c = 0; c < cols; c++) {
        for (let r = 0; r < rows; r++) {
            cellLayer.append('rect')
                .attr('x', colX(c)).attr('y', rowY(r))
                .attr('width', cellW).attr('height', cellH)
                .attr('fill', 'transparent')
                .attr('class', 'grid-click-target')
                .style('cursor', 'pointer')
                .datum({ col: c, row: r })
                .on('mouseover', function (event, d) {
                    const info = cellDataMap[`${d.col},${d.row}`];
                    const count = info ? info.systemCount : 0;
                    const region = info ? info.region : null;
                    const color = region ? getRegionColor(region, overview.regions) : '#ffffff';
                    tooltip.html(
                        `<strong>Grid ${String.fromCharCode(65 + d.col)}-${d.row + 1}</strong>` +
                        `<br>${count} system${count !== 1 ? 's' : ''}` +
                        (region ? `<br><span style="color:${color}">${region}</span>` : '') +
                        `<br><span style="color:#888">Click to explore</span>`
                    ).style('visibility', 'visible');

                    // Highlight the cell border
                    d3.select(this).attr('stroke', 'rgba(255,255,255,0.25)').attr('stroke-width', 1);
                })
                .on('mousemove', (event) => {
                    tooltip.style('top', (event.offsetY - 10) + 'px').style('left', (event.offsetX + 15) + 'px');
                })
                .on('mouseout', function () {
                    tooltip.style('visibility', 'hidden');
                    d3.select(this).attr('stroke', 'none');
                })
                .on('click', (event, d) => {
                    event.stopPropagation();
                    drillIntoCell(d.col, d.row);
                });
        }
    }

    // === CELL DENSITY INDICATORS (visual dots on top of click targets) ===
    const indicatorLayer = g.append('g');
    const maxCount = Math.max(1, ...overview.cells.map(c => c.systemCount));

    overview.cells.forEach(cell => {
        const cx = colX(cell.col) + cellW / 2;
        const cy = rowY(cell.row) + cellH / 2;
        const color = cell.region ? getRegionColor(cell.region, overview.regions) : '#ffffff';
        const sizeScale = 3 + (cell.systemCount / maxCount) * 12;

        const cg = indicatorLayer.append('g')
            .attr('transform', `translate(${cx},${cy})`)
            .style('pointer-events', 'none')
            .datum(cell);

        // Glow circle sized by density
        cg.append('circle')
            .attr('r', sizeScale)
            .attr('fill', color).attr('fill-opacity', 0.3)
            .attr('stroke', color).attr('stroke-opacity', 0.5)
            .attr('stroke-width', 0.8)
            .attr('filter', 'url(#glow)');

        // Count label
        if (cell.systemCount > 5) {
            cg.append('text')
                .attr('text-anchor', 'middle').attr('dy', '0.35em')
                .attr('fill', '#fff').attr('fill-opacity', 0.6)
                .attr('font-size', '7px')
                .text(cell.systemCount);
        }
    });

    // === ZOOM (manual scroll/pan disabled, only animated transitions) ===
    const zoom = d3.zoom()
        .scaleExtent([0.4, 50])
        .on('zoom', (event) => g.attr('transform', event.transform));

    // Allow free scroll/pan at overview level for navigation
    svg.call(zoom);

    // Fit to overview
    const initScale = Math.min(width / worldW, height / worldH) * 0.92;
    const initTx = (width - worldW * initScale) / 2;
    const initTy = (height - worldH * initScale) / 2;
    const overviewTransform = d3.zoomIdentity.translate(initTx, initTy).scale(initScale);
    svg.call(zoom.transform, overviewTransform);

    // Navigation state
    let currentLevel = 'overview'; // 'overview' | 'region' | 'cell' | 'system'
    let currentCell = null;
    let currentRegion = null;
    let currentSystemData = null;
    let onlyWithBodies = false;
    let lastCellSystems = null;
    let lastRegionSystems = null;
    let hiddenRegions = new Set();

    // === DRILL-DOWN: REGION ===
    async function drillIntoRegion(regionName) {
        const region = overview.regions.find(r => r.name === regionName);
        if (!region || region.cells.length === 0) return;

        currentLevel = 'region';
        currentRegion = region;
        currentCell = null;
        dotNetRef.invokeMethodAsync('OnLevelChanged', 'region', regionName);

        // Stop any pulse animations
        regionLayer.selectAll('.region-cell').interrupt('pulse');

        // Calculate bounding box of region cells
        let minCol = Infinity, maxCol = -Infinity, minRow = Infinity, maxRow = -Infinity;
        for (const [col, row] of region.cells) {
            minCol = Math.min(minCol, col);
            maxCol = Math.max(maxCol, col);
            minRow = Math.min(minRow, row);
            maxRow = Math.max(maxRow, row);
        }

        // Zoom to fit the region
        const x1 = colX(minCol), y1 = rowY(minRow);
        const rw = (maxCol - minCol + 1) * cellW;
        const rh = (maxRow - minRow + 1) * cellH;
        const targetScale = Math.min(width / rw, height / rh) * 0.8;
        const rcx = x1 + rw / 2, rcy = y1 + rh / 2;
        svg.transition().duration(800).ease(d3.easeCubicInOut)
            .call(zoom.transform, d3.zoomIdentity.translate(
                width / 2 - rcx * targetScale,
                height / 2 - rcy * targetScale
            ).scale(targetScale));

        // Dim non-active regions, highlight active
        regionLayer.selectAll('.region-group').each(function () {
            const el = d3.select(this);
            if (el.attr('data-region') === regionName) {
                el.selectAll('.region-cell').transition().duration(400)
                    .attr('fill-opacity', 0.15).attr('stroke-opacity', 0.4).attr('stroke-width', 1);
                el.select('.region-label').transition().duration(400).attr('fill-opacity', 0.7);
            } else {
                el.transition().duration(400).style('opacity', 0.05);
            }
        });

        // Dim cell indicators outside this region, subtly show in-region ones
        const regionCellSet = new Set(region.cells.map(([c, r]) => `${c},${r}`));
        indicatorLayer.selectAll('g').each(function () {
            const el = d3.select(this);
            const d = el.datum();
            if (d && regionCellSet.has(`${d.col},${d.row}`)) {
                el.transition().duration(400).style('opacity', 0.25);
            } else {
                el.transition().duration(400).style('opacity', 0.03);
            }
        });
        // Hide grid click targets during region drill-down
        cellLayer.transition().duration(400).style('opacity', 0).style('pointer-events', 'none');

        // Fetch and render systems within the region
        try {
            const result = await dotNetRef.invokeMethodAsync('FetchSystemsInRange', minCol, maxCol, minRow, maxRow);
            if (!result || !result.systems) return;
            // Filter to only systems in cells belonging to this region
            const regionSystems = result.systems.filter(s => regionCellSet.has(`${s.col},${s.row}`));
            lastRegionSystems = regionSystems;
            renderRegionSystems(regionSystems, region);
        } catch (e) {
            console.warn('Failed to fetch region systems:', e);
        }
    }

    function renderRegionSystems(systems, region) {
        contentLayer.selectAll('*').remove();

        // Ensure only systems within this region's cells are rendered
        const cellSet = new Set(region.cells.map(([c, r]) => `${c},${r}`));
        const inRegion = systems.filter(s => cellSet.has(`${s.col},${s.row}`));

        const bodyFilter = onlyWithBodies
            ? inRegion.filter(s => s.celestialBodies && s.celestialBodies.length > 0)
            : inRegion;

        if (bodyFilter.length === 0) {
            const pts = region.cells.map(([col, row]) => [colX(col) + cellW / 2, rowY(row) + cellH / 2]);
            const cx = pts.reduce((s, p) => s + p[0], 0) / pts.length;
            const cy = pts.reduce((s, p) => s + p[1], 0) / pts.length;
            contentLayer.append('text')
                .attr('x', cx).attr('y', cy)
                .attr('text-anchor', 'middle').attr('fill', 'rgba(255,255,255,0.4)')
                .attr('font-size', '14px')
                .text(onlyWithBodies ? 'No systems with celestial bodies in this region' : 'No systems in this region');
            return;
        }

        // Cap systems per cell — show systems with bodies first, then fill up to limit
        const maxPerCell = 8;
        const cellBuckets = {};
        bodyFilter.forEach(s => {
            const k = `${s.col},${s.row}`;
            if (!cellBuckets[k]) cellBuckets[k] = { shown: [], overflow: 0 };
            const bucket = cellBuckets[k];
            if (bucket.shown.length < maxPerCell) {
                bucket.shown.push(s);
            } else {
                bucket.overflow++;
            }
        });
        // Sort each bucket: systems with bodies first
        Object.values(cellBuckets).forEach(b => {
            b.shown.sort((a, c) => (c.celestialBodies?.length || 0) - (a.celestialBodies?.length || 0));
        });
        const filtered = Object.values(cellBuckets).flatMap(b => b.shown);

        // Spread systems within their cells via force layout
        const cellCounts = {};
        filtered.forEach(s => { const k = `${s.col},${s.row}`; cellCounts[k] = (cellCounts[k] || 0) + 1; });

        const nodes = filtered.map(sys => ({
            ...sys,
            x: colX(sys.col) + cellW / 2 + (Math.random() - 0.5) * cellW * 0.6,
            y: rowY(sys.row) + cellH / 2 + (Math.random() - 0.5) * cellH * 0.6,
        }));

        const collideR = cellW * 0.04;
        const sim = d3.forceSimulation(nodes)
            .force('x', d3.forceX(d => colX(d.col) + cellW / 2).strength(0.15))
            .force('y', d3.forceY(d => rowY(d.row) + cellH / 2).strength(0.15))
            .force('collide', d3.forceCollide(collideR).strength(0.9).iterations(4))
            .stop();
        for (let i = 0; i < 150; i++) sim.tick();

        // Clamp to cell boundaries
        const pad = cellW * 0.05;
        nodes.forEach(n => {
            const xMin = colX(n.col) + pad, xMax = colX(n.col) + cellW - pad;
            const yMin = rowY(n.row) + pad, yMax = rowY(n.row) + cellH - pad;
            n.x = Math.max(xMin, Math.min(xMax, n.x));
            n.y = Math.max(yMin, Math.min(yMax, n.y));
        });

        // Render clickable cell backgrounds within the region — click to drill into cell
        cellSet.forEach(k => {
            const [c, r] = k.split(',').map(Number);
            const total = cellBuckets[k] ? cellBuckets[k].shown.length + cellBuckets[k].overflow : 0;
            contentLayer.insert('rect', ':first-child')
                .attr('x', colX(c)).attr('y', rowY(r))
                .attr('width', cellW).attr('height', cellH)
                .attr('fill', 'transparent')
                .style('cursor', 'pointer')
                .on('click', (event) => { event.stopPropagation(); drillIntoCell(c, r); })
                .on('mouseover', function () {
                    d3.select(this).attr('fill', 'rgba(255,255,255,0.04)');
                    tooltip.html(`<strong>Grid ${String.fromCharCode(65 + c)}-${r + 1}</strong><br>${total} systems<br><span style="color:#888">Click to explore cell</span>`)
                        .style('visibility', 'visible');
                })
                .on('mousemove', (event) => {
                    tooltip.style('top', (event.offsetY - 10) + 'px').style('left', (event.offsetX + 15) + 'px');
                })
                .on('mouseout', function () {
                    d3.select(this).attr('fill', 'transparent');
                    tooltip.style('visibility', 'hidden');
                });

            // Show overflow badge if systems were capped
            if (cellBuckets[k] && cellBuckets[k].overflow > 0) {
                contentLayer.append('text')
                    .attr('x', (c + 1) * cellW - 6).attr('y', r * cellH + 11)
                    .attr('text-anchor', 'end')
                    .attr('fill', 'rgba(255,255,255,0.5)')
                    .attr('font-size', '9px')
                    .style('pointer-events', 'none')
                    .text(`+${cellBuckets[k].overflow}`);
            }
        });

        const sysG = contentLayer.selectAll('g.sys')
            .data(nodes).join('g')
            .attr('class', 'sys')
            .attr('transform', d => `translate(${d.x},${d.y})`)
            .style('cursor', 'pointer')
            .style('opacity', 0);

        sysG.transition().delay((d, i) => Math.min(i * 10, 500)).duration(400).style('opacity', 1);

        const sysColor = d => d.region ? getRegionColor(d.region, overview.regions) : '#ffffff';

        sysG.append('circle')
            .attr('r', cellW * 0.015)
            .attr('fill', sysColor).attr('fill-opacity', 0.7)
            .attr('stroke', sysColor).attr('stroke-opacity', 0.9).attr('stroke-width', 0.5)
            .attr('filter', 'url(#glow)');

        sysG.append('text')
            .attr('dy', cellW * -0.025).attr('text-anchor', 'middle')
            .attr('fill', '#e0e0f0').attr('font-size', `${cellW * 0.012}px`)
            .style('pointer-events', 'none')
            .text(d => d.name.replace(/ system$/i, '').replace(/\/Legends$/i, ''));

        sysG.each(function (d) {
            if (d.celestialBodies && d.celestialBodies.length > 0) {
                d3.select(this).append('text')
                    .attr('dy', cellW * 0.03).attr('text-anchor', 'middle')
                    .attr('fill', 'rgba(255,255,255,0.4)').attr('font-size', `${cellW * 0.009}px`)
                    .style('pointer-events', 'none')
                    .text(`${d.celestialBodies.length} bod${d.celestialBodies.length > 1 ? 'ies' : 'y'}`);
            }
        });

        // Interactions
        sysG.on('mouseover', function (event, d) {
            let html = `<strong>${d.name}</strong>`;
            if (d.region) html += `<br><span style="color:#aaa">Region:</span> ${d.region}`;
            if (d.sector) html += `<br><span style="color:#aaa">Sector:</span> ${d.sector}`;
            if (d.celestialBodies && d.celestialBodies.length > 0) {
                html += `<br><span style="color:#aaa">Bodies:</span> ${d.celestialBodies.length}`;
                html += `<br><span style="color:#888">Click to view system</span>`;
            }
            tooltip.html(html).style('visibility', 'visible');
            d3.select(this).select('circle').transition().duration(150)
                .attr('r', cellW * 0.025).attr('fill-opacity', 1);
        })
        .on('mousemove', (event) => {
            tooltip.style('top', (event.offsetY - 10) + 'px').style('left', (event.offsetX + 15) + 'px');
        })
        .on('mouseout', function () {
            tooltip.style('visibility', 'hidden');
            d3.select(this).select('circle').transition().duration(150)
                .attr('r', cellW * 0.015).attr('fill-opacity', 0.7);
        })
        .on('click', (event, d) => {
            event.stopPropagation();
            drillIntoSystem(d);
        });
    }

    // === DRILL-DOWN: CELL ===
    async function drillIntoCell(col, row) {
        if (!dotNetRef) return;
        currentLevel = 'cell';
        currentCell = { col, row };
        currentRegion = null;
        dotNetRef.invokeMethodAsync('OnLevelChanged', 'cell', `${String.fromCharCode(65 + col)}-${row + 1}`);

        // Animate zoom to cell
        const targetX = colX(col);
        const targetY = rowY(row);
        const targetScale = Math.min(width / cellW, height / cellH) * 0.85;
        const tx = width / 2 - (targetX + cellW / 2) * targetScale;
        const ty = height / 2 - (targetY + cellH / 2) * targetScale;
        svg.transition().duration(800).ease(d3.easeCubicInOut)
            .call(zoom.transform, d3.zoomIdentity.translate(tx, ty).scale(targetScale));

        // Fade out cell indicators and click targets
        indicatorLayer.transition().duration(400).style('opacity', 0.1);
        cellLayer.transition().duration(400).style('opacity', 0).style('pointer-events', 'none');
        regionLayer.selectAll('.region-label').transition().duration(400).attr('fill-opacity', 0);

        // Fetch systems for this cell
        try {
            const result = await dotNetRef.invokeMethodAsync('FetchSystemsInRange', col, col, row, row);
            if (!result || !result.systems) return;
            lastCellSystems = result.systems;
            renderCellSystems(result.systems, col, row);
        } catch (e) {
            console.warn('Failed to fetch systems:', e);
        }
    }

    function renderCellSystems(systems, col, row) {
        contentLayer.selectAll('*').remove();

        const filtered = onlyWithBodies
            ? systems.filter(s => s.celestialBodies && s.celestialBodies.length > 0)
            : systems;

        const cx = colX(col) + cellW / 2;
        const cy = rowY(row) + cellH / 2;

        if (filtered.length === 0) {
            contentLayer.append('text')
                .attr('x', cx).attr('y', cy)
                .attr('text-anchor', 'middle').attr('fill', 'rgba(255,255,255,0.4)')
                .attr('font-size', '14px')
                .text('No systems with celestial bodies in this cell');
            return;
        }

        // Grid layout: evenly distribute systems across the cell area
        const density = filtered.length;
        const gridCols = Math.ceil(Math.sqrt(density * (cellW / cellH)));
        const gridRows = Math.ceil(density / gridCols);
        const padX = cellW * 0.08, padY = cellH * 0.08;
        const slotW = (cellW - padX * 2) / gridCols;
        const slotH = (cellH - padY * 2) / gridRows;
        const startX = colX(col) + padX + slotW / 2;
        const startY = rowY(row) + padY + slotH / 2;

        const nodes = filtered.map((sys, i) => ({
            ...sys,
            x: startX + (i % gridCols) * slotW,
            y: startY + Math.floor(i / gridCols) * slotH,
        }));

        // Highlight current cell
        contentLayer.append('rect')
            .attr('x', colX(col) + 1).attr('y', rowY(row) + 1)
            .attr('width', cellW - 2).attr('height', cellH - 2)
            .attr('fill', 'rgba(255,255,255,0.03)')
            .attr('stroke', 'rgba(255,255,255,0.15)').attr('stroke-width', 0.5)
            .attr('rx', 2);

        const sysG = contentLayer.selectAll('g.sys')
            .data(nodes).join('g')
            .attr('class', 'sys')
            .attr('transform', d => `translate(${d.x},${d.y})`)
            .style('cursor', 'pointer')
            .style('opacity', 0);

        // Animate in
        sysG.transition().delay((d, i) => i * 20).duration(400).style('opacity', 1);

        const color = d => {
            if (!d.region) return '#ffffff';
            return getRegionColor(d.region, overview.regions);
        };

        sysG.append('circle')
            .attr('r', cellW * 0.02)
            .attr('fill', color).attr('fill-opacity', 0.7)
            .attr('stroke', color).attr('stroke-opacity', 0.9).attr('stroke-width', 0.5)
            .attr('filter', 'url(#glow)');

        sysG.append('text')
            .attr('dy', cellW * -0.03).attr('text-anchor', 'middle')
            .attr('fill', '#e0e0f0').attr('font-size', `${cellW * 0.015}px`)
            .style('pointer-events', 'none')
            .text(d => d.name.replace(/ system$/i, '').replace(/\/Legends$/i, ''));

        // Planet count badge
        sysG.each(function (d) {
            if (d.celestialBodies && d.celestialBodies.length > 0) {
                d3.select(this).append('text')
                    .attr('dy', cellW * 0.035).attr('text-anchor', 'middle')
                    .attr('fill', 'rgba(255,255,255,0.4)').attr('font-size', `${cellW * 0.01}px`)
                    .style('pointer-events', 'none')
                    .text(`${d.celestialBodies.length} bod${d.celestialBodies.length > 1 ? 'ies' : 'y'}`);
            }
        });

        // Interactions
        sysG.on('mouseover', function (event, d) {
            let html = `<strong>${d.name}</strong>`;
            if (d.region) html += `<br><span style="color:#aaa">Region:</span> ${d.region}`;
            if (d.sector) html += `<br><span style="color:#aaa">Sector:</span> ${d.sector}`;
            if (d.celestialBodies && d.celestialBodies.length > 0) {
                html += `<br><span style="color:#aaa">Bodies:</span> ${d.celestialBodies.length}`;
                html += `<br><span style="color:#888">Click to view system</span>`;
            }
            tooltip.html(html).style('visibility', 'visible');
            d3.select(this).select('circle').transition().duration(150)
                .attr('r', cellW * 0.035).attr('fill-opacity', 1);
        })
        .on('mousemove', (event) => {
            tooltip.style('top', (event.offsetY - 10) + 'px').style('left', (event.offsetX + 15) + 'px');
        })
        .on('mouseout', function () {
            tooltip.style('visibility', 'hidden');
            d3.select(this).select('circle').transition().duration(150)
                .attr('r', cellW * 0.02).attr('fill-opacity', 0.7);
        })
        .on('click', (event, d) => {
            event.stopPropagation();
            drillIntoSystem(d);
        });
    }

    // === DRILL-DOWN: SYSTEM ===
    function drillIntoSystem(sys) {
        currentLevel = 'system';
        currentSystemData = sys;
        dotNetRef.invokeMethodAsync('OnLevelChanged', 'system', sys.name);
        dotNetRef.invokeMethodAsync('OnSystemSelected', sys.id, sys.name);

        // Animate zoom to system center
        const targetScale = Math.min(width, height) / (cellW * 0.4);
        const tx = width / 2 - sys.x * targetScale;
        const ty = height / 2 - sys.y * targetScale;
        svg.transition().duration(700).ease(d3.easeCubicInOut)
            .call(zoom.transform, d3.zoomIdentity.translate(tx, ty).scale(targetScale));

        // Clear cell systems and render system detail
        setTimeout(() => {
            contentLayer.selectAll('*').remove();
            renderSystemDetail(sys);
        }, 400);
    }

    function renderSystemDetail(sys) {
        const cx = sys.x, cy = sys.y;
        const bodies = sys.celestialBodies || [];
        const color = sys.region ? getRegionColor(sys.region, overview.regions) : '#ffffff';
        const unit = cellW * 0.006; // base unit for sizing

        // Central star
        contentLayer.append('circle')
            .attr('cx', cx).attr('cy', cy)
            .attr('r', unit * 8)
            .attr('fill', color).attr('fill-opacity', 0.6)
            .attr('filter', 'url(#glow)');

        contentLayer.append('text')
            .attr('x', cx).attr('y', cy - unit * 12)
            .attr('text-anchor', 'middle').attr('fill', '#ffd866')
            .attr('font-size', `${unit * 5}px`).attr('font-weight', '600')
            .text(sys.name.replace(/ system$/i, '').replace(/\/Legends$/i, ''));

        if (bodies.length === 0) {
            contentLayer.append('text')
                .attr('x', cx).attr('y', cy + unit * 15)
                .attr('text-anchor', 'middle').attr('fill', 'rgba(255,255,255,0.4)')
                .attr('font-size', `${unit * 3}px`)
                .text('No known celestial bodies');
            return;
        }

        // Orbit rings and celestial bodies
        bodies.forEach((body, i) => {
            const orbitR = unit * (18 + i * 10);
            const angle = (i / bodies.length) * Math.PI * 2 + Math.random() * 0.5;
            const px = cx + Math.cos(angle) * orbitR;
            const py = cy + Math.sin(angle) * orbitR;
            const pColor = getPlanetColor(body.class);

            // Orbit ring
            contentLayer.append('circle')
                .attr('cx', cx).attr('cy', cy).attr('r', orbitR)
                .attr('fill', 'none')
                .attr('stroke', 'rgba(255,255,255,0.06)').attr('stroke-width', 0.3)
                .attr('stroke-dasharray', '2,2');

            // Celestial body
            const pg = contentLayer.append('g')
                .attr('transform', `translate(${px},${py})`)
                .style('cursor', 'pointer')
                .style('opacity', 0);

            pg.transition().delay(i * 80).duration(400).style('opacity', 1);

            pg.append('circle')
                .attr('r', unit * 4)
                .attr('fill', pColor).attr('fill-opacity', 0.85)
                .attr('stroke', pColor).attr('stroke-opacity', 0.5).attr('stroke-width', 0.5);

            pg.append('text')
                .attr('dy', unit * -6).attr('text-anchor', 'middle')
                .attr('fill', '#e0e0f0').attr('font-size', `${unit * 3}px`)
                .style('pointer-events', 'none')
                .text(body.name);

            if (body.class) {
                pg.append('text')
                    .attr('dy', unit * 7).attr('text-anchor', 'middle')
                    .attr('fill', 'rgba(255,255,255,0.3)').attr('font-size', `${unit * 2}px`)
                    .style('pointer-events', 'none')
                    .text(body.class);
            }

            pg.on('mouseover', function () {
                tooltip.html(`<strong>${body.name}</strong>${body.class ? '<br>Class: ' + body.class : ''}<br><span style="color:#888">Click for details</span>`)
                    .style('visibility', 'visible');
                d3.select(this).select('circle').transition().duration(150).attr('r', unit * 6);
            })
            .on('mousemove', (event) => {
                tooltip.style('top', (event.offsetY - 10) + 'px').style('left', (event.offsetX + 15) + 'px');
            })
            .on('mouseout', function () {
                tooltip.style('visibility', 'hidden');
                d3.select(this).select('circle').transition().duration(150).attr('r', unit * 4);
            })
            .on('click', (event) => {
                event.stopPropagation();
                dotNetRef.invokeMethodAsync('OnCelestialBodySelected', body.id, body.name);
            });
        });
    }

    // === BACK NAVIGATION ===
    function goBack() {
        tooltip.style('visibility', 'hidden');

        if (currentLevel === 'system') {
            if (currentCell) {
                // Back to cell view
                currentLevel = 'cell';
                currentSystemData = null;
                dotNetRef.invokeMethodAsync('OnLevelChanged', 'cell',
                    `${String.fromCharCode(65 + currentCell.col)}-${currentCell.row + 1}`);
                dotNetRef.invokeMethodAsync('OnSystemDeselected');

                const cs = Math.min(width / cellW, height / cellH) * 0.85;
                svg.transition().duration(600).ease(d3.easeCubicInOut)
                    .call(zoom.transform, d3.zoomIdentity.translate(
                        width / 2 - (colX(currentCell.col) + cellW / 2) * cs,
                        height / 2 - (rowY(currentCell.row) + cellH / 2) * cs
                    ).scale(cs));

                contentLayer.selectAll('*').remove();
                dotNetRef.invokeMethodAsync('FetchSystemsInRange',
                    currentCell.col, currentCell.col, currentCell.row, currentCell.row)
                    .then(result => {
                        if (result && result.systems) {
                            lastCellSystems = result.systems;
                            renderCellSystems(result.systems, currentCell.col, currentCell.row);
                        }
                    });
            } else if (currentRegion) {
                // Back to region view
                currentLevel = 'region';
                currentSystemData = null;
                dotNetRef.invokeMethodAsync('OnLevelChanged', 'region', currentRegion.name);
                dotNetRef.invokeMethodAsync('OnSystemDeselected');

                // Re-zoom to region bounding box
                let minCol = Infinity, maxCol = -Infinity, minRow = Infinity, maxRow = -Infinity;
                for (const [col, row] of currentRegion.cells) {
                    minCol = Math.min(minCol, col);
                    maxCol = Math.max(maxCol, col);
                    minRow = Math.min(minRow, row);
                    maxRow = Math.max(maxRow, row);
                }
                const rw = (maxCol - minCol + 1) * cellW;
                const rh = (maxRow - minRow + 1) * cellH;
                const rs = Math.min(width / rw, height / rh) * 0.8;
                const rcx = colX(minCol) + rw / 2;
                const rcy = rowY(minRow) + rh / 2;
                svg.transition().duration(600).ease(d3.easeCubicInOut)
                    .call(zoom.transform, d3.zoomIdentity.translate(
                        width / 2 - rcx * rs,
                        height / 2 - rcy * rs
                    ).scale(rs));

                contentLayer.selectAll('*').remove();
                if (lastRegionSystems) {
                    renderRegionSystems(lastRegionSystems, currentRegion);
                }
            } else {
                // Fallback: back to overview
                resetToOverview();
            }
        } else if (currentLevel === 'region' || currentLevel === 'cell') {
            resetToOverview();
        }
    }

    function resetToOverview() {
        currentLevel = 'overview';
        currentCell = null;
        currentRegion = null;
        currentSystemData = null;
        dotNetRef.invokeMethodAsync('OnLevelChanged', 'overview', '');
        dotNetRef.invokeMethodAsync('OnSystemDeselected');

        contentLayer.selectAll('*').remove();

        // Restore all region groups
        regionLayer.selectAll('.region-group')
            .transition().duration(400).style('opacity', 1);
        regionLayer.selectAll('.region-cell')
            .transition().duration(400)
            .attr('fill-opacity', 0.07).attr('stroke-opacity', 0.12).attr('stroke-width', 0.5);
        regionLayer.selectAll('.region-label')
            .transition().duration(400).attr('fill-opacity', 0.5);

        // Restore cell layer and indicator layer
        cellLayer.transition().duration(400).style('opacity', 1).style('pointer-events', 'auto');
        indicatorLayer.transition().duration(400).style('opacity', 1);

        // Re-apply hidden region visibility
        applyRegionVisibility();

        svg.transition().duration(700).ease(d3.easeCubicInOut)
            .call(zoom.transform, overviewTransform);
    }

    // === REGION VISIBILITY FILTER ===
    function applyRegionVisibility() {
        const allHidden = hiddenRegions.size >= overview.regions.length && overview.regions.length > 0;

        regionLayer.selectAll('.region-group').each(function () {
            const el = d3.select(this);
            const name = el.attr('data-region');
            const hidden = hiddenRegions.has(name);
            el.transition().duration(300)
                .style('opacity', hidden ? 0 : 1)
                .style('pointer-events', hidden ? 'none' : 'auto');
        });

        // Hide/show indicator dots based on region visibility
        indicatorLayer.selectAll('g').each(function () {
            const el = d3.select(this);
            const d = el.datum();
            if (!d) return;
            const hidden = allHidden || (d.region && hiddenRegions.has(d.region));
            if (hidden) {
                el.transition().duration(300).style('opacity', 0);
            } else if (currentLevel === 'overview') {
                el.transition().duration(300).style('opacity', 1);
            }
        });

        // Hide/show grid click targets based on region visibility
        cellLayer.selectAll('.grid-click-target').each(function () {
            const el = d3.select(this);
            const d = el.datum();
            if (!d) return;
            const info = cellDataMap[`${d.col},${d.row}`];
            const region = info ? info.region : null;
            const hidden = allHidden || (region && hiddenRegions.has(region));
            el.style('pointer-events', hidden ? 'none' : 'auto');
        });
    }

    function setRegionVisibility(hiddenNames) {
        hiddenRegions = new Set(hiddenNames || []);
        if (currentLevel === 'overview') {
            applyRegionVisibility();
        }
    }

    function setSystemFilter(value) {
        onlyWithBodies = value;
        if (currentLevel === 'cell' && currentCell && lastCellSystems) {
            renderCellSystems(lastCellSystems, currentCell.col, currentCell.row);
        } else if (currentLevel === 'region' && currentRegion && lastRegionSystems) {
            renderRegionSystems(lastRegionSystems, currentRegion);
        }
    }

    // Expose navigation functions
    // Build region cell map for temporal layer rendering
    const regionCellMap = {};
    overview.regions.forEach(r => { regionCellMap[r.name] = r.cells; });

    _state = {
        svg, container, goBack, drillIntoCell, drillIntoRegion,
        getCurrentLevel: () => currentLevel,
        setSystemFilter, setRegionVisibility,
        // Temporal layer state
        territoryLayer, heatmapLayer, markerLayer, tooltip, dotNetRef,
        cellW, cellH, cols, rows, startCol, startRow, colX, rowY, regionCellMap, overview,
        currentYearData: null, selectedLens: 'All',
    };
}

function getPlanetColor(cls) {
    if (!cls) return '#6bcb77';
    const c = cls.toLowerCase();
    if (c.includes('terrestrial')) return '#6bcb77';
    if (c.includes('gas')) return '#ff9f43';
    if (c.includes('ice')) return '#74b9ff';
    if (c.includes('aquatic') || c.includes('ocean')) return '#0984e3';
    if (c.includes('volcanic') || c.includes('molten')) return '#ff6b6b';
    return '#6bcb77';
}

export function goBack() {
    if (_state && _state.goBack) _state.goBack();
}

export function navigateToCell(col, row) {
    if (!_state) return;
    const level = _state.getCurrentLevel();

    if (level === 'overview') {
        _state.drillIntoCell(col, row);
    } else if (level === 'cell' || level === 'region') {
        _state.goBack();
        setTimeout(() => {
            if (_state) _state.drillIntoCell(col, row);
        }, 800);
    } else if (level === 'system') {
        _state.goBack();
        setTimeout(() => {
            if (_state) {
                _state.goBack();
                setTimeout(() => {
                    if (_state) _state.drillIntoCell(col, row);
                }, 800);
            }
        }, 700);
    }
}

export function setSystemFilter(value) {
    if (_state && _state.setSystemFilter) _state.setSystemFilter(value);
}

export function setRegionVisibility(hiddenNames) {
    if (_state && _state.setRegionVisibility) _state.setRegionVisibility(hiddenNames);
}

export function drillIntoRegion(regionName) {
    if (_state && _state.drillIntoRegion) _state.drillIntoRegion(regionName);
}

export function dispose() {
    if (_state) {
        _state.container.querySelectorAll(':scope > svg, :scope > .galaxy-tooltip').forEach(el => el.remove());
        _state = null;
    }
    Object.keys(regionColorMap).forEach(k => delete regionColorMap[k]);
}

// ═══════════════════════════════════════════════════
// TEMPORAL LAYERS (territory control + event heatmap)
// ═══════════════════════════════════════════════════

export function renderTemporalLayer(yearData, activeLayers, selectedLens) {
    if (!_state) return;
    _state.currentYearData = yearData;
    _state.selectedLens = selectedLens || 'All';
    const layers = new Set(activeLayers || []);

    _state.territoryLayer.style('display', layers.has('territory') ? null : 'none');
    _state.heatmapLayer.style('display', layers.has('events') ? null : 'none');
    _state.markerLayer.style('display', layers.has('events') ? null : 'none');

    _renderTerritory();
    _renderHeatmap();
}

export function setLayerVisibility(activeLayers) {
    if (!_state) return;
    const layers = new Set(activeLayers);
    _state.territoryLayer.style('display', layers.has('territory') ? null : 'none');
    _state.heatmapLayer.style('display', layers.has('events') ? null : 'none');
    _state.markerLayer.style('display', layers.has('events') ? null : 'none');
}

export function setLensFilter(lens) {
    if (!_state) return;
    _state.selectedLens = lens;
    _renderHeatmap();
}

export function clearTemporalLayers() {
    if (!_state) return;
    _state.territoryLayer.selectAll('*').remove();
    _state.heatmapLayer.selectAll('*').remove();
    _state.markerLayer.selectAll('*').remove();
}

export function setupPanelHoverListeners(panelId) {
    const panel = document.getElementById(panelId);
    if (!panel) return;
    panel.addEventListener('mouseover', (e) => {
        const el = e.target.closest('[data-highlight-region]');
        if (el) {
            const name = el.dataset.highlightRegion;
            d3.selectAll(`.region-group[data-region="${name}"] .region-cell`)
                .attr('stroke-opacity', 0.5).attr('stroke-width', 2);
        }
    });
    panel.addEventListener('mouseout', (e) => {
        const el = e.target.closest('[data-highlight-region]');
        if (el) {
            d3.selectAll('.region-cell')
                .attr('stroke-opacity', 0.12).attr('stroke-width', 0.5);
        }
    });
}

function _renderTerritory() {
    if (!_state) return;
    const { territoryLayer, cellW, cellH, colX, rowY, regionCellMap, currentYearData } = _state;
    territoryLayer.selectAll('*').remove();
    if (!currentYearData || !currentYearData.regions) return;

    for (const regionCtrl of currentYearData.regions) {
        const cells = regionCellMap[regionCtrl.region];
        if (!cells || cells.length === 0) continue;
        const dom = regionCtrl.factions[0];
        if (!dom) continue;

        for (const [col, row] of cells) {
            territoryLayer.append('rect')
                .attr('x', colX(col)).attr('y', rowY(row))
                .attr('width', cellW).attr('height', cellH)
                .attr('fill', dom.color)
                .attr('fill-opacity', 0.1 + dom.control * 0.25)
                .attr('stroke', dom.color)
                .attr('stroke-opacity', 0.1 + dom.control * 0.15)
                .attr('stroke-width', 0.5)
                .style('pointer-events', 'none');
        }

        if (regionCtrl.factions.length > 1) {
            for (let i = 1; i < regionCtrl.factions.length; i++) {
                const f = regionCtrl.factions[i];
                const subset = cells.filter((_, idx) => idx % regionCtrl.factions.length === i);
                for (const [col, row] of subset) {
                    territoryLayer.append('rect')
                        .attr('x', colX(col)).attr('y', rowY(row))
                        .attr('width', cellW).attr('height', cellH)
                        .attr('fill', f.color).attr('fill-opacity', f.control * 0.3)
                        .style('pointer-events', 'none');
                }
            }
        }
    }
}

function _renderHeatmap() {
    if (!_state) return;
    const { heatmapLayer, markerLayer, tooltip, cellW, cellH, colX, rowY, currentYearData, selectedLens } = _state;
    heatmapLayer.selectAll('*').remove();
    markerLayer.selectAll('*').remove();
    if (!currentYearData || !currentYearData.eventCells) return;

    const filteredCells = [];
    for (const cell of currentYearData.eventCells) {
        if (selectedLens === 'None') continue;
        const events = selectedLens === 'All' ? cell.events : cell.events.filter(e => e.lens === selectedLens);
        if (events.length === 0) continue;
        filteredCells.push({ ...cell, events, count: events.length });
    }
    if (filteredCells.length === 0) return;

    const maxCount = Math.max(...filteredCells.map(c => c.count), 1);

    for (const cell of filteredCells) {
        const cx = colX(cell.col) + cellW / 2;
        const cy = rowY(cell.row) + cellH / 2;
        const intensity = cell.count / maxCount;
        const radius = cellW * 0.3 + cellW * 0.5 * intensity;

        // Per-cell color: use the most common lens in this cell
        const cellColor = selectedLens !== 'All'
            ? getLensColor(selectedLens)
            : (() => {
                const counts = {};
                cell.events.forEach(e => { counts[e.lens] = (counts[e.lens] || 0) + 1; });
                const top = Object.entries(counts).sort((a, b) => b[1] - a[1])[0];
                return top ? getLensColor(top[0]) : '#ff6b6b';
            })();

        heatmapLayer.append('circle')
            .attr('cx', cx).attr('cy', cy).attr('r', radius * 1.4)
            .attr('fill', cellColor).attr('fill-opacity', intensity * 0.12)
            .attr('filter', 'url(#glow)');

        heatmapLayer.append('circle')
            .attr('cx', cx).attr('cy', cy).attr('r', radius)
            .attr('fill', cellColor).attr('fill-opacity', 0.08 + intensity * 0.4)
            .attr('stroke', cellColor).attr('stroke-opacity', 0.2 + intensity * 0.5)
            .attr('stroke-width', 1)
            .style('cursor', 'pointer')
            .on('mouseover', function (event) {
                d3.select(this).attr('stroke-width', 2).attr('fill-opacity', 0.25 + intensity * 0.4);
                const rect = _state.container.getBoundingClientRect();
                const screenX = rect.left + event.offsetX;
                const screenY = rect.top + event.offsetY;
                const contMap = { 0: 'Unknown', 1: 'Canon', 2: 'Legends' };
                const evts = cell.events.map(e => ({
                    title: e.title, lens: e.lens, category: e.category,
                    place: e.place, outcome: e.outcome, wikiUrl: e.wikiUrl,
                    pageId: e.pageId ?? null,
                    continuity: typeof e.continuity === 'string' ? e.continuity : (contMap[e.continuity] ?? 'Unknown'),
                }));
                if (_state.dotNetRef) {
                    _state.dotNetRef.invokeMethodAsync('OnEventCellHovered', cell.region || 'Unknown', cell.col, cell.row, screenX, screenY, evts);
                }
            })
            .on('mouseout', function () {
                d3.select(this).attr('stroke-width', 1).attr('fill-opacity', 0.08 + intensity * 0.4);
                // Don't close immediately — let Blazor handle it when mouse leaves the popover
            });

        if (cell.count >= 3) {
            markerLayer.append('text')
                .attr('x', cx).attr('y', cy + 4)
                .attr('text-anchor', 'middle')
                .attr('fill', '#fff').attr('fill-opacity', 0.7 + intensity * 0.3)
                .attr('font-size', Math.max(11, 9 + intensity * 8) + 'px')
                .attr('font-weight', 'bold')
                .style('pointer-events', 'none')
                .text(cell.count);
        }
    }

    // Pulse animation for top hotspots
    filteredCells.slice(0, 5).forEach(cell => {
        if (cell.count / maxCount < 0.3) return;
        const cx = colX(cell.col) + cellW / 2;
        const cy = rowY(cell.row) + cellH / 2;
        const radius = cellW * 0.3 + cellW * 0.5 * (cell.count / maxCount);
        const counts = {};
        cell.events.forEach(e => { counts[e.lens] = (counts[e.lens] || 0) + 1; });
        const topLens = Object.entries(counts).sort((a, b) => b[1] - a[1])[0];
        const pulseColor = selectedLens !== 'All' ? getLensColor(selectedLens) : (topLens ? getLensColor(topLens[0]) : '#ff6b6b');
        const pulse = markerLayer.append('circle')
            .attr('cx', cx).attr('cy', cy).attr('r', radius)
            .attr('fill', 'none').attr('stroke', pulseColor)
            .attr('stroke-opacity', 0.5).attr('stroke-width', 1.5)
            .style('pointer-events', 'none');
        pulse.append('animate').attr('attributeName', 'r').attr('from', radius).attr('to', radius * 1.8).attr('dur', '2s').attr('repeatCount', 'indefinite');
        pulse.append('animate').attr('attributeName', 'stroke-opacity').attr('from', '0.5').attr('to', '0').attr('dur', '2s').attr('repeatCount', 'indefinite');
    });
}

export function toggleBackground(visible) {
    if (!_state) return;
    _state.svg.select('.layer-bg').style('display', visible ? null : 'none');
}

export function toggleTradeRoutes(visible) {
    if (!_state) return;
    _state.svg.select('.layer-routes').style('display', visible ? null : 'none');
}

export function toggleNebulae(visible) {
    if (!_state) return;
    _state.svg.select('.layer-nebulae').style('display', visible ? null : 'none');
}
