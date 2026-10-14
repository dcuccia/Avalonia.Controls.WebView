const token = __DOCUMENT_TOKEN__;

if (window !== window.top) {
    throw new Error("The Blazor host only accepts a top-level local document.");
}

function send(kind, message) {
    invokeCSharpAction(JSON.stringify({ token, source: location.href, kind, message }));
}

// These two globals are the required Microsoft Blazor WebView transport contract.
window.external.sendMessage = message => send("ipc", message);
window.external.receiveMessage = callback => {
    window.__avaloniaBlazorReceiveMessage = callback;
};

window.addEventListener("error", event => send("error", event.message));
window.addEventListener("unhandledrejection", event => send("error", String(event.reason)));

try {
    await Blazor.start();
} catch (error) {
    send("error", String(error));
}
