function mcpToggleConsole(btn) {
    var el = btn.closest('.mcp-console');
    el.classList.toggle('is-collapsed');
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
