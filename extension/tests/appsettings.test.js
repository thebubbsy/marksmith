// appsettings.test.js — the "MarkSmith app · live" tab's value handling.
// Run: node extension/tests/appsettings.test.js
const { coerceValue, describeResult } = require("../appsettings.js");

let passed = 0, failed = 0;
const t = (name, cond) => { if (cond) { passed++; console.log("  ✓", name); } else { failed++; console.error("  ✗ FAIL:", name); } };

const width = { key: "contentWidth", label: "Page width (px)", kind: "number", min: 400, max: 2400 };
t("number in range", coerceValue(width, "900").value === 900);
t("number below range refused with the range", coerceValue(width, "50").error === "Page width (px) must be between 400 and 2400.");
t("decimal refused", /whole number/.test(coerceValue(width, "9.5").error));
t("blank number refused", !!coerceValue(width, "").error);

const shift = { key: "headingShift", label: "Heading level shift", kind: "number", min: -5, max: 5 };
t("negative numbers allowed", coerceValue(shift, "-2").value === -2);

const theme = { key: "theme", label: "Theme", kind: "choice", choices: [{ value: "Dracula", label: "Dracula" }] };
t("known choice", coerceValue(theme, "Dracula").value === "Dracula");
t("unknown choice refused", !!coerceValue(theme, "Nope").error);

const toggle = { key: "noEmoji", label: "No emoji", kind: "toggle" };
t("toggle true", coerceValue(toggle, true).value === true);
t("toggle false", coerceValue(toggle, false).value === false);

const to = { key: "emailTo", label: "To", kind: "text", maxLength: 10 };
t("text passes through", coerceValue(to, "a@b.co").value === "a@b.co");
t("text over the limit refused", /limited to 10/.test(coerceValue(to, "x".repeat(11)).error));
t("empty text allowed (clears it)", coerceValue(to, "").value === "");

t("applied reads as saved", describeResult(toggle, { applied: ["noEmoji"], rejected: [] }).kind === "ok");
t("refusal shows the app's reason", describeResult(width, { applied: [], rejected: [{ key: "contentWidth", reason: "Nope." }] }).text === "Nope.");
t("no answer is an error", describeResult(toggle, null).kind === "err");

console.log(`\nResults: ${passed} passed, ${failed} failed`);
process.exit(failed ? 1 : 0);
