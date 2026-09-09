// Auto-scroll helper for the live debate transcript. Keeps the newest turn in view
// as content streams in. Invoked from DebateChamber.razor via JS interop.
window.debateScroll = {
    toBottom: function (id) {
        const el = document.getElementById(id);
        if (el) {
            el.scrollTop = el.scrollHeight;
        }
    }
};
