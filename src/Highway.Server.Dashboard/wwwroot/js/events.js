// The protocol view (023 R5): the raw events, kept and labelled for their audience.
//
// It is the truth underneath the message view, and it is what someone debugging
// Highway itself needs. It is simply no longer what an operator is shown first.
import { esc, getJson, setActiveView } from './shared.js';

export async function render(container, options, params) {
    const name = params.get('name') || '';

    const refresh = async () => {
        const data = await getJson('events', `api/events/${encodeURIComponent(name)}?limit=200`);
        const list = data?.events || [];

        container.innerHTML = `
            <h2>${esc(name)} <span class="kind-badge">protocol events</span></h2>
            <p class="muted">
                The transport underneath the messages — claims, acknowledgements and doorbells.
                Useful for debugging Highway itself; <a href="#/entity?name=${encodeURIComponent(name)}">the
                message view</a> is what shows what your code did.
            </p>
            <table class="grid">
                <thead><tr><th>Time</th><th>Type</th><th>Node</th><th>Detail</th></tr></thead>
                <tbody>${list.map((e) => `<tr>
                    <td>${esc(new Date(e.timestamp).toLocaleTimeString())}</td>
                    <td>${esc(e.type)}</td>
                    <td>${esc(e.node || '')}</td>
                    <td>${esc(e.errorCode || '')}</td>
                </tr>`).join('') || '<tr><td colspan="4" class="muted">No events retained.</td></tr>'}</tbody>
            </table>`;
    };

    setActiveView(refresh, options.pollIntervalMs);
}
