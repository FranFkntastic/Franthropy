const registeredSurfaces = new WeakMap();

export function registerTableResizeSurface(root) {
    if (!root) return;
    unregisterTableResizeSurface(root);

    const state = {
        root,
        scroll: root.querySelector("[data-franthropy-table-resize-scroll]"),
        table: null,
        columns: [],
        rails: [],
        drag: null,
        resizeObserver: null,
        mutationObserver: null,
        abortController: new AbortController()
    };

    registeredSurfaces.set(root, state);
    bindSurface(state);
    refreshState(state);
}

export function refreshTableResizeSurface(root) {
    const state = registeredSurfaces.get(root);
    if (state) refreshState(state);
}

export function unregisterTableResizeSurface(root) {
    const state = registeredSurfaces.get(root);
    if (!state) return;

    finishDrag(state, false);
    state.abortController.abort();
    state.resizeObserver?.disconnect();
    state.mutationObserver?.disconnect();
    registeredSurfaces.delete(root);
}

function bindSurface(state) {
    const { root, scroll, abortController } = state;
    const options = { signal: abortController.signal };

    root.addEventListener("pointerdown", event => beginDrag(state, event), options);
    root.addEventListener("keydown", event => resizeFromKeyboard(state, event), options);
    scroll?.addEventListener("scroll", () => positionRails(state), options);

    state.resizeObserver = new ResizeObserver(() => positionRails(state));
    state.resizeObserver.observe(root);

    state.mutationObserver = new MutationObserver(() => refreshState(state));
    state.mutationObserver.observe(root, { childList: true, subtree: true });
}

function refreshState(state) {
    state.scroll ??= state.root.querySelector("[data-franthropy-table-resize-scroll]");
    state.table = state.scroll?.querySelector("table[data-franthropy-resizable-table]") ?? null;
    state.rails = Array.from(state.root.querySelectorAll("[data-franthropy-table-resize-rail]"));

    if (!state.table) {
        state.columns = [];
        state.rails.forEach(rail => rail.style.display = "none");
        return;
    }

    state.columns = Array.from(state.table.querySelectorAll("col[data-column-id]"));
    restoreWidths(state);
    positionRails(state);
}

function beginDrag(state, event) {
    const rail = event.target.closest("[data-franthropy-table-resize-rail]");
    if (!rail || !state.root.contains(rail) || !state.table) return;

    const columnId = rail.dataset.franthropyTableResizeRail;
    const columnIndex = state.columns.findIndex(column => column.dataset.columnId === columnId);
    if (columnIndex < 0) return;

    event.preventDefault();
    event.stopPropagation();
    finishDrag(state, false);

    const widths = measureWidths(state);
    if (widths.length !== state.columns.length) return;

    applyWidths(state, widths);

    const minWidth = readPositiveNumber(rail.dataset.franthropyTableMinWidth, 1);
    const direction = getComputedStyle(state.table).direction === "rtl" ? -1 : 1;
    const abortController = new AbortController();

    state.drag = {
        rail,
        columnIndex,
        startX: event.clientX,
        startWidth: widths[columnIndex],
        widths,
        minWidth,
        direction,
        pointerId: event.pointerId,
        abortController
    };

    state.root.classList.add("is-resizing");
    rail.classList.add("is-resizing");

    try {
        rail.setPointerCapture(event.pointerId);
    } catch {
        // Document listeners below are the authoritative fallback.
    }

    const options = { capture: true, signal: abortController.signal };
    document.addEventListener("pointermove", moveEvent => moveDrag(state, moveEvent), options);
    document.addEventListener("pointerup", upEvent => finishPointerDrag(state, upEvent), options);
    document.addEventListener("pointercancel", cancelEvent => finishPointerDrag(state, cancelEvent), options);
    window.addEventListener("blur", () => finishDrag(state, true), { signal: abortController.signal });
}

function moveDrag(state, event) {
    const drag = state.drag;
    if (!drag || event.pointerId !== drag.pointerId) return;

    event.preventDefault();
    const delta = (event.clientX - drag.startX) * drag.direction;
    drag.widths[drag.columnIndex] = Math.max(drag.minWidth, Math.round(drag.startWidth + delta));
    applyWidths(state, drag.widths);
    positionRails(state);
}

function finishPointerDrag(state, event) {
    if (!state.drag || event.pointerId !== state.drag.pointerId) return;
    finishDrag(state, true);
}

