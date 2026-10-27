// Keyboard shortcuts, focus helpers and drag-and-drop glue for the board.
window.doiq = {
    _dotnet: null,
    _handler: null,

    init(dotnet, token) {
        this._dotnet = dotnet;
        this._token = token;
        if (this._handler) window.removeEventListener('keydown', this._handler);

        const textTypes = ['checkbox', 'radio', 'button', 'submit', 'reset', 'range', 'color', 'file'];
        const isTyping = (t) => {
            if (!t) return false;
            if (t.tagName === 'TEXTAREA' || t.tagName === 'SELECT' || t.isContentEditable) return true;
            return t.tagName === 'INPUT' && !textTypes.includes((t.type || '').toLowerCase());
        };

        this._handler = (e) => {
            const t = e.target;
            if (e.key === 'Escape' && isTyping(t)) { t.blur(); e.preventDefault(); return; } // Esc leaves the field first
            if (isTyping(t) || e.ctrlKey || e.metaKey) return;

            const k = e.key;
            const lower = k.length === 1 ? k.toLowerCase() : k;
            const single = 'jklhnfgtcisb/?1234,'.includes(lower) && k.length === 1;
            const nav = e.altKey && ['ArrowUp', 'ArrowDown', 'Home', 'End'].includes(k);
            const handled = single || nav || k === 'Escape' || (k === 'Enter' && !t.closest('button, a, summary, select'));
            // Space and Enter keep their native meaning on buttons and checkboxes.
            if (!handled) return;
            if (e.altKey && !nav) return;
            e.preventDefault();
            this._dotnet.invokeMethodAsync('OnKey', k, e.shiftKey, e.altKey);
        };
        window.addEventListener('keydown', this._handler);

        // Firefox only starts a drag when something is set on the transfer.
        document.addEventListener('dragstart', (e) => {
            if (e.dataTransfer) { e.dataTransfer.effectAllowed = 'move'; e.dataTransfer.setData('text/plain', 'doiq-card'); }
        });
    },

    // A page that was just replaced must not tear down its successor's handler.
    dispose(token) {
        if (token !== this._token) return;
        if (this._handler) window.removeEventListener('keydown', this._handler);
        this._handler = null;
        this._dotnet = null;
    },

    async copy(text) {
        await navigator.clipboard.writeText(text);
    },

    focus(selector) {
        const el = document.querySelector(selector);
        if (el) { el.focus(); if (el.select && el.tagName === 'INPUT') el.select(); }
    },

    scrollTo(selector) {
        const el = document.querySelector(selector);
        if (el) el.scrollIntoView({ block: 'nearest', behavior: 'smooth' });
    },

    // Theme picked in this browser: 'light', 'dark' or 'system' (follow the OS). Stored like the last board.
    theme() {
        try { const t = localStorage.getItem('doiq.theme'); return t === 'light' || t === 'dark' ? t : 'system'; } catch { return 'system'; }
    },

    setTheme(theme) {
        const root = document.documentElement;
        if (theme === 'light' || theme === 'dark') root.dataset.theme = theme; else delete root.dataset.theme;
        try { if (root.dataset.theme) localStorage.setItem('doiq.theme', theme); else localStorage.removeItem('doiq.theme'); } catch { /* private mode: not remembered */ }
    },

    // The board shown last in this browser (localStorage can be blocked; then there is simply none).
    lastBoard() {
        try { return parseInt(localStorage.getItem('doiq.board') || '0', 10) || 0; } catch { return 0; }
    },

    rememberBoard(id) {
        try { localStorage.setItem('doiq.board', String(id)); } catch { /* private mode: not remembered */ }
    },

    // Keeps Tab inside a modal dialog.
    trapFocus(selector) {
        const dialog = document.querySelector(selector);
        if (!dialog) return;
        dialog.addEventListener('keydown', (e) => {
            if (e.key !== 'Tab') return;
            const items = [...dialog.querySelectorAll('button:not([disabled]), a[href], input:not([disabled])')];
            if (items.length === 0) return;
            const first = items[0], last = items[items.length - 1];
            if (e.shiftKey && document.activeElement === first) { e.preventDefault(); last.focus(); }
            else if (!e.shiftKey && document.activeElement === last) { e.preventDefault(); first.focus(); }
        });
    }
};
