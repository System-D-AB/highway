// Entity page (023 T4): MESSAGES, not protocol events.
//
// This page used to show QueueSent / QueueClaimed / QueueAcknowledged -- three rows
// per message, none of them a thing the developer did. They wrote SendAsync and a
// handler ran; that is the unit shown here.
//
// Correlation, outcomes and counts are all computed on the server. Nothing in this
// file groups events or decides what "acknowledged" means.
import { esc, getJson, bytes, fullness, setActiveView } from './shared.js';

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

function endpoint(at, node) {
    if (!at) return '<span class="muted">—</span>';
    return `${esc(clock(at))}${node ? ` <span class="muted">${esc(node)}</span>` : ''}`;
}

function messageRow(entity, m) {
    const href = `#/message?entity=${encodeURIComponent(entity)}&id=${encodeURIComponent(m.id)}`;
    const cls = OUTCOME_CLASS[m.outcome] || 'muted';

    return `<tr>
        <td><a href="${href}">${esc(m.id)}</a></td>
        <td>${endpoint(m.startedAt, m.startedOnNode)}</td>
        <td>${endpoint(m.completedAt, m.completedOnNode)}</td>
        <td><span class="${cls}">${esc(m.outcome)}</span>${m.failureDetail ? ` <span class="muted">${esc(m.failureDetail)}</span>` : ''}</td>
        <td>${esc(ms(m.durationMs))}</td>
    </tr>`;
}

function counts(d) {
    // The window travels with the numbers: "1,204 processed" reads as a lifetime
    // total, and this recorder is volatile and bounded.
    const window = d.windowStart
        ? `<span class="muted">since ${esc(clock(d.windowStart))}</span>`
        : '<span class="muted">no events retained</span>';

    const cell = (label, n, cls) =>
        n ? `<div class="stat"><span>${label}</span><b class="${cls}">${n}</b></div>` : '';

    return `<div class="stat-grid">
        ${cell('PROCESSED', d.processed, 'state-live')}
        ${cell('FAILED', d.failed, 'state-absent')}
        ${cell('DEAD-LETTERED', d.deadLettered, 'state-absent')}
        ${cell('REFUSED', d.refused, 'state-absent')}
        ${cell('IN FLIGHT', d.inFlight, 'muted')}
    </div><p>${window}</p>`;
}

function stateBlock(entry) {
    if (!entry) return '';
    const rows = [
        entry.hosts.length ? ['Hosted by', esc(entry.hosts.join(', '))] : null,
        entry.depth !== null && entry.depth !== undefined ? ['Depth', entry.depth] : null,
        entry.bytes !== null && entry.bytes !== undefined
            ? ['Size', `${esc(bytes(entry.bytes))} — ${esc(fullness(entry.bytes, entry.maxBytes))}`] : null,
    ].filter(Boolean);

    if (entry.state === 'NeverDeclared')
        rows.unshift(['State', '<span class="state-absent">addressed but never declared — no node hosts this</span>']);
    else if (entry.state === 'HostStale')
        rows.unshift(['State', '<span class="state-stale">every host has gone quiet</span>']);

    return `<dl class="detail">${rows.map(([k, v]) => `<dt>${esc(k)}</dt><dd>${v}</dd>`).join('')}</dl>`;
}

export async function render(container, options, params) {
    const name = params.get('name') || '';
    const kind = params.get('kind') || 'Unknown';

    const refresh = async () => {
        const [catalogue, data] = await Promise.all([
            getJson('catalogue', 'api/catalogue'),
            getJson('messages', `api/messages/${encodeURIComponent(name)}`),
        ]);

        const entries = catalogue?.entries || [];
        const entry = entries.find((e) => e.name === name);
        const groups = entries.filter((e) => e.parentChannel === name);

        let html = `<h2>${esc(name)} <span class="kind-badge">${esc(kind)}</span></h2>`;
        html += stateBlock(entry);

        if (data) html += counts(data);

        if (groups.length) {
            html += '<h3>Subscriber groups</h3><table class="grid"><tbody>' + groups.map((g) => `
                <tr>
                    <td><a href="#/entity?kind=Group&name=${encodeURIComponent(g.name)}">${esc(g.name)}</a></td>
                    <td>${g.depth ?? 0} queued</td>
                    <td>${g.deadLettered ? `<span class="state-absent">${g.deadLettered} dead</span>` : ''}</td>
                </tr>`).join('') + '</tbody></table>';
        }

        html += '<h3>Messages</h3>';
        const list = data?.messages || [];

        if (list.length === 0) {
            html += '<p class="muted">No messages retained for this entity.</p>';
        } else {
            html += `<table class="grid">
                <thead><tr>
                    <th>Message</th><th>Started</th><th>Completed</th><th>Outcome</th><th>Took</th>
                </tr></thead>
                <tbody>${list.map((m) => messageRow(name, m)).join('')}</tbody>
            </table>`;
        }

        // The raw events remain reachable and are labelled for their audience.
        html += `<p><a href="#/events?name=${encodeURIComponent(name)}">Protocol events →</a>
                 <span class="muted">the transport underneath, for debugging Highway itself</span></p>`;

        container.innerHTML = html;
    };

    setActiveView(refresh, options.pollIntervalMs);
}
