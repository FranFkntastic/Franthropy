const registrations = new WeakMap();

export function registerFilterAutocomplete(root, dotNetReference) {
    if (!root) return;
    unregisterFilterAutocomplete(root);

    const input = root.querySelector("input");
    if (!input) return;

    const abortController = new AbortController();
    let inputRevision = 0;
    input.addEventListener("input", () => {
        const value = input.value ?? "";
        const caret = Number.isInteger(input.selectionStart) ? input.selectionStart : value.length;
        dotNetReference.invokeMethodAsync("HandleAutocompleteInput", value, caret, ++inputRevision);
    }, { signal: abortController.signal });

    input.addEventListener("keydown", event => {
        if (!root.querySelector('[role="listbox"]')) return;
        if (!["ArrowDown", "ArrowUp", "Enter", "Tab", "Escape"].includes(event.key)) return;

        event.preventDefault();
        event.stopPropagation();
        dotNetReference.invokeMethodAsync("HandleAutocompleteKey", event.key);
    }, { capture: true, signal: abortController.signal });

    registrations.set(root, { abortController });
}

export function unregisterFilterAutocomplete(root) {
    const registration = registrations.get(root);
    if (!registration) return;
    registration.abortController.abort();
    registrations.delete(root);
}

export function focusAndSetCaret(input, position) {
    if (!input) return;
    input.focus({ preventScroll: true });
    input.setSelectionRange(position, position);
}
