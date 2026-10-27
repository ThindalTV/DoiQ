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

// The task drawer's width: dragged on its left edge (or arrow keys on it), kept in --drawer-w and remembered in this
// browser. Delegated from the document, so it works for every drawer without Blazor taking part.
(() => {
    const root = document.documentElement;
    const min = 400;
    const viewport = () => root.clientWidth;   // without the scrollbar, which is where the fixed drawer's right edge sits
    const max = () => Math.max(min, viewport() - 120);
    const clamp = (w) => Math.round(Math.min(max(), Math.max(min, w)));
    const set = (w, remember) => {
        if (w === null) { root.style.removeProperty('--drawer-w'); }
        else root.style.setProperty('--drawer-w', clamp(w) + 'px');
        if (!remember) return;
        try { if (w === null) localStorage.removeItem('doiq.drawerWidth'); else localStorage.setItem('doiq.drawerWidth', String(clamp(w))); } catch { /* private mode: not remembered */ }
    };
    try { const w = parseInt(localStorage.getItem('doiq.drawerWidth') || '', 10); if (w) set(w, false); } catch { }

    const width = (handle) => handle.closest('.drawer').getBoundingClientRect().width;

    document.addEventListener('pointerdown', (e) => {
        const handle = e.target.closest?.('.drawer-resize');
        if (!handle || e.button !== 0) return;
        e.preventDefault();
        handle.setPointerCapture(e.pointerId);
        handle.classList.add('dragging');
        document.body.classList.add('drawer-resizing');
        const move = (m) => set(viewport() - m.clientX, false);
        const up = () => {
            handle.removeEventListener('pointermove', move);
            handle.classList.remove('dragging');
            document.body.classList.remove('drawer-resizing');
            set(width(handle), true);
        };
        handle.addEventListener('pointermove', move);
        handle.addEventListener('pointerup', up, { once: true });
        handle.addEventListener('pointercancel', up, { once: true });
    });

    document.addEventListener('dblclick', (e) => {
        if (e.target.closest?.('.drawer-resize')) set(null, true);
    });

    // Left widens, Right narrows (the drawer grows leftwards); Shift takes bigger steps.
    document.addEventListener('keydown', (e) => {
        const handle = e.target.closest?.('.drawer-resize');
        if (!handle || (e.key !== 'ArrowLeft' && e.key !== 'ArrowRight')) return;
        e.preventDefault();
        const step = (e.shiftKey ? 80 : 20) * (e.key === 'ArrowLeft' ? 1 : -1);
        set(width(handle) + step, true);
    });

    // A remembered width that no longer fits a smaller window shrinks to fit.
    window.addEventListener('resize', () => {
        const w = parseInt(root.style.getPropertyValue('--drawer-w'), 10);
        if (w && w > max()) set(max(), false);
    });
})();
