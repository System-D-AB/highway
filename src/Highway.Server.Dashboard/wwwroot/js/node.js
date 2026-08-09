// Node page (023 T6): one node, and what it has actually done.
//
// The nodes list has linked here since 022, but the route did not exist -- the link
// fell through the router's default and landed on the catalogue. It works now.
//
// Two lists, deliberately side by side. DECLARED is what the node said it hosts;
// PROCESSED is what came through it. A node declaring a service it has never served
// is a misconfiguration, and neither list shows that on its own.
import { esc, getJson, since, setActiveView } from './shared.js';

const OUTCOME_CLASS = {
    Processed: 'state-live',
    Failed: 'state-absent',
    DeadLettered: 'state-absent',
    Refused: 'state-absent',
    InFlight: 'muted',
    Incomplete: 'state-stale',
};

const ms = (n) => (n === null || n === undefined) ? '—'
    : n < 1000 ? `${Math.round(n)}ms` : `${(n / 1000).toFixed(1)}s`;

const clock = (t) => t ? new Date(t).toLocaleTimeString() : '—';

function declared(label, list) {
    if (!list || list.length === 0) return '';

    const items = list.map((n) =>
        `<li><a href="#/entity?name=${encodeURIComponent(n)}">${esc(n)}</a></li>`).join('');

    return `<div class="declared-group"><h4>${esc(label)}</h4><ul>${items}</ul></div>`;
}

function row(m) {
    const href = `#/message?entity=${encodeURIComponent(m.entity)}&id=${encodeURIComponent(m.id)}`;
    const cls = OUTCOME_CLASS[m.outcome] || 'muted';

    return `<tr>
        <td><a href="#/entity?name=${encodeURIComponent(m.entity)}">${esc(m.entity)}</a></td>
        <td><a href="${href}">${esc(m.id)}</a></td>
        <td><span class="${cls}">${esc(m.outcome)}</span>${m.failureDetail ? ` <span class="muted">${esc(m.failureDetail)}</span>` : ''}</td>
        <td>${esc(clock(m.completedAt))}</td>
        <td>${esc(ms(m.durationMs))}</td>
    </tr>`;
}

export async function render(container, options, params) {
    const name = params.get('name');
    if (!name) {
        container.innerHTML = '<p class="unavailable">No node named in the link.</p>';
        return;
    }

    const refresh = async () => {
        const d = await getJson('node', `api/node/${encodeURIComponent(name)}`);
        if (!d) return;

        const state = d.state === 'live'
            ? '<span class="state-live">live</span>'
            : `<span class="state-${esc(d.state)}">${esc(d.state)} ${since(d.sinceSeconds)}</span>`;

        // Labelled as an observation. See nodes.js.
        const seen = d.seenFrom
            ? `<span class="mono">${esc(d.seenFrom)}</span>`
            : '<span class="muted">not connected</span>';

        const groups = declared('Services', d.services)
            + declared('Queues', d.queues)
            + declared('Channels', d.channels);

        const rows = d.messages.map(row).join('');

        container.innerHTML = `
            <h2>${esc(d.name)}</h2>
            ${d.unavailable ? `<p class="unavailable">Registry: ${esc(d.unavailable)}</p>` : ''}

            <p class="summary">
                ${state} · seen from ${seen} ·
                <span class="state-live">${d.processed} processed</span>
                ${d.failed > 0 ? ` · <span class="state-absent">${d.failed} failed</span>` : ''}
            </p>

            <h3>Declares</h3>
            <div class="declared">${groups || '<p class="muted">Declares nothing.</p>'}</div>

            <h3>Completed here</h3>
            <p class="muted">Messages this node finished, newest first, within the flight
               recorder's retained window. A message's origin node is not recorded — see the
               entity page.</p>
            <table class="grid">
                <thead><tr><th>Entity</th><th>Message</th><th>Outcome</th><th>Completed</th><th>Took</th></tr></thead>
                <tbody>${rows || '<tr><td colspan="5" class="muted">Nothing retained for this node.</td></tr>'}</tbody>
            </table>`;
    };

    setActiveView(refresh, options.pollIntervalMs);
}
