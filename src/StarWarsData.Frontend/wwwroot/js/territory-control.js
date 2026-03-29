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

export function initialize(containerId, overview, regionCells, factionColors, factionWikiUrls, dotNetRef) {
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

    const defs = svg.append('defs');
    defs.append('filter').attr('id', 'territory-glow')
        .append('feGaussianBlur').attr('stdDeviation', '4').attr('result', 'blur');

    const g = svg.append('g');
    const bgLayer = g.append('g').attr('class', 'bg-layer');
    const regionLayer = g.append('g').attr('class', 'region-layer');
    const gridLayer = g.append('g').attr('class', 'grid-layer');
    const territoryLayer = g.append('g').attr('class', 'territory-layer');
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

    // Region background cells
    if (regionCells) {
        for (const region of regionCells) {
            const color = getRegionColor(region.name, regionCells);
            for (const [col, row] of region.cells) {
                regionLayer.append('rect')
                    .attr('x', col * cellW).attr('y', row * cellH)
                    .attr('width', cellW).attr('height', cellH)
                    .attr('fill', color).attr('fill-opacity', 0.03)
                    .attr('stroke', color).attr('stroke-opacity', 0.08)
                    .attr('stroke-width', 0.5);
            }
            // Region name at centroid
            const cx = region.cells.reduce((s, c) => s + c[0], 0) / region.cells.length * cellW + cellW / 2;
            const cy = region.cells.reduce((s, c) => s + c[1], 0) / region.cells.length * cellH + cellH / 2;
            regionLayer.append('text')
                .attr('x', cx).attr('y', cy)
                .attr('text-anchor', 'middle').attr('dominant-baseline', 'central')
                .attr('fill', '#fff').attr('fill-opacity', 0.35)
                .attr('font-size', '16px').attr('font-weight', '700')
                .attr('paint-order', 'stroke').attr('stroke', 'rgba(0,0,0,0.6)')
                .attr('stroke-width', '3px')
                .text(region.name);
        }
    }

    // Grid lines and labels
    const alphabet = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ';
    for (let c = 0; c <= cols; c++) {
        gridLayer.append('line')
            .attr('x1', c * cellW).attr('y1', 0)
            .attr('x2', c * cellW).attr('y2', worldH)
            .attr('stroke', 'rgba(255,255,255,0.05)').attr('stroke-width', 0.5);
        if (c < cols) {
            gridLayer.append('text')
                .attr('x', c * cellW + cellW / 2).attr('y', -8)
                .attr('text-anchor', 'middle').attr('fill', 'rgba(255,255,255,0.25)')
                .attr('font-size', '10px').text(alphabet[c]);
        }
    }
    for (let r = 0; r <= rows; r++) {
        gridLayer.append('line')
            .attr('x1', 0).attr('y1', r * cellH)
            .attr('x2', worldW).attr('y2', r * cellH)
            .attr('stroke', 'rgba(255,255,255,0.05)').attr('stroke-width', 0.5);
        if (r < rows) {
            gridLayer.append('text')
                .attr('x', -12).attr('y', r * cellH + cellH / 2)
                .attr('text-anchor', 'middle').attr('dominant-baseline', 'central')
                .attr('fill', 'rgba(255,255,255,0.25)')
                .attr('font-size', '10px').text(r + 1);
        }
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
        dotNetRef, zoom
    };
}

export function renderTerritoryLayer(yearData) {
    if (!_state) return;
    const { territoryLayer, labelLayer, tooltip, cellW, cellH, factionColors, factionWikiUrls, dotNetRef } = _state;

    territoryLayer.selectAll('*').remove();
    labelLayer.selectAll('*').remove();

    if (!yearData || !yearData.regions) return;

    // Build a map of region name -> cells from the overview data
    const regionCellMap = {};
    if (_state.regionCells) {
        for (const region of _state.regionCells) {
            regionCellMap[region.name] = region.cells;
        }
    }

    for (const regionControl of yearData.regions) {
        const cells = regionCellMap[regionControl.region];
        if (!cells || cells.length === 0) continue;

        // Use the dominant faction (first in list, sorted by control desc)
        const factions = regionControl.factions;
        if (!factions || factions.length === 0) continue;

        const dominant = factions[0];
        const color = dominant.color || factionColors[dominant.faction] || '#888888';
        const opacity = 0.08 + dominant.control * 0.35;
        const strokeOpacity = 0.15 + dominant.control * 0.4;
        const isContested = dominant.contested || factions.length > 1;

        for (const [col, row] of cells) {
            const x = col * cellW, y = row * cellH;

            if (isContested && factions.length > 1) {
                // Split cell diagonally for contested regions
                const f1 = factions[0], f2 = factions[1];
                const c1 = f1.color || factionColors[f1.faction] || '#888';
                const c2 = f2.color || factionColors[f2.faction] || '#888';

                // Top-left triangle (dominant faction)
                territoryLayer.append('polygon')
                    .attr('points', `${x},${y} ${x + cellW},${y} ${x},${y + cellH}`)
                    .attr('fill', c1).attr('fill-opacity', 0.08 + f1.control * 0.35)
                    .attr('stroke', c1).attr('stroke-opacity', 0.15 + f1.control * 0.4)
                    .attr('stroke-width', 0.5);

                // Bottom-right triangle (secondary faction)
                territoryLayer.append('polygon')
                    .attr('points', `${x + cellW},${y} ${x + cellW},${y + cellH} ${x},${y + cellH}`)
                    .attr('fill', c2).attr('fill-opacity', 0.08 + f2.control * 0.35)
                    .attr('stroke', c2).attr('stroke-opacity', 0.15 + f2.control * 0.4)
                    .attr('stroke-width', 0.5);
            } else {
                // Single faction fill
                territoryLayer.append('rect')
                    .attr('x', x).attr('y', y)
                    .attr('width', cellW).attr('height', cellH)
                    .attr('fill', color).attr('fill-opacity', opacity)
                    .attr('stroke', color).attr('stroke-opacity', strokeOpacity)
                    .attr('stroke-width', 0.8);
            }

            // Invisible click target
            territoryLayer.append('rect')
                .attr('x', x).attr('y', y)
                .attr('width', cellW).attr('height', cellH)
                .attr('fill', 'transparent')
                .attr('cursor', 'pointer')
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

        // Region faction label at centroid
        const cx = cells.reduce((s, c) => s + c[0], 0) / cells.length * cellW + cellW / 2;
        const cy = cells.reduce((s, c) => s + c[1], 0) / cells.length * cellH + cellH / 2;

        if (dominant.control >= 0.3) {
            labelLayer.append('text')
                .attr('x', cx).attr('y', cy + 16)
                .attr('text-anchor', 'middle').attr('dominant-baseline', 'central')
                .attr('fill', color).attr('fill-opacity', 0.6)
                .attr('font-size', '11px').attr('font-weight', '500')
                .text(dominant.faction);
        }
    }
}

export function clearTerritoryLayer() {
    if (!_state) return;
    _state.territoryLayer.selectAll('*').remove();
    _state.labelLayer.selectAll('*').remove();
}

function getRegionColor(name, regions) {
    if (!regionColorMap[name]) {
        const idx = regions.findIndex(r => r.name === name);
        regionColorMap[name] = REGION_PALETTE[idx % REGION_PALETTE.length];
    }
    return regionColorMap[name];
}
