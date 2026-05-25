// family-tree.js — Blazor interop wrapper around family-chart-premium.
//
// The premium UMD bundle (loaded via <script> tag in App.razor) registers a
// global `window.f3` with `createChart`, `SpouseLinkTextPlugin`,
// `KinshipPlugin`, etc. We render read-only here — no .editTree() call per
// the spec's non-goal.
//
// API used (verified against dist/types/index.d.ts in family-chart-premium@0.0.0-beta.2):
//   f3.createChart(cont, data) -> Chart
//   Chart.setCardHtml() -> CardHtml
//   CardHtml.setCardDisplay(rows) -> CardHtml
//   CardHtml.setCardImageField(name) -> CardHtml
//   CardHtml.setOnCardClick(fn) -> CardHtml
//   Chart.use(plugin) -> Chart  (for SpouseLinkTextPlugin / KinshipPlugin)
//   Chart.updateTree({ initial: true }) -> Chart
//   Chart.updateData(data) -> Chart

const _state = new Map(); // containerId -> { chart, container }

function _safeData(payload) {
    // payload is the FamilyTreeResponse wire shape — people[] is the array
    // family-chart wants. Defensive copy + ensure required keys exist.
    const people = Array.isArray(payload?.people) ? payload.people : [];
    // Strip the Wookieepedia "/Legends" article slug from displayed names.
    // The kg.nodes.name for Legends-variant articles is suffixed (e.g.
    // "Anakin Skywalker/Legends"), which the SplitName helper carried into
    // the last-name field — so cards previously read "Anakin Skywalker/Legends".
    // The dedicated Canon/Legends chip below now conveys that signal cleanly;
    // the article slug is redundant on the card label. PageId / wikiUrl /
    // continuity all still point at the correct Legends entity.
    const stripLegendsSuffix = (s) => (typeof s === 'string' ? s.replace(/\s*\/?\s*Legends\s*$/i, '') : s);
    return people.map((p) => ({
        id: String(p.id),
        data: {
            gender: p?.data?.gender === 'F' ? 'F' : 'M',
            'first name': stripLegendsSuffix(p?.data?.['first name'] ?? ''),
            'last name': stripLegendsSuffix(p?.data?.['last name'] ?? ''),
            // Free-form extras family-chart will ignore unless we display them.
            // pageId is the integer used by the click handler.
            pageId: p?.data?.pageId ?? null,
            wikiUrl: p?.data?.wikiUrl ?? null,
            imageUrl: p?.data?.imageUrl ?? null,
            // continuity drives the per-card Canon/Legends chip styling — see
            // setCardDisplay below + the `.ft-continuity-chip[data-continuity=...]`
            // CSS rules in wwwroot/style.css. Defaults to "Unknown" so the
            // renderer doesn't blow up on stubs / partial data.
            continuity: p?.data?.continuity ?? 'Unknown',
        },
        rels: {
            parents: Array.isArray(p?.rels?.parents) ? p.rels.parents.map(String) : [],
            spouses: Array.isArray(p?.rels?.spouses) ? p.rels.spouses.map(String) : [],
            children: Array.isArray(p?.rels?.children) ? p.rels.children.map(String) : [],
        },
    }));
}

