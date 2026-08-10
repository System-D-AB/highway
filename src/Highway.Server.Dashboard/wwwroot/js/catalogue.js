// Catalogue view (022 T5): what exists, what serves it, and what nobody serves.
import { esc, getJson, fullness, setActiveView } from './shared.js';

// Kind and state both arrive decided by the server. Nothing here parses a name:
// '@' separates a derived group-queue name BECAUSE the server derives it that way
// (018 T0), so a rule here would be a second implementation of that convention.
const SECTIONS = [
    ['Services', 'Service'],
    ['Queues', 'Queue'],
    ['Channels', 'Channel'],
];

function stateBadge(entry) {
    switch (entry.state) {
        case 'Live':
            return `<span class="muted">${esc(entry.hosts.join(', '))}</span>`;
        case 'HostStale':
            // Declared, but everyone who declared it has gone quiet. Different from
            // never-declared, and it needs a different action (022 R-2A).
            return `<span class="state-stale">host stale — ${esc(entry.hosts.join(', '))}</span>`;
        case 'NeverDeclared':
            // The row worth having: a service nobody serves looks identical to a
            // healthy one when all you have is a depth number.
            return '<span class="state-absent">⚠ no host — addressed but never declared</span>';
        default:
            return '<span class="muted">unknown</span>';
    }
}

function metrics(entry) {
    if (entry.depth === null || entry.depth === undefined) return '';
    const dlq = entry.deadLettered ? ` · <span class="state-absent">${entry.deadLettered} dead</span>` : '';
    return `<td>${entry.depth} · ${esc(fullness(entry.bytes, entry.maxBytes))}${dlq}</td>`;
}

function link(entry) {
    // Kind and name travel as query params so a '/' inside an identifier is
    // unambiguous (022 R-6A).
    const href = `#/entity?kind=${encodeURIComponent(entry.kind)}&name=${encodeURIComponent(entry.name)}`;
    return `<a href="${href}">${esc(entry.name)}</a>`;
}

function channelBlock(channel, groups) {
    // A channel NESTS its groups. Listing them as peers is the single biggest
    // reason the old page was unreadable: 'orders.placed' and
    // 'orders.placed@shop-1' are one channel and one of its subscribers.
    const children = groups.map((g) => `
        <tr class="child">
            <td>└ ${link(g)}</td>
            <td>${stateBadge(g)}</td>
            ${metrics(g) || '<td></td>'}
        </tr>`).join('');

    return `
        <tr>
            <td>${link(channel)}</td>
            <td>${stateBadge(channel)}</td>
            <td></td>
        </tr>${children}`;
}

export async function render(container, options) {
    const refresh = async () => {
        const data = await getJson('catalogue', 'api/catalogue');
        if (!data) return;
        const jobsData = await getJson('jobs', 'api/jobs');

        const entries = data.entries || [];
        const groups = entries.filter((e) => e.kind === 'Group');

        let html = '<h2>Catalogue</h2>';

        if (data.unavailable) {
            // The declared half is unreadable (mTLS, 022 R-3A). The observed half
            // still works, so the page degrades rather than disappearing.
            html += `<p class="unavailable">Showing observed entities only: ${esc(data.unavailable)}</p>`;
        }

        for (const [heading, kind] of SECTIONS) {
            const rows = entries.filter((e) => e.kind === kind);
            if (rows.length === 0) continue;

            const body = rows.map((e) => kind === 'Channel'
                ? channelBlock(e, groups.filter((g) => g.parentChannel === e.name))
                : `<tr><td>${link(e)}</td><td>${stateBadge(e)}</td>${metrics(e) || '<td></td>'}</tr>`).join('');

            html += `<h3>${heading}</h3><table class="grid"><tbody>${body}</tbody></table>`;
        }

        // Recurring jobs (028). "Never fires — no node processes this queue" is the state
        // this section exists for: a schedule with nothing polling accumulates nothing,
        // silently, and silence is the one thing the dashboard is not allowed to render.
        const jobs = jobsData?.jobs || [];
        if (jobs.length > 0) {
            const body = jobs.map((j) => `<tr>
                <td>${esc(j.job)}</td>
                <td><a href="#/entity?kind=Queue&name=${encodeURIComponent(j.queue)}">${esc(j.queue)}</a></td>
                <td><span class="mono">${esc(j.expression)}</span></td>
                <td>${esc(new Date(j.nextFire).toLocaleString())}</td>
                <td>${j.lastFire ? esc(new Date(j.lastFire).toLocaleString()) : '<span class="muted">never</span>'}</td>
                <td>${j.hosted
                    ? '<span class="state-live">hosted</span>'
                    : '<span class="state-absent">never fires — no node processes this queue</span>'}</td>
            </tr>`).join('');

            html += `<h3>Schedules</h3><table class="grid">
                <thead><tr><th>Job</th><th>Queue</th><th>Schedule</th><th>Next fire</th><th>Last fire</th><th></th></tr></thead>
                <tbody>${body}</tbody></table>`;
        }

        if (entries.filter((e) => e.kind !== 'Internal' && e.kind !== 'Node').length === 0)
            html += '<p class="muted">Nothing has been declared or addressed yet.</p>';

        container.innerHTML = html;
    };

    setActiveView(refresh, options.pollIntervalMs);
}
