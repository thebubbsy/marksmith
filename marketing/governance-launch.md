# Marksmith Governance Monitor — Launch Kit

Ready-to-post copy for the launch of the Marksmith Governance Monitor (Enterprise Extension).

---

## Positioning (the core message)

> **Absolute forensic ground truth for AI usage.** The Marksmith Governance Monitor is a zero-friction, enterprise DLP extension for managed devices. When an employee leaks sensitive data to an AI chatbot, it captures the exact, unmasked raw text of the entire message—giving Incident Response teams the actual context and payload they need to secure the perimeter.

One-liner: **Silent, no-friction AI Governance and DLP capture for enterprise SecOps.**

---

## LinkedIn / Enterprise B2B Post

**The problem with most AI DLP tools? They mask the data you actually need.**

When an employee pastes an AWS key into ChatGPT, getting an alert that says `[redacted API key]` doesn't help your Incident Response team. You need the exact key to search CloudTrail. You need the context of the entire message to know if it was an accidental paste buried in 2,000 lines of code, or a deliberate exfiltration.

Enter the **Marksmith Governance Monitor**. 

We built it for hardcore SecOps and IR teams who need absolute ground truth. Deployed silently to managed enterprise devices (via Intune/GPO), it monitors interactions with ChatGPT, Gemini, and Claude without adding user friction or consent popups. 

*   **Full Raw Capture**: When a DLP policy is triggered (e.g. AWS keys, passwords, PII), it captures the *exact, unmasked raw message* sent by the user.
*   **Privacy where it matters**: Clean messages that don't trigger DLP rules are NEVER captured. We only store data when a real policy violation occurs.
*   **Self-Hosted Backend**: The collector runs entirely on your own infrastructure. You accept the liability of storing the plaintext secrets, keeping them out of third-party SaaS clouds.

Stop settling for redacted previews. Equip your SOC with the raw intelligence they need to respond to AI data leaks today.

---

## X / Twitter thread (SecOps focused)

1. Most AI DLP tools are designed to protect the vendor's liability, not your company's network. When an employee leaks an AWS key to ChatGPT, getting an alert that says "User leaked [REDACTED]" is useless for Incident Response. 🧵
2. You need the exact key to search your CloudTrail logs. You need the surrounding message to understand intent. You need absolute forensic ground truth.
3. Today we're launching the Marksmith Governance Monitor. It's a silent, managed browser extension that monitors AI chat usage without user friction or consent popups.
4. How it works: 99% of messages are clean. For those, we capture zero content. But when an employee triggers a DLP rule (passwords, keys, PII), the extension executes based on your enterprise policy.
5. By default, it provides a zero-knowledge masked preview. But if your IR team accepts the liability, you can toggle it to **Full Raw Capture**, pushing the exact, unmasked raw text of the entire message to your self-hosted collector.
6. Deploys via Intune/GPO directly to your managed fleet. If you're serious about securing employee AI usage and want configurable forensic data, check it out below. 👇

---

## Product Hunt / Show HN 

- **Name:** Marksmith Governance
- **Tagline:** Configurable AI DLP and forensic capture for SecOps
- **Description:**
  > Marksmith Governance is an enterprise browser extension deployed to managed devices that monitors employee interactions with ChatGPT, Gemini, and Claude. It puts the control back in the hands of the organization. By default, it operates as a privacy-preserving DLP tool that masks secrets. But if your Incident Response team needs absolute ground truth, you can configure it for Full Raw Capture. When an employee triggers a DLP policy, the extension captures the exact, unmasked raw text of the entire message and sends it to a self-hosted collector. It operates silently with zero user friction, dropping all clean messages to preserve privacy where appropriate, while giving your organization the exact level of forensic data you choose to accept.
