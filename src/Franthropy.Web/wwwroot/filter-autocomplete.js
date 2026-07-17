const registrations = new WeakMap();

export function registerFilterAutocomplete(root, dotNetReference) {
    if (!root) return;
    unregisterFilterAutocomplete(root);

    const input = root.querySelector("input");
    if (!input) return;

    const abortController = new AbortController();
    let inputRevision = 0;
    let selectionBeforeInput = null;
    let valueBeforeInput = input.value ?? "";
    input.addEventListener("input", event => {
        const value = input.value ?? "";
        const reportedCaret = Number.isInteger(input.selectionStart) ? input.selectionStart : value.length;
        let caret = reportedCaret;
        if (value.length > valueBeforeInput.length && value.startsWith(valueBeforeInput)) {
            caret = value.length;
        } else if (selectionBeforeInput && reportedCaret === selectionBeforeInput.start) {
            if (event.inputType === "deleteContentBackward" && selectionBeforeInput.start === selectionBeforeInput.end) {
                caret = Math.max(0, selectionBeforeInput.start - 1);
            } else {
                const replacedLength = selectionBeforeInput.end - selectionBeforeInput.start;
                const insertedLength = Math.max(0, value.length - (valueBeforeInput.length - replacedLength));
                caret = selectionBeforeInput.start + insertedLength;
            }
        }
        selectionBeforeInput = null;
        valueBeforeInput = value;
        caret = Math.max(0, Math.min(caret, value.length));
        dotNetReference.invokeMethodAsync("HandleAutocompleteInput", value, caret, ++inputRevision);
    }, { signal: abortController.signal });

    input.addEventListener("keydown", event => {
        selectionBeforeInput = {
            start: Number.isInteger(input.selectionStart) ? input.selectionStart : input.value.length,
            end: Number.isInteger(input.selectionEnd) ? input.selectionEnd : input.value.length,
        };
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
