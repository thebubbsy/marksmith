## Error budget by region

Pipe-table cells carry inline content — math, code, emphasis and links — and Marksmith keeps every
one of them alive through the DOCX round-trip, along with per-column alignment and the header rule.

| Region | Endpoint | Uptime | Budget burn | Status |
| :--- | :--- | ---: | ---: | :---: |
| ap-southeast-2 | `api.au.svc` | 99.98% | $0.41\,B_{\max}$ | **Healthy** |
| us-east-1 | `api.us.svc` | 99.95% | $0.88\,B_{\max}$ | **At risk** |
| eu-west-1 | `api.eu.svc` | 99.99% | $0.22\,B_{\max}$ | **Healthy** |
| sa-east-1 | `api.br.svc` | 99.91% | $1.04\,B_{\max}$ | **Breached** |

Burn is measured against $B_{\max} = (1 - S_{\text{target}}) \cdot T_{28}$, so a value above $1.0$
means the region has spent more than its whole 28-day allowance.
