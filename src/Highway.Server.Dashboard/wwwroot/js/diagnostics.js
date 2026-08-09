// Diagnostics (022 T7): the recorder's own index, and the internal names.
//
// This is the old main page. It is genuinely useful WHEN DEBUGGING THE RECORDER,
// and actively misleading as an operator's home page — six kinds of thing in one
// column called "Name". So it moves here rather than disappearing.
import { esc, getJson, bytes, setActiveView } from './shared.js';

export async function render(container, options) {
    const refresh = async () => {
        const data = await getJson('diagnostics', 'api/recorder');
        if (!data) return;

        const rows = (data.names || []).map((n) => `
            <tr>
                <td><a href="#/entity?kind=Unknown&name=${encodeURIComponent(n.name)}">${esc(n.name)}</a></td>
                <td>${n.events}</td>
                <td>${esc(bytes(n.bytes))}</td>
                <td>${esc(n.capture)}</td>
                <td>${n.dropped}</td>
            </tr>`).join('');

        container.innerHTML = `
            <h2>Recorder Diagnostics</h2>
            <p class="muted">
                The flight recorder's own name index. It mixes nodes, services, queues, channels,
                groups and broker-internal buckets — useful for debugging the recorder, which is
                why it lives here and not on the front page.
            </p>
            <div class="stat-grid">
                <div class="stat"><span>EVENTS</span><b>${data.totalEvents}</b></div>
                <div class="stat"><span>BYTES</span><b>${esc(bytes(data.totalBytes))}</b></div>
                <div class="stat"><span>NAMES</span><b>${(data.names || []).length}</b></div>
                <div class="stat"><span>DROPPED (CAPACITY)</span><b>${data.droppedCapacity}</b></div>
                <div class="stat"><span>DROPPED (BUDGET)</span><b>${data.droppedBudget}</b></div>
                <div class="stat"><span>FAILURES</span><b>${data.failures}</b></div>
            </div>
            <table class="grid">
                <thead><tr><th>Name</th><th>Events</th><th>Bytes</th><th>Capture</th><th>Dropped</th></tr></thead>
                <tbody>${rows}</tbody>
            </table>`;
    };

    setActiveView(refresh, options.pollIntervalMs);
}
