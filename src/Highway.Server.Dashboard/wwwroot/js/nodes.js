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

function row(node) {
    const state = node.state === 'live'
        ? '<span class="state-live">live</span>'
        : `<span class="state-${esc(node.state)}">${esc(node.state)} ${since(node.sinceSeconds)}</span>`;

    return `<tr>
        <td><a href="#/node?name=${encodeURIComponent(node.name)}">${esc(node.name)}</a></td>
        <td>${state}</td>
        <td>${hostsSummary(node)}</td>
    </tr>`;
}

export async function render(container, options) {
    const refresh = async () => {
        const data = await getJson('nodes', 'api/nodes');
        if (!data) return;

        if (data.unavailable) {
            container.innerHTML = `<p class="unavailable">Nodes unavailable: ${esc(data.unavailable)}</p>`;
            return;
        }

        const rows = data.nodes.map(row).join('');
        container.innerHTML = `
            <h2>Nodes</h2>
            <table class="grid">
                <thead><tr><th>Node</th><th>State</th><th>Hosts</th></tr></thead>
                <tbody>${rows || '<tr><td colspan="3" class="muted">No nodes registered.</td></tr>'}</tbody>
            </table>`;
    };

    setActiveView(refresh, options.pollIntervalMs);
}
