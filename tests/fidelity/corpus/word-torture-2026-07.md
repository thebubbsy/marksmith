**1. Aligned table with mixed formatting**

| Feature | Status | Coverage |
|:--------|:------:|---------:|
| **Auth** | ✅ Shipped | 94% |
| *Payments* | 🟡 Beta | 71% |
| ~~Legacy API~~ | ❌ Killed | — |

---

**2. Task list with nesting**

- [x] Design approved
- [ ] Build phase
  - [x] Backend endpoints
  - [ ] Frontend wiring
    - [ ] Error states
- [ ] Ship it 🚀

---

**3. Nested blockquote conversation**

> **Sarah:** Did you deploy on a Friday?
>> **Dev:** Define "deploy"
>>> **Sarah:** Define "Friday"
>
> — *incident retro, unedited*

---

**4. Syntax-highlighted code with a header line**

```typescript
// billing/invoice.ts — DO NOT touch without telling Finance
export async function chargeCustomer(id: string): Promise<Receipt> {
  const customer = await db.customers.findOrThrow(id);
  return stripe.charge(customer, { retries: 3, idempotencyKey: id });
}
```

---

**5. Inline code mixed into dense prose**

Set `MAX_RETRIES=3` in `.env`, then run `npm run migrate -- --force` — if it fails with `ECONNREFUSED`, your `DATABASE_URL` is pointing at `localhost:5432` instead of the container.

---

**6. Definition-style list with hard indents**

**P0 — Drop everything**
: Production down, revenue bleeding

**P1 — Today**
: Major feature broken, workaround exists

**P2 — This sprint**
: Annoying, survivable

---

**7. Horizontal-rule sandwich with centered-feel heading**

---

## ⚡ QUARTERLY NUMBERS ⚡

### Revenue up **34%** · Churn down **2.1pts** · NPS **+12**

---

**8. Mixed ordered/unordered deep nesting**

1. **Phase One** — Discovery
   - Stakeholder interviews
     1. Engineering leads
     2. Support team
        - *Include the night shift this time*
2. **Phase Two** — Build
   - Sprint 1–3
3. **Phase Three** — Regret nothing

---

**9. Table containing code AND emoji AND line-break abuse**

| Endpoint | Example | Notes |
|---|---|---|
| `POST /auth` | `{"user": "kt"}` | 🔐 Rate-limited<br>10 req/min |
| `GET /health` | — | 💚 No auth<br>Cache 30s |

---

**10. The kitchen sink**

> ### 🏆 Release v2.4 — *"The One That Worked"*
>
> | Metric | Before | After |
> |---|---:|---:|
> | Build time | 14m | **3m** |
> | Bundle size | 2.1MB | **840KB** |
>
> - [x] Zero downtime deploy
> - [x] `feature-flags` cleaned up
>
> ```diff
> - const timeout = 30000; // why was this 30s
> + const timeout = 3000;
> ```
