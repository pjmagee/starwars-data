// Territory Control D3.js visualization
// Renders faction-controlled galactic regions on a 26x20 grid with timeline scrubbing

const BG_URL = '/galaxy.png';

const REGION_PALETTE = [
    '#4fc3f7','#81c784','#ffb74d','#e57373','#ba68c8',
    '#4db6ac','#fff176','#f06292','#7986cb','#a1887f',
    '#90a4ae','#aed581','#ce93d8','#64b5f6','#dce775'
];

let _state = null;
const regionColorMap = {};

export function initialize(containerId, overview, regionCells, factionColors, factionWikiUrls, factionIcons, dotNetRef) {
    const container = document.getElementById(containerId);
    if (!container) return;

    const width = container.clientWidth || 1200;
    const height = container.clientHeight || 800;

    const cols = overview.gridColumns || 26;
    const rows = overview.gridRows || 20;
    const worldW = 2600, worldH = 2000;
    const cellW = worldW / cols, cellH = worldH / rows;

    d3.select(container).select('svg').remove();

    const svg = d3.select(container).append('svg')
        .attr('width', width).attr('height', height)
        .style('background', '#0a0a1a');

    const g = svg.append('g');
    const bgLayer = g.append('g').attr('class', 'bg-layer');
    const regionLayer = g.append('g').attr('class', 'region-layer');
    const gridLayer = g.append('g').attr('class', 'grid-layer');
    const territoryLayer = g.append('g').attr('class', 'territory-layer');
    const regionLabelLayer = g.append('g').attr('class', 'region-label-layer');
    const labelLayer = g.append('g').attr('class', 'label-layer');

    const tooltip = d3.select(container).append('div')
        .style('position', 'absolute').style('pointer-events', 'none')
        .style('background', 'rgba(20,20,40,0.95)').style('color', '#e0e0e0')
        .style('padding', '10px 14px').style('border-radius', '8px')
        .style('font-size', '13px').style('line-height', '1.5')
        .style('box-shadow', '0 4px 20px rgba(0,0,0,0.5)')
        .style('border', '1px solid rgba(255,255,255,0.15)')
        .style('max-width', '300px').style('z-index', '100')
        .style('display', 'none');

    // Background galaxy image
    bgLayer.append('image')
        .attr('href', BG_URL)
        .attr('x', -80).attr('y', 40)
        .attr('width', worldW).attr('height', worldH)
        .attr('preserveAspectRatio', 'xMidYMid slice')
        .attr('opacity', 0.3);

    // Region background — subtle outlines only
    if (regionCells) {
        for (const region of regionCells) {
            const color = getRegionColor(region.name, regionCells);
            for (const [col, row] of region.cells) {
                regionLayer.append('rect')
                    .attr('x', col * cellW).attr('y', row * cellH)
                    .attr('width', cellW).attr('height', cellH)
                    .attr('fill', 'none')
                    .attr('stroke', color).attr('stroke-opacity', 0.06)
                    .attr('stroke-width', 0.5)
                    .attr('data-region', region.name);
            }
            // Region name at centroid
            const cx = region.cells.reduce((s, c) => s + c[0], 0) / region.cells.length * cellW + cellW / 2;
            const cy = region.cells.reduce((s, c) => s + c[1], 0) / region.cells.length * cellH + cellH / 2;
            regionLabelLayer.append('text')
                .attr('x', cx).attr('y', cy)
                .attr('text-anchor', 'middle').attr('dominant-baseline', 'central')
                .attr('fill', '#fff').attr('fill-opacity', 0.4)
                .attr('font-size', '16px').attr('font-weight', '700')
                .attr('paint-order', 'stroke').attr('stroke', 'rgba(0,0,0,0.7)')
                .attr('stroke-width', '3px')
                .style('pointer-events', 'none')
                .text(region.name);
        }
    }

    // Grid lines only (no column/row labels for cleaner look)
    const alphabet = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ';
    for (let c = 0; c <= cols; c++) {
        gridLayer.append('line')
            .attr('x1', c * cellW).attr('y1', 0)
            .attr('x2', c * cellW).attr('y2', worldH)
            .attr('stroke', 'rgba(255,255,255,0.03)').attr('stroke-width', 0.5);
    }
    for (let r = 0; r <= rows; r++) {
        gridLayer.append('line')
            .attr('x1', 0).attr('y1', r * cellH)
            .attr('x2', worldW).attr('y2', r * cellH)
            .attr('stroke', 'rgba(255,255,255,0.03)').attr('stroke-width', 0.5);
    }

    // Zoom/pan
    const zoom = d3.zoom().scaleExtent([0.3, 8]).on('zoom', e => g.attr('transform', e.transform));
    svg.call(zoom);
    const initialScale = Math.min(width / worldW, height / worldH) * 0.9;
    const tx = (width - worldW * initialScale) / 2;
    const ty = (height - worldH * initialScale) / 2;
    svg.call(zoom.transform, d3.zoomIdentity.translate(tx, ty).scale(initialScale));

    _state = {
        container, svg, g, territoryLayer, labelLayer, tooltip,
        worldW, worldH, cellW, cellH, cols, rows,
        regionCells, factionColors: factionColors || {},
        factionWikiUrls: factionWikiUrls || {},
        factionIcons: factionIcons || {},
        dotNetRef, zoom
    };

    // Client-side hover listeners for info panel items (no SignalR round-trip)
    setupPanelHoverListeners();
}

