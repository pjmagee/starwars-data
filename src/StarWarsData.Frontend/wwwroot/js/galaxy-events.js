// D3.js Galaxy Events - Timeline-aware heatmap visualization
// Renders galaxy regions as base layer, overlays event data as heatmaps

const REGION_PALETTE = [
    '#4ecdc4', '#ff6f91', '#7e6fff', '#ffd93d', '#6bcb77',
    '#ff6b6b', '#a29bfe', '#fd79a8', '#fdcb6e', '#00cec9',
    '#e17055', '#0984e3', '#6c5ce7', '#00b894', '#e84393',
    '#fab1a0', '#74b9ff', '#55efc4', '#ff7675', '#dfe6e9',
];

// Compute lens color from name hash — matches the C# GetLensColor method
function getLensColor(lens) {
    let hash = 0;
    for (const c of lens) hash = (hash * 31 + c.charCodeAt(0)) | 0;
    const hue = ((hash % 360) + 360) % 360;
    return `hsl(${hue}, 75%, 55%)`;
}

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
    container.querySelectorAll(':scope > svg, :scope > .galaxy-events-tooltip').forEach(el => el.remove());

    const width = container.clientWidth || 1200;
    const height = container.clientHeight || 800;
    const cols = overview.gridColumns;
    const rows = overview.gridRows;

    const worldW = 2600;
    const worldH = 2000;
    const cellW = worldW / cols;
    const cellH = worldH / rows;

    const svg = d3.select(container)
        .append('svg')
        .attr('width', width)
        .attr('height', height)
        .style('background', '#0a0a1a');

    const defs = svg.append('defs');

    // Glow filter for events
    const glow = defs.append('filter').attr('id', 'event-glow');
    glow.append('feGaussianBlur').attr('stdDeviation', '4').attr('result', 'coloredBlur');
    const fm = glow.append('feMerge');
    fm.append('feMergeNode').attr('in', 'coloredBlur');
    fm.append('feMergeNode').attr('in', 'SourceGraphic');

    // Radial gradient for heatmap cells
    const radGrad = defs.append('radialGradient').attr('id', 'heat-gradient');
    radGrad.append('stop').attr('offset', '0%').attr('stop-color', '#fff').attr('stop-opacity', 0.8);
    radGrad.append('stop').attr('offset', '50%').attr('stop-color', '#ff4444').attr('stop-opacity', 0.5);
    radGrad.append('stop').attr('offset', '100%').attr('stop-color', '#ff4444').attr('stop-opacity', 0);

    const g = svg.append('g');

    // Layers
    const bgLayer = g.append('g');
    const regionLayer = g.append('g');
    const gridLayer = g.append('g');
    const heatmapLayer = g.append('g').attr('class', 'heatmap-layer');
    const markerLayer = g.append('g').attr('class', 'marker-layer');

    // Tooltip
    const tooltip = d3.select(container).append('div')
        .attr('class', 'galaxy-events-tooltip')
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
        .style('max-width', '320px');

    // Background image
    bgLayer.append('image')
        .attr('href', BG_URL)
        .attr('x', -80).attr('y', 40)
        .attr('width', worldW).attr('height', worldH)
        .attr('preserveAspectRatio', 'xMidYMid slice')
        .attr('opacity', 0.35);

    // Grid lines (subtle)
    for (let c = 0; c <= cols; c++) {
        gridLayer.append('line')
            .attr('x1', c * cellW).attr('y1', 0)
            .attr('x2', c * cellW).attr('y2', worldH)
            .attr('stroke', 'rgba(255,255,255,0.04)')
            .attr('stroke-width', 0.5);
    }
    for (let r = 0; r <= rows; r++) {
        gridLayer.append('line')
            .attr('x1', 0).attr('y1', r * cellH)
            .attr('x2', worldW).attr('y2', r * cellH)
            .attr('stroke', 'rgba(255,255,255,0.04)')
            .attr('stroke-width', 0.5);
    }

    // Column labels
    for (let c = 0; c < cols; c++) {
        gridLayer.append('text')
            .attr('x', c * cellW + cellW / 2).attr('y', -8)
            .attr('text-anchor', 'middle')
            .attr('fill', 'rgba(255,255,255,0.35)')
            .attr('font-size', '13px').attr('font-weight', '600')
            .text(String.fromCharCode(65 + c));
    }
    // Row labels
    for (let r = 0; r < rows; r++) {
        gridLayer.append('text')
            .attr('x', -12).attr('y', r * cellH + cellH / 2 + 4)
            .attr('text-anchor', 'end')
            .attr('fill', 'rgba(255,255,255,0.35)')
            .attr('font-size', '13px').attr('font-weight', '600')
            .text(r + 1);
    }

    // Regions (subtle background cells)
    for (const region of overview.regions) {
        const color = getRegionColor(region.name, overview.regions);
        const rg = regionLayer.append('g').attr('class', 'region-group');

        for (const [col, row] of region.cells) {
            rg.append('rect')
                .attr('x', col * cellW).attr('y', row * cellH)
                .attr('width', cellW).attr('height', cellH)
                .attr('fill', color).attr('fill-opacity', 0.04)
                .attr('stroke', color).attr('stroke-opacity', 0.06).attr('stroke-width', 0.5);
        }

        // Region label at centroid
        if (region.cells.length > 0) {
            const cx = region.cells.reduce((s, c) => s + c[0], 0) / region.cells.length * cellW + cellW / 2;
            const cy = region.cells.reduce((s, c) => s + c[1], 0) / region.cells.length * cellH + cellH / 2;
            rg.append('text')
                .attr('x', cx).attr('y', cy)
                .attr('text-anchor', 'middle')
                .attr('dominant-baseline', 'middle')
                .attr('fill', color).attr('fill-opacity', 0.2)
                .attr('font-size', '12px')
                .attr('font-weight', 'bold')
                .text(region.name);
        }
    }

    // Zoom
    const zoom = d3.zoom()
        .scaleExtent([0.3, 8])
        .on('zoom', (event) => g.attr('transform', event.transform));

    svg.call(zoom);

    const initialScale = Math.min(width / worldW, height / worldH) * 0.9;
    const tx = (width - worldW * initialScale) / 2;
    const ty = (height - worldH * initialScale) / 2;
    svg.call(zoom.transform, d3.zoomIdentity.translate(tx, ty).scale(initialScale));

    _state = {
        container, svg, g, heatmapLayer, markerLayer, tooltip,
        worldW, worldH, cellW, cellH, cols, rows,
        overview, dotNetRef, zoom
    };
}

