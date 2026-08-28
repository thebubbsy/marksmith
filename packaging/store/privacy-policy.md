# Marksmith — Privacy Policy

_Last updated: 2026-07-06_

Marksmith is a local-first desktop application. This policy explains what it does and does
not do with your data.

## The short version

Marksmith does **not** collect, transmit, or sell your personal data. Your documents are
processed **locally on your device**. There is no Marksmith account, no telemetry, and no
analytics.

## What stays on your device

- **Documents and Markdown** you open, paste, or convert are processed entirely on your
  computer. Exported PDF/DOCX files are written only to the output folder you choose.
- **Settings** (themes, formatting options, watch folders, recent files) are stored locally
  in `%LOCALAPPDATA%\MarkSmith\settings.json`.
- **History** of your exports is stored locally.

## Network access

Marksmith works fully offline. It makes network requests only in these cases:

- **Rendering assets:** diagram (Mermaid) and math (KaTeX) and syntax-highlighting libraries
  may be loaded from a public CDN to render a preview. No document content is sent; only the
  libraries are fetched.
- **Update check:** if you press "Check for updates," Marksmith queries the public GitHub
  Releases API for the latest version number. No personal data is sent.
- **Local REST API:** the optional API listens only on `127.0.0.1` (your machine) and is off
  unless you enable it.

## Optional: AI-usage governance (organizations)

Marksmith includes an **optional, off-by-default** governance feature for organizations. When
an administrator deploys it and an employee consents, the companion browser extension may
report **usage metadata and data-loss-prevention flags only — never conversation content —**
to an administrator-operated collector. This requires explicit consent and is intended for
transparent, lawful workplace-governance use. See `store/GOVERNANCE.md` for details and the
legal considerations before deployment.

## The browser extension

The optional Marksmith Connector browser extension reads the content of an AI chat page only
when you click its button (or enable auto-send) and sends that content to the Marksmith app
running locally on your machine. It does not transmit data to any third party.

## Children

Marksmith is not directed at children and does not knowingly collect data from anyone.

## Contact

Questions: **mbubbtech@gmail.com** · Source code: https://github.com/thebubbsy/marksmith
