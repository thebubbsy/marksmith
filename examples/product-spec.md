---
title: Payments Service — Design Spec
---

# Payments Service — Design Spec

A small, auditable service that authorises card payments and records every state transition.

> [!NOTE]
> This document is illustrative. Amounts and identifiers are examples, not production values.

## Architecture

```mermaid
graph TD
    subgraph Client Tier
        Web["Web Client"]
        Mobile["Mobile App"]
        POS["Point of Sale"]
    end

    subgraph API Gateway
        Gateway["API Gateway / WAF"]
        RateLimit["Rate Limiter"]
    end

    subgraph Payments Core
        API["Payments API"]
        Auth["Auth / Tokenization"]
        Risk["Risk & Fraud Engine"]
        Ledger["Ledger (append-only)"]
    end

    subgraph External Processors
        Stripe["Stripe Processor"]
        PayPal["PayPal Processor"]
        Bank["Direct Bank Transfer"]
    end

    subgraph Data & Async
        DB[("Postgres Master")]
        DB_Replica[("Postgres Read Replica")]
        Cache[("Redis Cache")]
        Queue["Kafka Event Bus"]
        Webhook["Webhook Dispatcher"]
        Merchant["Merchant Callback"]
    end

    %% Client requests
    Web --> Gateway
    Mobile --> Gateway
    POS --> Gateway

    %% Gateway logic
    Gateway --> RateLimit
    RateLimit --> |Allowed| API
    RateLimit --> |Blocked| Drop["Drop Request"]

    %% Core logic
    API --> Cache
    API --> Risk
    Risk --> |Approved| Auth
    Risk --> |Rejected| Decline["Decline Transaction"]
    Auth --> Stripe
    Auth --> PayPal
    Auth --> Bank

    %% State persistence
    Auth --> |Success/Fail| Ledger
    Ledger --> DB
    DB -.-> |Replication| DB_Replica

    %% Async processing
    Ledger --> Queue
    Queue --> Webhook
    Webhook --> Merchant
```

## Request flow

1. The client posts a charge with an idempotency key.
2. The API tokenizes the card and calls the processor.
3. Every transition is appended to the **ledger** — nothing is ever mutated in place.
4. A webhook notifies the merchant asynchronously.

```json
{
  "idempotency_key": "chg_9f2a...",
  "amount": 4200,
  "currency": "AUD",
  "capture": true
}
```

## State machine

| State | Next | Trigger |
| --- | --- | --- |
| `created` | `authorized` | processor approves |
| `authorized` | `captured` | capture requested |
| `authorized` | `voided` | timeout / cancel |
| `captured` | `refunded` | refund issued |

> [!WARNING]
> Idempotency keys expire after 24h. Re-using an expired key starts a **new** charge.