function setupPanelHoverListeners() {
    const panel = document.querySelector('.territory-info-panel');
    if (!panel) return;

    let currentRegion = null;

    panel.addEventListener('mouseover', (e) => {
        const el = e.target.closest('[data-highlight-region]');
        if (!el) return;
        const region = el.dataset.highlightRegion;
        if (region === currentRegion) return;
        currentRegion = region;
        softHighlightRegion(region);
    });

    panel.addEventListener('mouseout', (e) => {
        const el = e.target.closest('[data-highlight-region]');
        if (!el) return;
        const related = e.relatedTarget?.closest?.('[data-highlight-region]');
        if (related) return;
        currentRegion = null;
        clearSoftHighlight();
    });
}

// Lightweight highlight — boost opacity on matching region cells via data attribute
function softHighlightRegion(regionName) {
    if (!_state) return;
    _state.regionLayer.selectAll('rect')
        .attr('stroke-opacity', function() {
            return this.getAttribute('data-region') === regionName ? 0.6 : 0.03;
        })
        .attr('stroke-width', function() {
            return this.getAttribute('data-region') === regionName ? 2 : 0.5;
        });
}

function clearSoftHighlight() {
    if (!_state) return;
    _state.regionLayer.selectAll('rect')
        .attr('stroke-opacity', 0.06)
        .attr('stroke-width', 0.5);
}

export function renderTerritoryLayer(yearData) {
    if (!_state) return;
    const { territoryLayer, labelLayer, tooltip, cellW, cellH,
            factionColors, factionWikiUrls, factionIcons, dotNetRef } = _state;

    territoryLayer.selectAll('*').remove();
    labelLayer.selectAll('*').remove();

    if (!yearData || !yearData.regions) return;

    // Build region -> cells lookup
    const regionCellMap = {};
    if (_state.regionCells) {
        for (const region of _state.regionCells) {
            regionCellMap[region.name] = region.cells;
        }
    }

    for (const regionControl of yearData.regions) {
        const cells = regionCellMap[regionControl.region];
        if (!cells || cells.length === 0) continue;

        const factions = regionControl.factions;
        if (!factions || factions.length === 0) continue;

        const dominant = factions[0];
        const color = dominant.color || factionColors[dominant.faction] || '#888';
        const opacity = 0.1 + dominant.control * 0.3;

        // Fill all cells with dominant faction color
        for (const [col, row] of cells) {
            territoryLayer.append('rect')
                .attr('x', col * cellW).attr('y', row * cellH)
                .attr('width', cellW).attr('height', cellH)
                .attr('fill', color).attr('fill-opacity', opacity)
                .attr('stroke', color).attr('stroke-opacity', opacity + 0.1)
                .attr('stroke-width', 0.5);
        }

        // Invisible click/hover targets (one per cell)
        for (const [col, row] of cells) {
            territoryLayer.append('rect')
                .attr('x', col * cellW).attr('y', row * cellH)
                .attr('width', cellW).attr('height', cellH)
                .attr('fill', 'transparent').attr('cursor', 'pointer')
                .on('mouseover', (event) => {
                    let html = `<strong>${regionControl.region}</strong><br/>`;
                    for (const f of factions) {
                        const pct = Math.round(f.control * 100);
                        const fc = f.color || factionColors[f.faction] || '#888';
                        const dot = `<span style="display:inline-block;width:8px;height:8px;border-radius:50%;background:${fc};margin-right:4px;"></span>`;
                        const wikiUrl = factionWikiUrls[f.faction];
                        const name = wikiUrl
                            ? `<a href="${wikiUrl}" target="_blank" style="color:${fc};text-decoration:underline;">${f.faction}</a>`
                            : `<span style="color:${fc}">${f.faction}</span>`;
                        html += `${dot}${name}: ${pct}%`;
                        if (f.contested) html += ' <em style="opacity:0.6">(contested)</em>';
                        html += '<br/>';
                    }
                    if (factions[0]?.note) html += `<br/><em style="opacity:0.6;font-size:11px;">${factions[0].note}</em>`;
                    tooltip.html(html).style('display', 'block').style('pointer-events', 'auto');
                })
                .on('mousemove', (event) => {
                    tooltip.style('left', (event.offsetX + 15) + 'px')
                           .style('top', (event.offsetY - 10) + 'px');
                })
                .on('mouseout', () => { tooltip.style('display', 'none').style('pointer-events', 'none'); })
                .on('click', () => {
                    if (dotNetRef) {
                        dotNetRef.invokeMethodAsync('OnTerritoryCellSelected',
                            regionControl.region,
                            factions.map(f => ({ faction: f.faction, control: f.control, contested: f.contested, color: f.color, note: f.note }))
                        );
                    }
                });
        }

        // Centroid for icon + label (one per region, not per cell)
        const cx = cells.reduce((s, c) => s + c[0], 0) / cells.length * cellW + cellW / 2;
        const cy = cells.reduce((s, c) => s + c[1], 0) / cells.length * cellH + cellH / 2;

        // No icons or labels on canvas — info panel handles context
    }
}

