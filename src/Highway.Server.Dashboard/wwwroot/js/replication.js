// Replication (042 T4): role, epoch, slots, lag — the same fields HW.STATS / HW.REPL.STATUS serve.
import { esc, getJson, setActiveView } from './shared.js';

export async function render(container, options) {
    const refresh = async () => {
        const data = await getJson('replication', 'api/replication');
        if (!data) return;

        if (data.unavailable) {
            container.innerHTML = `
                <h2>Replication</h2>
                <p class="muted">${esc(data.unavailable)}</p>`;
            return;
        }

        const fields = data.fields || [];
        const byName = Object.fromEntries(fields.map((f) => [f.name, f.value]));
        const slots = [];
        for (let i = 0; ; i++) {
            const id = byName[`repl.slot.${i}.id`];
            if (!id) break;
            slots.push({
                id,
                acked: byName[`repl.slot.${i}.acked`],
                state: byName[`repl.slot.${i}.state`],
                lag: byName[`repl.slot.${i}.lag`],
            });
        }

        const rows = slots.map((s) => `
            <tr>
                <td>${esc(s.id)}</td>
                <td>${esc(s.state)}</td>
                <td>${esc(s.acked)}</td>
                <td>${esc(s.lag)}</td>
            </tr>`).join('');

        container.innerHTML = `
            <h2>Replication</h2>
            <p class="muted">
                One primary, warm standbys. Replicas serve no client traffic. Lag is WAL sequences
                behind the primary; a slot past the cap is dropped and must re-bootstrap.
            </p>
            <div class="stat-grid">
                <div class="stat"><span>ROLE</span><b>${esc(byName['repl.role'] || '—')}</b></div>
                <div class="stat"><span>EPOCH</span><b>${esc(byName['repl.epoch'] || '—')}</b></div>
                <div class="stat"><span>FENCED</span><b>${esc(byName['repl.fenced'] || '—')}</b></div>
                <div class="stat"><span>SLOTS</span><b>${esc(byName['repl.slots'] || '0')}</b></div>
                <div class="stat"><span>MIN ACKED</span><b>${esc(byName['repl.minAcked'] || '—')}</b></div>
                <div class="stat"><span>DROPS</span><b>${esc(byName['repl.drops'] || '0')}</b></div>
            </div>
            <p class="muted">endpoint ${esc(byName['repl.endpoint'] || '')}
                ${byName['repl.lastPromotionReason'] ? ' — last promote: ' + esc(byName['repl.lastPromotionReason']) : ''}
                ${byName['repl.reconciliation'] ? ' — reconciliation ' + esc(byName['repl.reconciliation']) : ''}
            </p>
            <table class="grid">
                <thead><tr><th>Replica</th><th>State</th><th>Acked seq</th><th>Lag</th></tr></thead>
                <tbody>${rows || '<tr><td colspan="4" class="muted">No replica slots</td></tr>'}</tbody>
            </table>`;
    };

    setActiveView(refresh, options.pollIntervalMs);
}