function finishDrag(state, persist) {
    const drag = state.drag;
    if (!drag) return;

    drag.abortController.abort();
    drag.rail.classList.remove("is-resizing");
    state.root.classList.remove("is-resizing");

    try {
        if (drag.rail.hasPointerCapture(drag.pointerId)) drag.rail.releasePointerCapture(drag.pointerId);
    } catch {
        // The pointer may have been released outside the document.
    }

    if (persist) persistWidths(state, drag.widths);
    state.drag = null;
    positionRails(state);
}

function resizeFromKeyboard(state, event) {
    if (event.key !== "ArrowLeft" && event.key !== "ArrowRight") return;
    const rail = event.target.closest("[data-franthropy-table-resize-rail]");
    if (!rail || !state.table) return;

    const columnId = rail.dataset.franthropyTableResizeRail;
    const columnIndex = state.columns.findIndex(column => column.dataset.columnId === columnId);
    if (columnIndex < 0) return;

    event.preventDefault();
    const widths = measureWidths(state);
    const minWidth = readPositiveNumber(rail.dataset.franthropyTableMinWidth, 1);
    const direction = event.key === "ArrowRight" ? 1 : -1;
    const step = event.shiftKey ? 1 : 10;
    widths[columnIndex] = Math.max(minWidth, Math.round(widths[columnIndex] + direction * step));
    applyWidths(state, widths);
    persistWidths(state, widths);
    positionRails(state);
}

function measureWidths(state) {
    return state.columns.map(column => {
        const id = column.dataset.columnId;
        const header = state.table.querySelector(`thead tr:first-child th[data-column-id="${cssEscape(id)}"]`);
        const fallback = readPositiveNumber(column.dataset.defaultWidth, 120);
        return Math.max(1, Math.round(header?.getBoundingClientRect().width ?? fallback));
    });
}

function applyWidths(state, widths) {
    let totalWidth = 0;
    state.columns.forEach((column, index) => {
        const width = Math.max(1, Math.round(widths[index] ?? 1));
        column.style.width = `${width}px`;
        totalWidth += width;
    });

    state.table.style.width = `${totalWidth}px`;
    state.table.style.minWidth = "0";
}

function restoreWidths(state) {
    const storageKey = state.root.dataset.franthropyTableStorageKey;
    if (!storageKey) return;

    const stored = readStoredWidths(storageKey);
    const widths = state.columns.map(column => {
        const id = column.dataset.columnId;
        const minWidth = readPositiveNumber(column.dataset.minWidth, 1);
        const fallback = readPositiveNumber(column.dataset.defaultWidth, 120);
        return Math.max(minWidth, readPositiveNumber(stored[id], fallback));
    });

    if (Object.keys(stored).length > 0) applyWidths(state, widths);
}

function persistWidths(state, widths) {
    const storageKey = state.root.dataset.franthropyTableStorageKey;
    if (!storageKey) return;

    const stored = {};
    state.columns.forEach((column, index) => {
        stored[column.dataset.columnId] = Math.max(1, Math.round(widths[index]));
    });

    try {
        localStorage.setItem(storageKey, JSON.stringify(stored));
    } catch {
        // Persistence is a convenience; resizing must remain available without it.
    }
}

function readStoredWidths(storageKey) {
    try {
        const value = JSON.parse(localStorage.getItem(storageKey) || "{}");
        return value && typeof value === "object" ? value : {};
    } catch {
        return {};
    }
}

function positionRails(state) {
    if (!state.table || !state.scroll) return;

    const rootRect = state.root.getBoundingClientRect();
    const visibleLeft = rootRect.left;
    const visibleRight = rootRect.right;

    state.rails.forEach(rail => {
        const id = rail.dataset.franthropyTableResizeRail;
        const header = state.table.querySelector(`thead tr:first-child th[data-column-id="${cssEscape(id)}"]`);
        if (!header) {
            rail.style.display = "none";
            return;
        }

        const boundary = header.getBoundingClientRect().right;
        if (boundary < visibleLeft || boundary > visibleRight) {
            rail.style.display = "none";
            return;
        }

        rail.style.display = "block";
        rail.style.left = `${Math.round(boundary - rootRect.left)}px`;
    });
}

function readPositiveNumber(value, fallback) {
    const parsed = Number(value);
    return Number.isFinite(parsed) && parsed > 0 ? parsed : fallback;
}

function cssEscape(value) {
    if (globalThis.CSS?.escape) return CSS.escape(value ?? "");
    return String(value ?? "").replace(/["\\]/g, "\\$&");
}
