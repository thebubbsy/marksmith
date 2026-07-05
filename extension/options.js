const portInput = document.getElementById("port");
const status = document.getElementById("status");

chrome.storage.sync.get({ port: 47821 }, ({ port }) => (portInput.value = port));

document.getElementById("save").addEventListener("click", () => {
    const port = Math.min(65535, Math.max(1024, Number(portInput.value) || 47821));
    chrome.storage.sync.set({ port }, () => {
        status.textContent = "Saved ✓";
        setTimeout(() => (status.textContent = ""), 1500);
    });
});
