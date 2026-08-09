// Router and bootstrap (022 R-5A, R-6A, R-7A).
//
// Routes carry kind and name as QUERY PARAMS rather than path segments, because a
// service or queue name may legitimately contain '/' and a path segment would be
// ambiguous about where the name ends.
import { getJson, setActiveView } from './shared.js';
import * as nodes from './nodes.js';
import * as catalogue from './catalogue.js';
import * as entity from './entity.js';
import * as diagnostics from './diagnostics.js';
import * as message from './message.js';
import * as events from './events.js';

const VIEWS = {
    '/nodes': nodes,
    '/catalogue': catalogue,
    '/entity': entity,
    '/diagnostics': diagnostics,
    '/message': message,
    '/events': events,
};

const options = { pollIntervalMs: 3000 };

function parseRoute() {
    const hash = location.hash.replace(/^#/, '') || '/catalogue';
    const [path, query] = hash.split('?');
    return { path, params: new URLSearchParams(query || '') };
}

async function route() {
    const { path, params } = parseRoute();
    const view = VIEWS[path] || catalogue;

    for (const link of document.querySelectorAll('nav a'))
        link.classList.toggle('active', link.getAttribute('href') === `#${path}`);

    // Stop the previous view's polling before the next one starts, or two views
    // poll for ever and the dashboard becomes its own broker's busiest client.
    setActiveView(null, 0);
    await view.render(document.getElementById('view'), options, params);
}

async function boot() {
    const info = await getJson('broker', 'api/recorder');
    if (info) {
        // Broker identity, and ONLY broker identity. It used to share an element
        // with error text, so a failure overwrote it permanently (022 R-4A).
        document.getElementById('broker-info').textContent = info.broker;
    }

    window.addEventListener('hashchange', route);
    await route();
}

boot();
