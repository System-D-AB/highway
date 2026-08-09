// Shared helpers: fetching, errors, and rendering primitives (022 R-5A).
//
// No build step. These are plain ES modules the browser resolves itself.

export const esc = (s) =>
    String(s ?? '').replace(/[&<>"']/g, (c) => ({
        '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;',
    }[c]));

// ---- the error region (022 R-4A) ------------------------------------------
//
// The old page held broker identity AND error text in one element, and only the
// recorder poller ever rewrote it. So "Connection error: Failed to fetch" could
// sit above a fully-rendered page for ever, and navigating away never cleared it.
//
// An error message that is wrong is worse than none: it teaches the reader to
// ignore the banner that will one day be right.
//
// Each source owns its own entry. Success clears that entry and nothing else.

const errors = new Map();

export function reportError(source, message) {
    errors.set(source, message);
    renderErrors();
}

export function clearError(source) {
    if (errors.delete(source)) renderErrors();
}

function renderErrors() {
    const region = document.getElementById('error-region');
    if (!region) return;

    if (errors.size === 0) {
        region.className = 'error-region hidden';
        region.innerHTML = '';
        return;
    }

    region.className = 'error-region';
    region.innerHTML = [...errors.entries()]
        // Naming the source is the point: with several panels polling, "failed to
        // fetch" identifies nothing (R6.2).
        .map(([source, message]) => `<div class="error-row"><b>${esc(source)}</b>: ${esc(message)}</div>`)
        .join('');
}

/**
 * Fetches JSON, reporting failure under `source` and clearing it on success.
 * Returns null on failure so callers can render an empty state rather than throw.
 */
export async function getJson(source, url) {
    try {
        const response = await fetch(url, { headers: { 'Accept': 'application/json' } });
        if (!response.ok) {
            reportError(source, `HTTP ${response.status}`);
            return null;
        }
        clearError(source);
        return await response.json();
    } catch (e) {
        reportError(source, e.message || 'request failed');
        return null;
    }
}

// ---- formatting ------------------------------------------------------------

export function bytes(n) {
    if (n === null || n === undefined) return '—';
    if (n < 1024) return `${n} B`;
    if (n < 1024 * 1024) return `${(n / 1024).toFixed(1)} KB`;
    if (n < 1024 * 1024 * 1024) return `${(n / 1024 / 1024).toFixed(1)} MB`;
    return `${(n / 1024 / 1024 / 1024).toFixed(2)} GB`;
}

/**
 * Fullness as a proportion, never a bare number (022 R3.2). "847 MB" means nothing
 * without the limit beside it; "83% of 1 GB" is actionable at a glance.
 */
export function fullness(used, max) {
    if (!max || used === null || used === undefined) return '—';
    const pct = (used / max) * 100;
    const shown = pct < 1 ? '<1%' : `${pct.toFixed(0)}%`;
    return `${shown} of ${bytes(max)}`;
}

/** Elapsed time as an interpretation rather than arithmetic homework (022 R2.2). */
export function since(seconds) {
    if (seconds < 60) return `${Math.floor(seconds)}s`;
    if (seconds < 3600) return `${Math.floor(seconds / 60)}m`;
    if (seconds < 86400) return `${Math.floor(seconds / 3600)}h`;
    return `${Math.floor(seconds / 86400)}d`;
}

// ---- the scheduler (022 R-7A) ---------------------------------------------
//
// One timer for the whole page, and only the visible view polls. Several views
// each running their own interval is how a dashboard quietly becomes the busiest
// client its own broker has.

let timer = null;
let active = null;

export function setActiveView(refresh, intervalMs) {
    if (timer) clearInterval(timer);
    active = refresh;

    if (!refresh) { timer = null; return; }

    refresh();
    timer = setInterval(() => active && active(), intervalMs);
}
