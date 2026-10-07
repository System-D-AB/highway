// Entity page (023 T4): MESSAGES, not protocol events.
//
// This page used to show QueueSent / QueueClaimed / QueueAcknowledged -- three rows
// per message, none of them a thing the developer did. They wrote SendAsync and a
// handler ran; that is the unit shown here.
//
// Correlation, outcomes and counts are all computed on the server. Nothing in this
// file groups events or decides what "acknowledged" means.
import { esc, getJson, postJson, bytes, fullness, setActiveView } from './shared.js';

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

// A publish that reached two of three subscribers is neither processed nor failed,
// and the single outcome word has room for only one of those. This says the rest.
function fanout(m) {
    if (!m.subscriberGroups) return '';

    const all = m.deliveredGroups === m.subscriberGroups;
    const cls = all ? 'muted' : 'state-stale';

    return `<span class="${cls}" title="Subscriber groups that finished this message">`
        + `${m.deliveredGroups}/${m.subscriberGroups} groups</span>`;
}

function messageRow(entity, m) {
    const href = `#/message?entity=${encodeURIComponent(entity)}&id=${encodeURIComponent(m.id)}`;
    const cls = OUTCOME_CLASS[m.outcome] || 'muted';

    return `<tr>
        <td><a href="${href}">${esc(m.id)}</a></td>
        <td>${endpoint(m.startedAt, m.startedOnNode)}</td>
        <td>${endpoint(m.completedAt, m.completedOnNode)}</td>
        <td>
            <span class="${cls}">${esc(m.outcome)}</span>
            ${fanout(m)}
            ${m.failureDetail ? ` <span class="muted">${esc(m.failureDetail)}</span>` : ''}
        </td>
        <td>${esc(ms(m.durationMs))}</td>
    </tr>`;
}

