const FIELDS = ["orgName", "orgId", "collectorUrl", "policyUrl", "userEmail"];

(async () => {
  const sync = await chrome.storage.sync.get(null);
  for (const f of FIELDS) if (sync[f]) document.getElementById(f).value = sync[f];

  document.getElementById("save").addEventListener("click", async () => {
    const values = {};
    for (const f of FIELDS) values[f] = document.getElementById(f).value.trim();
    // orgMode implicit: this extension IS the governance product, so saving org config activates it.
    await chrome.storage.sync.set(values);
    const status = document.getElementById("status");
    status.textContent = "Saved.";
    setTimeout(() => (status.textContent = ""), 2000);
  });
})();
