// One message, its whole life (023 T5).
//
// Public steps lead; the broker's own mechanics are one click away. Both are here --
// the point is which one an operator meets first, not hiding the other.
import { esc, getJson, setActiveView } from './shared.js';

const ms = (n) => (n === null || n === undefined) ? ''
    : n < 1000 ? `+${Math.round(n)}ms` : `+${(n / 1000).toFixed(1)}s`;

// The server decides visibility; this file only renders it. A rule here that read
// the event NAME would be a second implementation of the taxonomy.
const isPublic = (s) => s.visibility === 'Public';

function step(s) {
    const cls = isPublic(s) ? '' : ' class="child"';
    return `<tr${cls}>
        <td>${esc(new Date(s.at).toLocaleTimeString())}</td>
        <td>${esc(s.type)}</td>
        <td>${esc(s.node || '')}</td>
        <td class="muted">${esc(ms(s.sincePreviousMs))}</td>
        <td>${esc(s.detail || '')}</td>
    </tr>`;
}

function body(d) {
    switch (d.payloadState) {
        case 'captured':
            try {
                const text = new TextDecoder().decode(
                    Uint8Array.from(atob(d.payload), (c) => c.charCodeAt(0)));
                // Pretty-printed when it parses; shown raw when it does not, because a
                // body that will not parse is itself worth seeing.
                let shown = text;
                try { shown = JSON.stringify(JSON.parse(text), null, 2); } catch { /* raw */ }
                return `<h3>Message</h3><pre class="body">${esc(shown)}</pre>`;
            } catch {
                return '<h3>Message</h3><p class="muted">The body could not be decoded.</p>';
            }
        case 'headers-only':
            // Not an exemption from feature 002's capture modes, and it says which one.
            return '<h3>Message</h3><p class="unavailable">Withheld: this name is configured HeadersOnly.</p>';
        case 'disabled':
            return '<h3>Message</h3><p class="unavailable">Withheld: recording is disabled for this name.</p>';
        default:
            return '<h3>Message</h3><p class="muted">No body was captured for this message.</p>';
    }
}

export async function render(container, options, params) {
    const entity = params.get('entity') || '';
    const id = params.get('id') || '';

    const refresh = async () => {
        const d = await getJson('message', `api/message/${encodeURIComponent(entity)}/${encodeURIComponent(id)}`);
        if (!d) {
            container.innerHTML = '<p class="unavailable">This message is no longer retained.</p>';
            return;
        }

        const steps = d.steps || [];
        const publicSteps = steps.filter(isPublic);
        const internalCount = steps.length - publicSteps.length;

        const showAll = params.get('all') === '1';
        const shown = showAll ? steps : publicSteps;

        const toggle = internalCount === 0 ? '' : showAll
            ? `<a href="#/message?entity=${encodeURIComponent(entity)}&id=${encodeURIComponent(id)}">Hide broker steps</a>`
            : `<a href="#/message?entity=${encodeURIComponent(entity)}&id=${encodeURIComponent(id)}&all=1">
                 Show ${internalCount} broker step${internalCount === 1 ? '' : 's'}</a>`;

        container.innerHTML = `
            <h2>${esc(id)} <span class="kind-badge">${esc(d.outcome)}</span></h2>
            <p><a href="#/entity?name=${encodeURIComponent(entity)}">← ${esc(entity)}</a></p>
            <h3>Timeline</h3>
            <table class="grid">
                <thead><tr><th>Time</th><th>Step</th><th>Node</th><th>Since</th><th>Detail</th></tr></thead>
                <tbody>${shown.map(step).join('')}</tbody>
            </table>
            <p>${toggle}</p>
            ${body(d)}`;
    };

    setActiveView(refresh, options.pollIntervalMs);
}