export function renderEventLayer(layerData) {
    if (!_state) return;
    const { heatmapLayer, markerLayer, tooltip, cellW, cellH } = _state;

    // Clear previous
    heatmapLayer.selectAll('*').remove();
    markerLayer.selectAll('*').remove();

    if (!layerData || !layerData.cells || layerData.cells.length === 0) return;

    const lens = layerData.lens || 'Battle';
    const lensColor = getLensColor(lens);

    // Render heatmap cells
    for (const cell of layerData.cells) {
        const cx = cell.col * cellW + cellW / 2;
        const cy = cell.row * cellH + cellH / 2;
        const intensity = cell.intensity;
        const radius = cellW * 0.4 + cellW * 0.6 * intensity;

        // Outer glow
        heatmapLayer.append('circle')
            .attr('cx', cx).attr('cy', cy)
            .attr('r', radius * 1.5)
            .attr('fill', lensColor)
            .attr('fill-opacity', intensity * 0.15)
            .attr('filter', 'url(#event-glow)');

        // Core circle
        heatmapLayer.append('circle')
            .attr('cx', cx).attr('cy', cy)
            .attr('r', radius)
            .attr('fill', lensColor)
            .attr('fill-opacity', 0.1 + intensity * 0.5)
            .attr('stroke', lensColor)
            .attr('stroke-opacity', 0.3 + intensity * 0.5)
            .attr('stroke-width', 1)
            .style('cursor', 'pointer')
            .on('mouseover', function (event) {
                d3.select(this)
                    .attr('stroke-width', 2)
                    .attr('fill-opacity', 0.3 + intensity * 0.5);

                const eventList = cell.events.slice(0, 5).map(e =>
                    `<div style="margin: 2px 0; font-size: 11px;">
                        <span style="color: ${lensColor};">\u25CF</span> ${e.title}
                        <span style="color: rgba(255,255,255,0.5);">(${e.year} ${e.demarcation})</span>
                    </div>`
                ).join('');

                const more = cell.count > 5 ? `<div style="color: rgba(255,255,255,0.4); font-size: 10px;">+${cell.count - 5} more</div>` : '';

                tooltip.html(`
                    <div style="font-weight: bold; margin-bottom: 4px; color: ${lensColor};">
                        ${cell.region || 'Unknown Region'} \u2022 ${cell.count} ${lens}${cell.count !== 1 ? 's' : ''}
                    </div>
                    <div style="color: rgba(255,255,255,0.5); font-size: 10px; margin-bottom: 4px;">
                        Grid: ${String.fromCharCode(65 + cell.col)}-${cell.row + 1}
                    </div>
                    ${eventList}${more}
                `)
                    .style('visibility', 'visible')
                    .style('left', (event.offsetX + 15) + 'px')
                    .style('top', (event.offsetY - 10) + 'px');
            })
            .on('mousemove', function (event) {
                tooltip
                    .style('left', (event.offsetX + 15) + 'px')
                    .style('top', (event.offsetY - 10) + 'px');
            })
            .on('mouseout', function () {
                d3.select(this)
                    .attr('stroke-width', 1)
                    .attr('fill-opacity', 0.1 + intensity * 0.5);
                tooltip.style('visibility', 'hidden');
            })
            .on('click', function () {
                if (_state.dotNetRef && cell.events.length > 0) {
                    _state.dotNetRef.invokeMethodAsync('OnEventCellSelected',
                        cell.col, cell.row, cell.region || '', cell.count);
                }
            });

        // Event count label for high-intensity cells
        if (cell.count >= 3) {
            markerLayer.append('text')
                .attr('x', cx).attr('y', cy)
                .attr('text-anchor', 'middle')
                .attr('dominant-baseline', 'middle')
                .attr('fill', '#fff')
                .attr('fill-opacity', 0.7 + intensity * 0.3)
                .attr('font-size', Math.max(10, 8 + intensity * 8) + 'px')
                .attr('font-weight', 'bold')
                .style('pointer-events', 'none')
                .text(cell.count);
        }
    }

    // Pulse animation for top hotspots
    const topCells = layerData.cells.slice(0, 5);
    for (const cell of topCells) {
        if (cell.intensity < 0.3) continue;
        const cx = cell.col * cellW + cellW / 2;
        const cy = cell.row * cellH + cellH / 2;
        const radius = cellW * 0.4 + cellW * 0.6 * cell.intensity;

        markerLayer.append('circle')
            .attr('cx', cx).attr('cy', cy)
            .attr('r', radius)
            .attr('fill', 'none')
            .attr('stroke', lensColor)
            .attr('stroke-opacity', 0.6)
            .attr('stroke-width', 1.5)
            .style('pointer-events', 'none')
            .append('animate')
            .attr('attributeName', 'r')
            .attr('from', radius)
            .attr('to', radius * 2)
            .attr('dur', '2s')
            .attr('repeatCount', 'indefinite');

        markerLayer.select('circle:last-child')
            .append('animate')
            .attr('attributeName', 'stroke-opacity')
            .attr('from', '0.6')
            .attr('to', '0')
            .attr('dur', '2s')
            .attr('repeatCount', 'indefinite');
    }
}

export function clearEventLayer() {
    if (!_state) return;
    _state.heatmapLayer.selectAll('*').remove();
    _state.markerLayer.selectAll('*').remove();
}

export function dispose() {
    if (_state) {
        _state.svg.remove();
        _state.tooltip.remove();
        _state = null;
    }
}
