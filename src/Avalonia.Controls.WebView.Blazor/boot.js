const token = __DOCUMENT_TOKEN__;

if (window !== window.top) {
    throw new Error("The Blazor host only accepts a top-level local document.");
}

function send(kind, message) {
    invokeCSharpAction(JSON.stringify({ token, source: location.href, kind, message }));
}

window.addEventListener("error", event => send("error", event.message));
window.addEventListener("unhandledrejection", event => send("error", String(event.reason)));

// These two globals are the required Microsoft Blazor WebView transport contract.
window.external ??= {};
window.external.sendMessage = message => send("ipc", message);
window.external.receiveMessage = callback => {
    window.__avaloniaBlazorReceiveMessage = callback;
};

try {
    await Blazor.start();
} catch (error) {
    send("error", String(error));
}
