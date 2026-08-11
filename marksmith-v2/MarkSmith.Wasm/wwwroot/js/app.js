// Marksmith WASM — client-side file download bridge.
// Blazor passes the .docx bytes (byte[] → Uint8Array) and we hand them to the browser.
window.marksmithDownload = function (filename, bytes) {
    const blob = new Blob([bytes], { type: "application/vnd.openxmlformats-officedocument.wordprocessingml.document" });
    const url = URL.createObjectURL(blob);
    const a = document.createElement("a");
    a.href = url;
    a.download = filename;
    document.body.appendChild(a);
    a.click();
    a.remove();
    URL.revokeObjectURL(url);
};