export function renderFamilyTree(containerId, payload, dotNetRef) {
    if (!window.f3 || typeof window.f3.createChart !== 'function') {
        console.error('[family-tree] window.f3 is not loaded — check App.razor <script> reference');
        return;
    }
    const container = document.getElementById(containerId);
    if (!container) {
        console.warn('[family-tree] container not found:', containerId);
        return;
    }

    // Idempotent: if a previous chart exists on this container, tear it down.
    destroy(containerId);

    // Clear DOM in case the framework left anything behind.
    while (container.firstChild) container.removeChild(container.firstChild);

    const people = _safeData(payload);
    if (people.length === 0) {
        const empty = document.createElement('div');
        empty.style.padding = '24px';
        empty.style.opacity = '0.6';
        empty.textContent = 'No family tree data available.';
        container.appendChild(empty);
        return;
    }

    const rootId = String(payload?.rootId ?? people[0].id);

    let chart;
    try {
        chart = window.f3.createChart(container, people);
    } catch (err) {
        console.error('[family-tree] createChart failed:', err);
        const errDiv = document.createElement('div');
        errDiv.style.padding = '24px';
        errDiv.style.color = 'var(--mud-palette-error, #f44336)';
        errDiv.textContent = 'Family tree renderer failed to start: ' + (err && err.message ? err.message : String(err));
        container.appendChild(errDiv);
        return;
    }

    // setCardHtml() returns a CardHtml instance — configure the visible rows and
    // wire the click handler to .NET. Per the d.ts:
    //   setCardDisplay(card_display) takes a 2D array of field names (row × column).
    //   setOnCardClick(fn) takes (e, treeDatum) => void; treeDatum.data is the Datum.
    try {
        const card = chart.setCardHtml();
        if (typeof card.setCardDisplay === 'function') {
            // Row 1: name (joined first + last via the array shorthand).
            // Row 2: a function that returns inline HTML for the continuity chip.
            //
            // family-chart-premium's card_display normaliser (`I(e)` in the
            // bundle) accepts three row types: function, string, and array.
            // Each row is interpolated into `<div class="f3-card-label">...</div>`
            // via a template literal, so a function returning raw HTML is
            // preserved unescaped. We use that to emit a colour-coded chip:
            // Canon  → primary palette (--mud-palette-primary)
            // Legends → secondary palette (--mud-palette-secondary)
            // Other  → muted default
            //
            // The chip element has data-continuity="Canon|Legends|Unknown"
            // so the scoped CSS in wwwroot/style.css can colour-map per
            // Principle VII without per-card JS work.
            const escapeHtml = (s) => String(s ?? 'Unknown')
                .replace(/&/g, '&amp;')
                .replace(/</g, '&lt;')
                .replace(/>/g, '&gt;')
                .replace(/"/g, '&quot;')
                .replace(/'/g, '&#39;');
            card.setCardDisplay([
                ['first name', 'last name'],
                (datum) => {
                    const c = escapeHtml(datum?.data?.continuity);
                    return `<span class="ft-continuity-chip" data-continuity="${c}">${c}</span>`;
                },
            ]);
        }
        if (typeof card.setCardImageField === 'function') {
            card.setCardImageField('imageUrl');
        }
        if (typeof card.setOnCardClick === 'function') {
            card.setOnCardClick((e, td) => {
                try {
                    // td.data is the Datum; td.data.data is the original DatumJson data block.
                    const pageId = td?.data?.data?.pageId ?? null;
                    if (pageId !== null && pageId !== undefined && dotNetRef) {
                        // family-chart's default click handler updates the main person.
                        // We want navigation only — invoke .NET and DON'T call the default.
                        dotNetRef.invokeMethodAsync('OnPersonClicked', Number(pageId));
                    }
                } catch (err) {
                    console.warn('[family-tree] card click handler failed:', err);
                }
            });
        }
    } catch (err) {
        console.warn('[family-tree] card setup partial failure:', err);
    }

    // Opt into premium plugins per spec research.md R-2.
    // Each is a class exported on `window.f3`; the chart's .use() takes an instance.
    // Both are optional — if the bundle ever drops them, log and continue rather than crash.
    try {
        if (typeof window.f3.SpouseLinkTextPlugin === 'function') {
            chart.use(new window.f3.SpouseLinkTextPlugin({
                text: (sp1, sp2) => {
                    // family-chart hands us TreeDatum pairs; default label is "Spouse".
                    // Could be enriched later by stashing the edge label on rel_data
                    // server-side and reading it here.
                    return 'Spouse';
                },
            }));
        } else {
            console.info('[family-tree] SpouseLinkTextPlugin not found on window.f3 — labels will be default.');
        }
    } catch (err) {
        console.warn('[family-tree] SpouseLinkTextPlugin failed to register:', err);
    }

    try {
        if (typeof window.f3.KinshipPlugin === 'function') {
            const kinship = new window.f3.KinshipPlugin();
            if (typeof kinship.setSelfId === 'function') kinship.setSelfId(rootId);
            chart.use(kinship);
        } else {
            console.info('[family-tree] KinshipPlugin not found on window.f3 — secondary kinship view disabled.');
        }
    } catch (err) {
        console.warn('[family-tree] KinshipPlugin failed to register:', err);
    }

    // Anchor the chart on the focal Character BEFORE the initial render.
    // Without this, family-chart defaults to people[0] from the array order —
    // which lands wherever the BFS happened to enumerate first (Breha Organa
    // for "Anakin Skywalker family tree" in starwars-dev, because PageId
    // 451699 < 452390). The kinship plugin's setSelfId only controls
    // relationship-label computation; it does NOT change the chart's main
    // person. updateMainId(rootId) does.
    try {
        if (typeof chart.updateMainId === 'function') {
            chart.updateMainId(rootId);
        } else {
            console.warn('[family-tree] chart.updateMainId not available — chart will use array-order default.');
        }
    } catch (err) {
        console.warn('[family-tree] updateMainId failed:', err);
    }

    // Initial layout/render.
    try {
        if (typeof chart.updateTree === 'function') {
            chart.updateTree({ initial: true });
        }
    } catch (err) {
        console.error('[family-tree] updateTree failed:', err);
    }

    _state.set(containerId, { chart, container });
}

export function destroy(containerId) {
    const entry = _state.get(containerId);
    if (!entry) {
        // Even with no state, clear the DOM in case the chart was rendered before
        // (e.g. hot reload) and we never tracked it.
        const c = document.getElementById(containerId);
        if (c) while (c.firstChild) c.removeChild(c.firstChild);
        return;
    }
    try {
        // The library exposes no explicit destroy on the Chart class — it tears down
        // via DOM removal. Clear the container's children, drop our reference.
        const { container } = entry;
        if (container) {
            while (container.firstChild) container.removeChild(container.firstChild);
        }
    } catch (err) {
        console.warn('[family-tree] destroy failed:', err);
    } finally {
        _state.delete(containerId);
    }
}
