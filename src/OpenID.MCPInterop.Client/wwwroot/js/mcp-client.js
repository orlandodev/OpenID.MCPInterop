function mcpToggleConsole(btn) {
    var el = btn.closest('.mcp-console');
    el.classList.toggle('is-collapsed');
    if (el.classList.contains('is-collapsed')) {
        // A dragged-in inline height would otherwise beat the .is-collapsed
        // CSS rule (inline style always wins over a class rule) and the
        // panel wouldn't visibly collapse at all.
        el.style.height = '';
    } else {
        mcpApplyStoredConsoleHeight();
    }
    btn.textContent = el.classList.contains('is-collapsed') ? 'Expand' : 'Collapse';
}

function mcpSetMode(mode) {
    var toggle = document.getElementById('mcp-input-toggle');
    if (!toggle) { return; }
    var fields = document.getElementById('mcp-fields-view');
    var json = document.getElementById('mcp-json-view');
    toggle.querySelectorAll('button').forEach(function (b) {
        b.setAttribute('aria-pressed', b.dataset.mode === mode ? 'true' : 'false');
    });
    if (fields) { fields.style.display = mode === 'form' ? '' : 'none'; }
    if (json) { json.style.display = mode === 'json' ? '' : 'none'; }
    if (mode === 'json') { mcpUpdateJsonPreview(); }
}

function mcpUpdateJsonPreview() {
    var fields = document.getElementById('mcp-fields-view');
    var pre = document.getElementById('mcp-json-preview');
    if (!fields || !pre) { return; }
    var obj = {};
    fields.querySelectorAll('[data-field-name]').forEach(function (input) {
        if (input.value) { obj[input.dataset.fieldName] = input.value; }
    });
    pre.textContent = JSON.stringify(obj, null, 2);
}

document.body.addEventListener('input', function (e) {
    if (e.target.closest && e.target.closest('#mcp-fields-view')) {
        mcpUpdateJsonPreview();
    }
});

var MCP_CONSOLE_HEIGHT_KEY = 'mcpConsoleHeightPx';

// Re-applies the last dragged console height after every htmx swap - #mcp-app
// (which contains #mcp-console) gets replaced via outerHTML on nearly every
// interaction, so any inline style set by a drag would otherwise be lost.
function mcpApplyStoredConsoleHeight() {
    var el = document.getElementById('mcp-console');
    if (!el) { return; }
    try {
        var stored = localStorage.getItem(MCP_CONSOLE_HEIGHT_KEY);
        if (stored) { el.style.height = stored + 'px'; }
    } catch (e) { /* localStorage unavailable - fall back to the default height */ }
}

function mcpInitConsoleResize() {
    document.body.addEventListener('pointerdown', function (e) {
        var handle = e.target.closest('.mcp-console-resize-handle');
        if (!handle) { return; }
        var consoleEl = document.getElementById('mcp-console');
        if (!consoleEl) { return; }

        var startY = e.clientY;
        var startHeight = consoleEl.getBoundingClientRect().height;
        var minHeight = 120;
        var maxHeight = Math.max(minHeight, consoleEl.parentElement.clientHeight - 150);
        handle.classList.add('is-dragging');
        handle.setPointerCapture(e.pointerId);

        function onMove(moveEvent) {
            var delta = startY - moveEvent.clientY;
            var next = Math.min(maxHeight, Math.max(minHeight, startHeight + delta));
            consoleEl.style.height = next + 'px';
        }
        function onUp() {
            handle.classList.remove('is-dragging');
            handle.removeEventListener('pointermove', onMove);
            handle.removeEventListener('pointerup', onUp);
            try { localStorage.setItem(MCP_CONSOLE_HEIGHT_KEY, Math.round(consoleEl.getBoundingClientRect().height)); }
            catch (e) { /* localStorage unavailable */ }
        }
        handle.addEventListener('pointermove', onMove);
        handle.addEventListener('pointerup', onUp);
    });

    // afterSettle (not afterSwap) - afterSwap can fire before the new
    // #mcp-console has actually replaced the old one in the live DOM for an
    // outerHTML swap, so re-querying by id there can still hand back the
    // about-to-be-discarded old element.
    document.body.addEventListener('htmx:afterSettle', mcpApplyStoredConsoleHeight);
    mcpApplyStoredConsoleHeight();
}

function mcpCopyLog(btn) {
    var body = btn.closest('.mcp-console').querySelector('.mcp-console-body');
    var lines = Array.prototype.map.call(body.querySelectorAll('.mcp-log-line'), function (line) {
        var time = line.querySelector('.mcp-log-time');
        var text = line.querySelector('.mcp-log-text');
        return (time ? time.textContent + ' ' : '') + (text ? text.textContent : '');
    });
    var original = btn.textContent;
    navigator.clipboard.writeText(lines.join('\n')).then(function () {
        btn.textContent = 'Copied';
        setTimeout(function () { btn.textContent = original; }, 1500);
    }, function () {
        btn.textContent = 'Copy failed';
        setTimeout(function () { btn.textContent = original; }, 1500);
    });
}

mcpInitConsoleResize();