export function clearTerritoryLayer() {
    if (!_state) return;
    _state.territoryLayer.selectAll('*').remove();
    _state.labelLayer.selectAll('*').remove();
}

export function highlightRegion(regionName) {
    if (!_state || !_state.regionCells) return;
    clearHighlight();

    const region = _state.regionCells.find(r => r.name === regionName);
    if (!region) return;

    const { cellW, cellH } = _state;
    const hlLayer = _state.g.append('g').attr('class', 'highlight-layer');

    for (const [col, row] of region.cells) {
        hlLayer.append('rect')
            .attr('x', col * cellW).attr('y', row * cellH)
            .attr('width', cellW).attr('height', cellH)
            .attr('fill', '#fff').attr('fill-opacity', 0)
            .attr('stroke', '#fff').attr('stroke-opacity', 0.8)
            .attr('stroke-width', 2)
            .attr('rx', 2);
    }

    // Pulse animation
    (function pulse() {
        hlLayer.selectAll('rect')
            .transition().duration(800)
            .attr('fill-opacity', 0.15).attr('stroke-opacity', 1)
            .transition().duration(800)
            .attr('fill-opacity', 0).attr('stroke-opacity', 0.5)
            .on('end', pulse);
    })();

    // Zoom to region
    const xs = region.cells.map(c => c[0] * cellW);
    const ys = region.cells.map(c => c[1] * cellH);
    const x0 = Math.min(...xs), x1 = Math.max(...xs) + cellW;
    const y0 = Math.min(...ys), y1 = Math.max(...ys) + cellH;
    const pad = cellW;
    const { svg, zoom } = _state;
    const w = svg.attr('width'), h = svg.attr('height');
    const scale = Math.min(w / (x1 - x0 + pad * 2), h / (y1 - y0 + pad * 2), 3);
    const cx = (x0 + x1) / 2, cy = (y0 + y1) / 2;
    svg.transition().duration(600)
        .call(zoom.transform, d3.zoomIdentity
            .translate(w / 2, h / 2)
            .scale(scale)
            .translate(-cx, -cy));
}

export function clearHighlight() {
    if (!_state) return;
    _state.g.selectAll('.highlight-layer').remove();
}

export function highlightCell(col, row, color) {
    if (!_state) return;
    clearHighlight();

    const { cellW, cellH, svg, zoom } = _state;
    const hlLayer = _state.g.append('g').attr('class', 'highlight-layer');
    const c = color || '#fff';

    // Marker ring at cell center
    const cx = col * cellW + cellW / 2;
    const cy = row * cellH + cellH / 2;
    const r = Math.min(cellW, cellH) * 0.4;

    hlLayer.append('circle')
        .attr('cx', cx).attr('cy', cy).attr('r', r)
        .attr('fill', 'none').attr('stroke', c).attr('stroke-width', 3)
        .attr('stroke-opacity', 0);

    hlLayer.append('circle')
        .attr('cx', cx).attr('cy', cy).attr('r', r * 0.3)
        .attr('fill', c).attr('fill-opacity', 0);

    // Pulse
    (function pulse() {
        hlLayer.selectAll('circle')
            .transition().duration(600)
            .attr('stroke-opacity', 1).attr('fill-opacity', 0.6)
            .transition().duration(600)
            .attr('stroke-opacity', 0.3).attr('fill-opacity', 0.2)
            .on('end', pulse);
    })();

    // Zoom to cell
    const w = svg.attr('width'), h = svg.attr('height');
    const scale = Math.min(w / (cellW * 5), h / (cellH * 5), 4);
    svg.transition().duration(600)
        .call(zoom.transform, d3.zoomIdentity
            .translate(w / 2, h / 2)
            .scale(scale)
            .translate(-cx, -cy));
}

export function resetZoom() {
    if (!_state) return;
    const { svg, zoom, worldW, worldH } = _state;
    const w = svg.attr('width'), h = svg.attr('height');
    const scale = Math.min(w / worldW, h / worldH) * 0.9;
    const tx = (w - worldW * scale) / 2;
    const ty = (h - worldH * scale) / 2;
    svg.transition().duration(600)
        .call(zoom.transform, d3.zoomIdentity.translate(tx, ty).scale(scale));
}

function getRegionColor(name, regions) {
    if (!regionColorMap[name]) {
        const idx = regions.findIndex(r => r.name === name);
        regionColorMap[name] = REGION_PALETTE[idx % REGION_PALETTE.length];
    }
    return regionColorMap[name];
}
