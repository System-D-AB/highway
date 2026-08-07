(function () {
    'use strict';

    // --- State ---
    let currentName = null;
    let currentFrom = '-5m';
    let liveSource = null;
    let refreshTimer = null;

    // --- DOM refs ---
    const brokerInfo = document.getElementById('broker-info');
    const exposureBanner = document.getElementById('exposure-banner');
    const overviewSection = document.getElementById('overview');
    const nameViewSection = document.getElementById('name-view');
    const statsEl = document.getElementById('stats');
    const nameTbody = document.querySelector('#name-table tbody');
    const nameTitle = document.getElementById('name-title');
    const eventTbody = document.querySelector('#event-table tbody');
    const detailPanel = document.getElementById('event-detail');
    const detailContent = document.getElementById('detail-content');
    const dropNotice = document.getElementById('drop-notice');
    const eventsState = document.getElementById('events-state');
    const nodeFilter = document.getElementById('node-filter');
    const limitField = document.getElementById('limit-field');
    const refreshBtn = document.getElementById('refresh-btn');
    const liveToggle = document.getElementById('live-toggle');
    const windowBtns = document.querySelectorAll('.window-btn');

    // --- Routing ---
    function route() {
        const hash = location.hash || '#/';
        const match = hash.match(/^#\/events\/(.+)$/);
        if (match) {
            currentName = decodeURIComponent(match[1]);
            showNameView();
        } else {
            currentName = null;
            showOverview();
        }
    }

    function showOverview() {
        overviewSection.classList.remove('hidden');
        nameViewSection.classList.add('hidden');
        stopLive();
        loadRecorder();
        startAutoRefresh();
    }

    function showNameView() {
        overviewSection.classList.add('hidden');
        nameViewSection.classList.remove('hidden');
        nameTitle.textContent = currentName;
        detailPanel.classList.add('hidden');
        dropNotice.classList.add('hidden');
        eventsState.classList.add('hidden');
        stopAutoRefresh();
        loadEvents();
    }

    // --- Overview ---
    async function loadRecorder() {
        try {
            const res = await fetch('api/recorder');
            if (!res.ok) { brokerInfo.textContent = 'Error: ' + res.status; return; }
            const data = await res.json();

            brokerInfo.textContent = 'Broker: ' + data.broker + ' | Recorder: ' + (data.enabled ? 'enabled' : 'disabled');

            if (data.replayEnabled === false && data.enabled) {
                brokerInfo.textContent += ' | Replay: disabled';
            }

            statsEl.innerHTML =
                stat('Events', data.totalEvents) +
                stat('Bytes', formatBytes(data.totalBytes)) +
                stat('Names', data.names.length) +
                stat('Dropped (capacity)', data.droppedCapacity, data.droppedCapacity > 0 ? 'warn' : '') +
                stat('Dropped (budget)', data.droppedBudget, data.droppedBudget > 0 ? 'warn' : '') +
                stat('Failures', data.failures, data.failures > 0 ? 'error' : '') +
                stat('Observer failures', data.observerFailures, data.observerFailures > 0 ? 'error' : '');

            nameTbody.innerHTML = data.names.map(function (n) {
                return '<tr>' +
                    '<td><a href="#/events/' + encodeURIComponent(n.name) + '">' + esc(n.name) + '</a></td>' +
                    '<td>' + n.count + '</td>' +
                    '<td>' + formatBytes(n.bytes) + '</td>' +
                    '<td>' + esc(n.capture) + '</td>' +
                    '<td class="' + (n.droppedCapacity > 0 ? 'warn' : '') + '">' + n.droppedCapacity + '</td>' +
                    '</tr>';
            }).join('');
        } catch (e) {
            brokerInfo.textContent = 'Connection error: ' + e.message;
        }
    }

    function stat(label, value, cls) {
        return '<div class="stat"><div class="stat-label">' + esc(label) + '</div><div class="stat-value' + (cls ? ' ' + cls : '') + '">' + value + '</div></div>';
    }

    // --- Name view / Events ---
    async function loadEvents() {
        if (!currentName) return;
        eventsState.classList.add('hidden');

        const node = nodeFilter.value.trim() || null;
        const limit = parseInt(limitField.value, 10) || 100;

        let url = 'api/events/' + encodeURIComponent(currentName) + '?from=' + encodeURIComponent(currentFrom) + '&limit=' + limit;
        if (node) url += '&node=' + encodeURIComponent(node);

        try {
            const res = await fetch(url);
            if (!res.ok) { showState('Error: ' + res.status); return; }
            const data = await res.json();

            if (data.state === 'disabled') {
                showState('Recorder is disabled.');
                eventTbody.innerHTML = '';
                return;
            }
            if (data.state === 'unknown') {
                showState('Name "' + currentName + '" is not known to the recorder.');
                eventTbody.innerHTML = '';
                return;
            }
            if (data.events.length === 0) {
                showState('No events in the selected window.');
                eventTbody.innerHTML = '';
                return;
            }

            renderEvents(data.events);
        } catch (e) {
            showState('Connection error: ' + e.message);
        }
    }

    function renderEvents(events) {
        eventsState.classList.add('hidden');
        eventTbody.innerHTML = events.map(function (e, i) {
            var failed = e.errorCode ? ' event-failed' : '';
            return '<tr class="event-row' + failed + '" data-idx="' + i + '">' +
                '<td>' + formatTime(e.timestamp) + '</td>' +
                '<td>' + esc(e.type) + '</td>' +
                '<td class="truncate">' + esc(e.node || '') + '</td>' +
                '<td class="truncate">' + esc(e.requestId || '') + '</td>' +
                '<td>' + (e.messageId != null ? e.messageId : '') + '</td>' +
                '<td>' + formatBytes(e.payloadSize) + '</td>' +
                '<td>' + esc(e.payloadState) + '</td>' +
                '<td>' + esc(e.errorCode || '') + '</td>' +
                '</tr>';
        }).join('');

        // Attach click handlers for detail expansion
        eventTbody.querySelectorAll('.event-row').forEach(function (row) {
            row.addEventListener('click', function () {
                var idx = parseInt(row.dataset.idx, 10);
                showDetail(events[idx]);
            });
        });
    }

    function showDetail(evt) {
        var html = '';
        html += dt('Timestamp', evt.timestamp);
        html += dt('Type', evt.type);
        html += dt('Name', evt.name);
        html += dt('Node', evt.node || '—');
        html += dt('Request ID', evt.requestId || '—');
        html += dt('Message ID', evt.messageId != null ? evt.messageId : '—');
        html += dt('Payload Size', formatBytes(evt.payloadSize));
        html += dt('Payload State', evt.payloadState);
        html += dt('Error Code', evt.errorCode || '—');
        html += dt('Count', evt.count != null ? evt.count : '—');

        if (evt.payload) {
            var decoded = decodePayload(evt.payload);
            html += '<dt>Payload</dt><dd><div class="payload-content">' + esc(decoded) + '</div></dd>';
        }

        detailContent.innerHTML = html;
        detailPanel.classList.remove('hidden');
    }

    function decodePayload(base64) {
        try {
            var bytes = atob(base64);
            // Try UTF-8 decode
            var utf8 = decodeURIComponent(escape(bytes));
            // Verify it's printable / valid
            if (/[\x00-\x08\x0e-\x1f]/.test(utf8)) {
                return toHex(bytes);
            }
            return utf8;
        } catch (e) {
            try {
                return toHex(atob(base64));
            } catch (e2) {
                return '(decode error)';
            }
        }
    }

    function toHex(str) {
        var hex = '';
        for (var i = 0; i < str.length; i++) {
            var b = str.charCodeAt(i).toString(16).padStart(2, '0');
            hex += b + ' ';
            if ((i + 1) % 16 === 0) hex += '\n';
        }
        return hex.trim();
    }

    function showState(msg) {
        eventsState.textContent = msg;
        eventsState.classList.remove('hidden');
    }

    // --- Live tailing ---
    function startLive() {
        if (!currentName) return;
        if (liveSource) return;

        var url = 'api/stream/' + encodeURIComponent(currentName);
        liveSource = new EventSource(url);

        liveSource.onmessage = function (e) {
            try {
                var evt = JSON.parse(e.data);
                appendLiveEvent(evt);
            } catch (err) { /* ignore parse errors */ }
        };

        liveSource.addEventListener('dropped', function (e) {
            try {
                var data = JSON.parse(e.data);
                dropNotice.textContent = '⚠️ ' + data.count + ' events dropped (slow consumer)';
                dropNotice.classList.remove('hidden');
            } catch (err) { /* ignore */ }
        });

        liveSource.onerror = function () {
            // Reconnection is handled by EventSource automatically
        };

        liveToggle.textContent = 'On';
        liveToggle.classList.add('active');
    }

    function stopLive() {
        if (liveSource) {
            liveSource.close();
            liveSource = null;
        }
        liveToggle.textContent = 'Off';
        liveToggle.classList.remove('active');
    }

    function appendLiveEvent(evt) {
        eventsState.classList.add('hidden');
        var failed = evt.errorCode ? ' event-failed' : '';
        var row = document.createElement('tr');
        row.className = 'event-row' + failed;
        row.innerHTML =
            '<td>' + formatTime(evt.timestamp) + '</td>' +
            '<td>' + esc(evt.type) + '</td>' +
            '<td class="truncate">' + esc(evt.node || '') + '</td>' +
            '<td class="truncate">' + esc(evt.requestId || '') + '</td>' +
            '<td>' + (evt.messageId != null ? evt.messageId : '') + '</td>' +
            '<td>' + formatBytes(evt.payloadSize) + '</td>' +
            '<td>' + esc(evt.payloadState) + '</td>' +
            '<td>' + esc(evt.errorCode || '') + '</td>';

        row.addEventListener('click', function () { showDetail(evt); });

        // Insert at top for latest-first
        if (eventTbody.firstChild) {
            eventTbody.insertBefore(row, eventTbody.firstChild);
        } else {
            eventTbody.appendChild(row);
        }

        // Cap visible rows
        while (eventTbody.children.length > 200) {
            eventTbody.removeChild(eventTbody.lastChild);
        }
    }

    // --- Auto-refresh (overview only) ---
    function startAutoRefresh() {
        stopAutoRefresh();
        refreshTimer = setInterval(loadRecorder, 3000);
    }

    function stopAutoRefresh() {
        if (refreshTimer) { clearInterval(refreshTimer); refreshTimer = null; }
    }

    // --- Utilities ---
    function formatBytes(b) {
        if (b < 1024) return b + ' B';
        if (b < 1048576) return (b / 1024).toFixed(1) + ' KB';
        return (b / 1048576).toFixed(1) + ' MB';
    }

    function formatTime(ts) {
        try {
            var d = new Date(ts);
            return d.toLocaleTimeString(undefined, { hour12: false }) + '.' + String(d.getMilliseconds()).padStart(3, '0');
        } catch (e) { return ts; }
    }

    function esc(s) {
        if (!s) return '';
        var el = document.createElement('span');
        el.textContent = s;
        return el.innerHTML;
    }

    function dt(label, value) {
        return '<dt>' + esc(label) + '</dt><dd>' + esc(String(value)) + '</dd>';
    }

    // --- Event bindings ---
    windowBtns.forEach(function (btn) {
        btn.addEventListener('click', function () {
            windowBtns.forEach(function (b) { b.classList.remove('active'); });
            btn.classList.add('active');
            currentFrom = btn.dataset.from;
            loadEvents();
        });
    });

    refreshBtn.addEventListener('click', loadEvents);

    liveToggle.addEventListener('click', function () {
        if (liveSource) {
            stopLive();
        } else {
            startLive();
        }
    });

    // --- Init ---
    window.addEventListener('hashchange', route);
    route();
})();
