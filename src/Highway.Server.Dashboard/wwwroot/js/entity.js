// Entity page (022 T6): one service, queue or channel — its state, its groups,
// and its events, together.
//
// This absorbs 020's four flat views. The same information belongs beside the
// entity it describes rather than in a separate table an operator has to join by
// hand.
import { esc, getJson, bytes, fullness, since, setActiveView } from './shared.js';

function eventRow(e) {
    // Styled on the event, not on "does it have an errorCode". Until 020 T5 lands
    // a severity field, a failure is inferred from the code alone — noted here so
    // it is fixed in one place when it does.
    const failed = e.errorCode ? ' event-failed' : '';
    return `<tr class="${failed}">
        <td>${esc(new Date(e.timestamp).toLocaleTimeString())}</td>
        <td>${esc(e.type)}</td>
        <td>${esc(e.node || '')}</td>
        <td>${esc(e.errorCode || '')}</td>
    </tr>`;
}

function stateBlock(entry) {
    if (!entry) return '';

    const rows = [
        entry.hosts.length ? ['Hosted by', esc(entry.hosts.join(', '))] : null,
        entry.depth !== null && entry.depth !== undefined ? ['Depth', entry.depth] : null,
        entry.bytes !== null && entry.bytes !== undefined
            ? ['Size', `${esc(bytes(entry.bytes))} — ${esc(fullness(entry.bytes, entry.maxBytes))}`] : null,
        entry.deadLettered ? ['Dead letters', entry.deadLettered] : null,
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
        const [catalogue, events] = await Promise.all([
            getJson('catalogue', 'api/catalogue'),
            getJson('events', `api/events/${encodeURIComponent(name)}?limit=100`),
        ]);

        const entries = catalogue?.entries || [];
        const entry = entries.find((e) => e.name === name);
        const groups = entries.filter((e) => e.parentChannel === name);

        let html = `<h2>${esc(name)} <span class="kind-badge">${esc(kind)}</span></h2>`;
        html += stateBlock(entry);

        // A channel's page includes its groups, because a subscriber's backlog is
        // the channel's problem from the operator's point of view (022 R4.2).
        if (groups.length) {
            html += '<h3>Subscriber groups</h3><table class="grid"><tbody>' + groups.map((g) => `
                <tr>
                    <td><a href="#/entity?kind=Group&name=${encodeURIComponent(g.name)}">${esc(g.name)}</a></td>
                    <td>${g.depth ?? 0} queued</td>
                    <td>${g.deadLettered ? `<span class="state-absent">${g.deadLettered} dead</span>` : ''}</td>
                </tr>`).join('') + '</tbody></table>';
        }

        html += '<h3>Events</h3>';
        const list = events?.events || [];

        if (list.length === 0) {
            // Said explicitly: an empty table reads like a loading failure (R4.4).
            html += '<p class="muted">No events recorded for this entity.</p>';
        } else {
            html += `<table class="grid">
                <thead><tr><th>Time</th><th>Type</th><th>Node</th><th>Detail</th></tr></thead>
                <tbody>${list.map(eventRow).join('')}</tbody></table>`;
        }

        container.innerHTML = html;
    };

    setActiveView(refresh, options.pollIntervalMs);
}
