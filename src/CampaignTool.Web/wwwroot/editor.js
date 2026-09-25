// Cursor-aware helpers for the email body box (a textarea inside the element with the given id).
function box(id) { return document.querySelector('#' + id + ' textarea'); }

export function selection(id) {
    const t = box(id);
    if (!t) return { start: -1, end: -1, text: '' };
    return { start: t.selectionStart, end: t.selectionEnd, text: t.value.substring(t.selectionStart, t.selectionEnd) };
}
