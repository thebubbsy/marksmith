const portInput = document.getElementById("port");
const autoSend = document.getElementById("autoSendIdle");
const idleInput = document.getElementById("idleSeconds");
const status = document.getElementById("status");

chrome.storage.sync.get({ port: 47821, autoSendIdle: false, idleSeconds: 20 }, (s) => {
    portInput.value = s.port;
    autoSend.checked = s.autoSendIdle;
    idleInput.value = s.idleSeconds;
});

document.getElementById("save").addEventListener("click", () => {
    const port = Math.min(65535, Math.max(1024, Number(portInput.value) || 47821));
    const idleSeconds = Math.min(300, Math.max(5, Number(idleInput.value) || 20));
    chrome.storage.sync.set({ port, autoSendIdle: autoSend.checked, idleSeconds }, () => {
        status.textContent = "Saved ✓";
        setTimeout(() => (status.textContent = ""), 1500);
    });
});
