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
    return people.map((p) => ({
        id: String(p.id),
        data: {
            gender: p?.data?.gender === 'F' ? 'F' : 'M',
            'first name': p?.data?.['first name'] ?? '',
            'last name': p?.data?.['last name'] ?? '',
            // Free-form extras family-chart will ignore unless we display them.
            // pageId is the integer used by the click handler.
            pageId: p?.data?.pageId ?? null,
            wikiUrl: p?.data?.wikiUrl ?? null,
            imageUrl: p?.data?.imageUrl ?? null,
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
            card.setCardDisplay([
                ['first name', 'last name'],
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