function counts(d) {
    // The window travels with the numbers: "1,204 processed" reads as a lifetime
    // total, and this recorder is volatile and bounded.
    const window = d.windowStart
        ? `<span class="muted">since ${esc(clock(d.windowStart))}</span>`
        : '<span class="muted">no events retained</span>';

    const cell = (label, n, cls) => n
        ? `<div class="stat">
             <div class="stat-label">${label}</div>
             <div class="stat-value ${cls}">${n}</div>
           </div>`
        : '';

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


// Dead-letter bulk actions (073): the table pages, the broker acts on at most BULK_MAX per command.
const DEAD_PAGE = 50;
const BULK_MAX = 500;
const bulkCount = (btn) => Math.min(Number(btn.getAttribute('data-total')) || 0, BULK_MAX);
const bulkPrompt = (verb, count, btn, name) => {
    const total = Number(btn.getAttribute('data-total')) || 0;
    return total > count
        ? `${verb} the oldest ${count} of ${total} dead letters for ${name}? Run it again for the rest.`
        : `${verb} ${count} dead letter${count === 1 ? '' : 's'} for ${name}?`;
};

export async function render(container, options, params) {
    const name = params.get('name') || '';
    const kind = params.get('kind') || 'Unknown';

    const refresh = async () => {
        const [catalogue, data, deadData] = await Promise.all([
            getJson('catalogue', 'api/catalogue'),
            getJson('messages', `api/messages/${encodeURIComponent(name)}`),
            getJson('deadletters', `api/deadletters?kind=${encodeURIComponent(kind)}&name=${encodeURIComponent(name)}&count=${DEAD_PAGE}`),
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
                    <td>${g.deadLettered ? `<span class="state-absent">${g.deadLettered} dead</span>` : ''}${g.draining ? ' <span class="state-absent" title="Its last member stopped subscribing: it receives nothing new and keeps its backlog until the retirement threshold">draining</span>' : ''}</td>
                </tr>`).join('') + '</tbody></table>';
        }

        // Dead Letters section (feature 073)
        html += '<h3>Dead Letters</h3>';
        if (deadData?.readOnly && deadData.readOnlyReason) {
            html += `<p class="muted" style="margin-bottom: 0.5rem; color: #e6a23c;">${esc(deadData.readOnlyReason)}</p>`;
        }

        const deadItems = deadData?.items || [];
        // The table shows the oldest DEAD_PAGE; the bulk actions act on the real total, one broker batch
        // (BULK_MAX) at a time — and say so, so a button never promises more or less than it does (073 fix).
        const deadTotal = Math.max(entry?.deadLettered ?? 0, deadItems.length);
        if (deadItems.length === 0) {
            html += '<p class="muted">No dead letters currently stored.</p>';
        } else {
            if (!deadData?.readOnly) {
                html += `<div style="margin-bottom: 0.75rem; display: flex; gap: 0.5rem;">
                    <button type="button" class="btn btn-sm btn-requeue-all" data-kind="${esc(kind)}" data-name="${esc(name)}" data-total="${deadTotal}">Requeue All (${deadTotal})</button>
                    <button type="button" class="btn btn-sm btn-purge-all" data-kind="${esc(kind)}" data-name="${esc(name)}" data-total="${deadTotal}">Purge All (${deadTotal})</button>
                </div>`;
            }
            if (deadTotal > deadItems.length) {
                html += `<p class="muted">Showing the oldest ${deadItems.length} of ${deadTotal}.</p>`;
            }

            html += `<table class="grid dead-letters-table">
                <thead><tr>
                    <th>Dead ID</th>
                    <th>Message / Request ID</th>
                    <th>Dead-Lettered</th>
                    <th>Attempts</th>
                    <th>Requeues</th>
                    <th>Failure</th>
                    <th>Payload Preview</th>
                    ${deadData?.readOnly ? '' : '<th>Actions</th>'}
                </tr></thead>
                <tbody>` + deadItems.map((dl) => {
                    const idDisplay = dl.messageId ? esc(dl.messageId) : dl.requestId ? esc(dl.requestId) : '—';
                    let payloadPreview = '—';
                    if (dl.payloadState === 'captured') {
                        payloadPreview = dl.payload ? `<code title="${esc(dl.payload)}">${esc(dl.payload.length > 40 ? dl.payload.substring(0, 40) + '…' : dl.payload)}</code>` : '<span class="muted">empty</span>';
                    } else if (dl.payloadState === 'headers-only') {
                        payloadPreview = '<span class="muted">[headers only]</span>';
                    } else if (dl.payloadState === 'not-captured') {
                        payloadPreview = '<span class="muted">[not captured]</span>';
                    } else {
                        payloadPreview = '<span class="muted">[disabled]</span>';
                    }

                    const failureDisplay = dl.failureType
                        ? `<span class="state-absent" title="${esc(dl.failureDetail || '')}">${esc(dl.failureType)}</span>`
                        : `<span class="muted">${esc(dl.reason || '—')}</span>`;

                    let actions = '';
                    if (!deadData?.readOnly) {
                        actions = `<td>
                            <button type="button" class="btn btn-xs btn-requeue" data-id="${esc(dl.deadId)}" data-kind="${esc(kind)}" data-name="${esc(name)}">Requeue</button>
                            <button type="button" class="btn btn-xs btn-requeue-after" data-id="${esc(dl.deadId)}" data-kind="${esc(kind)}" data-name="${esc(name)}">Requeue After…</button>
                            <button type="button" class="btn btn-xs btn-purge" data-id="${esc(dl.deadId)}" data-kind="${esc(kind)}" data-name="${esc(name)}">Purge</button>
                        </td>`;
                    }

                    return `<tr>
                        <td><code>${esc(dl.deadId)}</code></td>
                        <td>${idDisplay}</td>
                        <td>${endpoint(dl.deadLetteredAt)}</td>
                        <td>${dl.attempts}</td>
                        <td>${dl.requeues}</td>
                        <td>${failureDisplay}</td>
                        <td>${payloadPreview}</td>
                        ${actions}
                    </tr>`;
                }).join('') + '</tbody></table>';
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

        // Wire event listeners for dead letter actions
        container.querySelectorAll('.btn-requeue').forEach((btn) => {
            btn.onclick = async () => {
                const id = btn.getAttribute('data-id');
                const k = btn.getAttribute('data-kind');
                const n = btn.getAttribute('data-name');
                btn.disabled = true;
                await postJson('deadletters', 'api/deadletters/requeue', { kind: k, name: n, deadLetterId: id });
                await refresh();
            };
        });

        container.querySelectorAll('.btn-requeue-after').forEach((btn) => {
            btn.onclick = async () => {
                const id = btn.getAttribute('data-id');
                const k = btn.getAttribute('data-kind');
                const n = btn.getAttribute('data-name');
                const secondsStr = prompt('Delay before requeue (seconds):', '10');
                if (secondsStr === null) return;
                const sec = parseFloat(secondsStr);
                if (isNaN(sec) || sec <= 0) {
                    alert('Invalid delay.');
                    return;
                }
                btn.disabled = true;
                await postJson('deadletters', 'api/deadletters/requeue', { kind: k, name: n, deadLetterId: id, afterMs: Math.round(sec * 1000) });
                await refresh();
            };
        });

        container.querySelectorAll('.btn-purge').forEach((btn) => {
            btn.onclick = async () => {
                const id = btn.getAttribute('data-id');
                const k = btn.getAttribute('data-kind');
                const n = btn.getAttribute('data-name');
                if (!confirm(`Purge dead letter ${id}?`)) return;
                btn.disabled = true;
                await postJson('deadletters', 'api/deadletters/purge', { kind: k, name: n, deadLetterId: id });
                await refresh();
            };
        });

        container.querySelectorAll('.btn-requeue-all').forEach((btn) => {
            btn.onclick = async () => {
                const k = btn.getAttribute('data-kind');
                const n = btn.getAttribute('data-name');
                const count = bulkCount(btn);
                if (!confirm(bulkPrompt('Requeue', count, btn, n))) return;
                btn.disabled = true;
                await postJson('deadletters', 'api/deadletters/requeue', { kind: k, name: n, count });
                await refresh();
            };
        });

        container.querySelectorAll('.btn-purge-all').forEach((btn) => {
            btn.onclick = async () => {
                const k = btn.getAttribute('data-kind');
                const n = btn.getAttribute('data-name');
                const count = bulkCount(btn);
                if (!confirm(bulkPrompt('Purge', count, btn, n))) return;
                btn.disabled = true;
                await postJson('deadletters', 'api/deadletters/purge', { kind: k, name: n, count });
                await refresh();
            };
        });
    };

    setActiveView(refresh, options.pollIntervalMs);
}

