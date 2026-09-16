// Nodes view (022 T4): what is running, and whether it is healthy.
import { esc, getJson, since, setActiveView } from './shared.js';

const KINDS = [
    ['Services', 'services'],
    ['Queues', 'queues'],
    ['Channels', 'channels'],
];

function hostsSummary(node) {
    const parts = KINDS
        .map(([label, key]) => [label, node[key] || []])
        .filter(([, list]) => list.length > 0)
        .map(([label, list]) => `${list.length} ${label.toLowerCase()}`);

    // A node that declared nothing is usually a misconfiguration, and it was
    // invisible before this view existed (022 R2.5).
    return parts.length ? parts.join(' · ') : '<span class="muted">declares nothing</span>';
}

// Labelled as an observation, never as an address to dial (023 T8). Behind NAT, in a
// container, or under a load balancer the peer address is true and not reachable, and
// "seen from" is the whole difference between honest and misleading here.
function seenFrom(node) {
    return node.seenFrom
        ? `<span class="mono" title="Peer address of this node's connection, as the broker sees it">${esc(node.seenFrom)}</span>`
        : '<span class="muted" title="Registered, but not connected right now">not connected</span>';
}

function row(node) {
    const state = node.state === 'live'
        ? '<span class="state-live">live</span>'
        : `<span class="state-${esc(node.state)}">${esc(node.state)} ${since(node.sinceSeconds)}</span>`;

    return `<tr>
        <td><a href="#/node?name=${encodeURIComponent(node.name)}">${esc(node.name)}</a></td>
        <td>${state}</td>
        <td>${seenFrom(node)}</td>
        <td>${hostsSummary(node)}</td>
    </tr>`;
}

const HEAD = '<thead><tr><th>Node</th><th>State</th><th>Seen from</th><th>Hosts</th></tr></thead>';

function table(nodes, emptyText) {
    const rows = nodes.map(row).join('');
    return `<table class="grid">${HEAD}
        <tbody>${rows || `<tr><td colspan="4" class="muted">${esc(emptyText)}</td></tr>`}</tbody>
    </table>`;
}

export async function render(container, options, params) {
    // A node the broker has not seen for over an hour is "absent" — it may return, so it is
    // kept, but it does not belong in the list of what is running now (046). Absent nodes move
    // behind a toggle; the count is itself information.
    const showAbsent = params && params.get('absent') === '1';

    const refresh = async () => {
        const data = await getJson('nodes', 'api/nodes');
        if (!data) return;

        if (data.unavailable) {
            container.innerHTML = `<p class="unavailable">Nodes unavailable: ${esc(data.unavailable)}</p>`;
            return;
        }

        const current = data.nodes.filter((n) => n.state !== 'absent');
        const absent = data.nodes.filter((n) => n.state === 'absent');

        const toggle = absent.length === 0 ? '' : showAbsent
            ? '<a href="#/nodes">Hide absent</a>'
            : `<a href="#/nodes?absent=1">Show absent (${absent.length})</a>`;

        container.innerHTML = `
            <h2>Nodes</h2>
            ${table(current, 'No live or stale nodes.')}
            <p>${toggle}</p>
            ${showAbsent && absent.length ? `<h3 class="muted">Absent</h3>${table(absent, '')}` : ''}`;
    };

    setActiveView(refresh, options.pollIntervalMs);
}
