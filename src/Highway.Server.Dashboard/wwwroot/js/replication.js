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

        // Leadership + redundancy banner (050 T5): who is primary, at what epoch, since when, and
        // whether redundancy is currently intact — loud on the tab, not inferred from a stat tile.
        const role = byName['repl.role'] || '—';
        const epoch = byName['repl.epoch'] || '—';
        const redundancy = byName['repl.redundancy'] || '';
        const since = byName['repl.leadershipSince'] || '';
        const sinceText = since ? new Date(since).toLocaleString() : '—';
        const primaryShown = (role === 'Replica' || role === 'Demoted')
            ? (byName['repl.redirect'] || '—')
            : (byName['repl.endpoint'] || '—');
        const banner = `<div style="padding:10px 14px;border-left:4px solid var(--accent);background:rgba(127,127,127,.08);border-radius:6px;margin-bottom:12px;">
            Primary is now <b class="mono">${esc(primaryShown)}</b> · epoch ${esc(epoch)} · since ${esc(sinceText)}</div>`;
        const danger = (msg) => `<div style="padding:10px 14px;border-left:4px solid #e5484d;background:rgba(229,72,77,.12);border-radius:6px;margin-bottom:12px;font-weight:600;">⚠ ${esc(msg)}</div>`;
        let alert = '';
        if (redundancy === 'no-standby') alert = danger('No redundancy: this primary has no live standby attached.');
        else if (redundancy === 'demoted') alert = danger('This node is demoted and not yet following a primary.');
        else if (redundancy === 'fenced') alert = danger('This node is fenced (read-only) — no peer or client contact.');

        // Event timeline (050 T5): recent role/topology transitions, newest first.
        const timeline = [];
        for (let i = 0; ; i++) { const ev = byName[`repl.event.${i}`]; if (!ev) break; timeline.push(ev); }
        const timelineHtml = timeline.length
            ? `<h3>Recent role changes</h3>
               <ul class="mono" style="margin:0;padding-left:18px;line-height:1.6;">${timeline.slice().reverse().map((e) => `<li>${esc(e)}</li>`).join('')}</ul>`
            : '';

        // The replicated roster — every member with its priority and endpoint (047).
        const roster = [];
        for (let i = 0; ; i++) {
            const id = byName[`roster.${i}.id`];
            if (!id) break;
            roster.push({ id, priority: byName[`roster.${i}.priority`], endpoint: byName[`roster.${i}.endpoint`] });
        }
        const rosterById = Object.fromEntries(roster.map((m) => [m.id, m]));

        // Attached-replica slots (the primary's view), joined to the roster for endpoint + priority.
        const slots = [];
        for (let i = 0; ; i++) {
            const id = byName[`repl.slot.${i}.id`];
            if (!id) break;
            const m = rosterById[id] || {};
            slots.push({
                id,
                endpoint: m.endpoint || '—',
                priority: m.priority || '—',
                acked: byName[`repl.slot.${i}.acked`],
                state: byName[`repl.slot.${i}.state`],
                lag: byName[`repl.slot.${i}.lag`],
            });
        }
        const rows = slots.map((s) => `
            <tr>
                <td>${esc(s.id)}</td>
                <td class="mono">${esc(s.endpoint)}</td>
                <td class="mono">${esc(s.priority)}</td>
                <td>${esc(s.state)}</td>
                <td>${esc(s.acked)}</td>
                <td>${esc(s.lag)}</td>
            </tr>`).join('');

        const isReplica = (byName['repl.role'] || '') === 'Replica';
        const emptySlots = isReplica
            ? '<tr><td colspan="6" class="muted">This node is a replica — replica slots live on the primary.</td></tr>'
            : '<tr><td colspan="6" class="muted">No replica slots — no standby has attached yet.</td></tr>';

        // Succession order (048/049): who is serving now and who takes over if it goes away. The
        // current primary is this node when its role is Primary, else the primary it redirects to.
        const primaryEndpoint = isReplica ? (byName['repl.redirect'] || '') : (byName['repl.endpoint'] || '');
        const members = roster.map((m) => ({
            ...m,
            prio: parseInt(m.priority, 10) || 0,
            isPrimary: m.endpoint === primaryEndpoint,
        }));
        // Primary first; then eligible standbys by ascending priority; priority 0 (never) last.
        members.sort((a, b) => {
            if (a.isPrimary !== b.isPrimary) return a.isPrimary ? -1 : 1;
            const pa = a.prio === 0 ? Infinity : a.prio;
            const pb = b.prio === 0 ? Infinity : b.prio;
            return pa - pb;
        });
        let pos = 0;
        const successionRows = members.map((m) => {
            let status;
            if (m.prio === 0 && !m.isPrimary) {
                status = '<span class="muted">Never promotes (priority 0)</span>';
            } else {
                pos++;   // 1 = serving, 2 = next in line, …
                if (m.isPrimary) status = '<span style="color: var(--success); font-weight: 600;">Primary — serving now</span>';
                else if (pos === 2) status = '<span style="color: var(--accent); font-weight: 600;">Next if the primary fails</span>';
                else status = `<span class="muted">Standby — #${pos} in line</span>`;
            }
            return `<tr>
                <td class="mono">${esc(String(m.prio))}</td>
                <td>${esc(m.id)}</td>
                <td class="mono">${esc(m.endpoint)}</td>
                <td>${status}</td>
            </tr>`;
        }).join('');

        container.innerHTML = `
            <h2>Replication</h2>
            ${banner}${alert}
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
            <p class="muted">This node: <b class="mono">${esc(byName['repl.endpoint'] || '—')}</b> — ${esc(byName['repl.role'] || '—')}, priority ${esc(byName['repl.priority'] || '—')}
                ${byName['repl.lastPromotionReason'] ? ' · last promote: ' + esc(byName['repl.lastPromotionReason']) : ''}
                ${byName['repl.reconciliation'] ? ' · reconciliation ' + esc(byName['repl.reconciliation']) : ''}
            </p>
            ${isReplica ? `<p class="muted">Following primary <b class="mono">${esc(byName['repl.redirect'] || '—')}</b> — applied seq ${esc(byName['repl.latestSeq'] || '—')}</p>` : ''}
            <h3>Replica set</h3>
            <p class="muted">Succession order: who serves now, and who takes over if the primary goes away (lowest non-zero priority promotes first).</p>
            <table class="grid">
                <thead><tr><th>Priority</th><th>Node</th><th>Endpoint</th><th>Status</th></tr></thead>
                <tbody>${successionRows || '<tr><td colspan="4" class="muted">Roster empty — no members have joined yet.</td></tr>'}</tbody>
            </table>
            <h3>Attached replicas</h3>
            <p class="muted">Standbys currently streaming from this primary, with how far each is caught up.</p>
            <table class="grid">
                <thead><tr><th>Replica</th><th>Endpoint</th><th>Priority</th><th>State</th><th>Acked seq</th><th>Lag</th></tr></thead>
                <tbody>${rows || emptySlots}</tbody>
            </table>
            ${timelineHtml}`;
    };

    setActiveView(refresh, options.pollIntervalMs);
}
