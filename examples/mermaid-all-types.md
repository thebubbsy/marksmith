# Mermaid — Every Diagram Type

A gauntlet of every diagram family Mermaid v11 ships, for comparing the **live preview**, the
**DOCX ShapeForge export**, and the reference descriptions.

Each fence is tagged with the export path Marksmith takes:

- **Native** — a bespoke geometry renderer rebuilds it as editable Word shapes (no browser).
- **Harvest** — mermaid renders it in the browser; Marksmith harvests the SVG primitives and
  rebuilds them as native Word shapes.
- **Snapshot** — falls back to an embedded picture, then a code block.

---

## 1. Flowchart (top-down) — Native

```mermaid
flowchart TD
    A[Start] --> B{Is it working?}
    B -->|Yes| C[Great!]
    B -->|No| D[Debug]
    D --> B
```

## 2. Flowchart (left-right) — Native

```mermaid
flowchart LR
    A([Input]) --> B[Process]
    B --> C{Check}
    C -->|Pass| D[(Database)]
    C -->|Fail| B
```

## 3. Sequence Diagram — Native

```mermaid
sequenceDiagram
    participant U as User
    participant S as Server
    participant DB as Database
    U->>S: Login request
    S->>DB: Query user
    DB-->>S: User record
    S-->>U: Auth token
    Note over U,DB: Session established
```

## 4. Class Diagram — Native

```mermaid
classDiagram
    class Animal {
        +String name
        +int age
        +eat() void
    }
    class Duck {
        +swim() void
        +quack() void
    }
    Animal <|-- Duck
    Animal "1" --> "*" Duck : owns
```

## 5. State Diagram — Harvest

```mermaid
stateDiagram-v2
    [*] --> Idle
    Idle --> Processing : submit
    Processing --> Done : success
    Processing --> Idle : retry
    Done --> [*]
```

## 6. ER Diagram — Native

```mermaid
erDiagram
    CUSTOMER ||--o{ ORDER : places
    ORDER ||--|{ LINE_ITEM : contains
    CUSTOMER {
        int id PK
        string name
        string email UK
    }
    ORDER {
        int id PK
        date placed
        string status
    }
    LINE_ITEM {
        int id PK
        int order_id FK
        int quantity
    }
```

## 7. User Journey — Native

```mermaid
journey
    title My working day
    section Go to work
      Make tea: 5: Me
      Go upstairs: 3: Me
      Do work: 1: Me, Cat
    section Go home
      Go downstairs: 5: Me
      Sit down: 5: Me
```

## 8. Gantt Chart — Native

```mermaid
gantt
    title Project plan
    dateFormat YYYY-MM-DD
    section Design
    Wireframes :a1, 2024-01-01, 7d
    section Build
    Frontend :a2, after a1, 14d
    Backend  :a3, after a1, 21d
    section Ship
    Release  :milestone, after a3, 0d
```

## 9. Pie Chart — Native

```mermaid
pie title Time allocation
    "Coding" : 45
    "Meetings" : 25
    "Review" : 20
    "Other" : 10
```

## 10. Quadrant Chart — Native

```mermaid
quadrantChart
    title Reach and engagement
    x-axis Low Reach --> High Reach
    y-axis Low Engagement --> High Engagement
    quadrant-1 Expand
    quadrant-2 Promote
    quadrant-3 Re-evaluate
    quadrant-4 Improve
    Campaign A: [0.3, 0.6]
    Campaign B: [0.45, 0.23]
    Campaign C: [0.57, 0.69]
    Campaign D: [0.78, 0.34]
```

## 11. Requirement Diagram — Harvest

```mermaid
requirementDiagram
    requirement test_req {
        id: 1
        text: the test text.
        risk: high
        verifymethod: test
    }
    functionalRequirement test_req2 {
        id: 1.1
        text: the second test text.
        risk: low
        verifymethod: inspection
    }
```

## 12. Git Graph — Native

```mermaid
gitGraph
    commit
    commit
    branch develop
    checkout develop
    commit
    commit
    checkout main
    merge develop
    commit
```

## 13. C4 Context — Harvest

```mermaid
C4Context
    title System Context diagram
    Person(customer, "Customer", "A customer of the bank")
    System(banking, "Internet Banking", "Lets customers check accounts")
    System_Ext(mail, "E-mail system", "Sends e-mails")
    Rel(customer, banking, "Uses")
    Rel(banking, mail, "Sends e-mails using")
```

## 14. Mindmap — Native

```mermaid
mindmap
  root((mindmap))
    Origins
      Long history
      Popularisation
    Research
      Effectiveness
    Tools
      Pen and paper
      Software
```

## 15. Timeline — Native

```mermaid
timeline
    title History of Social Media
    2002 : LinkedIn
    2004 : Facebook
    2006 : Twitter
    2010 : Instagram
```

## 16. ZenUML — Harvest

```mermaid
zenuml
    title Demo
    Alice->John: Hello John, how are you?
    John->Alice: Great!
    Alice->John: Did you read the docs?
```

## 17. Sankey — Harvest

```mermaid
sankey-beta

Agricultural 'waste',Bio-conversion,124.729
Bio-conversion,Liquid,0.597
Bio-conversion,Losses,26.862
Bio-conversion,Solid,280.322
Bio-conversion,Gas,81.144
```

## 18. XY Chart — Native

```mermaid
xychart-beta
    title "Sales Revenue"
    x-axis [jan, feb, mar, apr, may, jun]
    y-axis "Revenue ($)" 4000 --> 11000
    bar [5000, 6000, 7500, 8200, 9500, 10500]
    line [5000, 6000, 7500, 8200, 9500, 10500]
```

## 19. Block Diagram — Harvest

```mermaid
block-beta
    columns 1
    db("DB")
    block:app
        A["Service A"]
        B["Service B"]
    end
    client("Client")
    client --> app
    app --> db
```

## 20. Packet — Harvest

```mermaid
packet-beta
    title UDP Packet
    0-15: "Source Port"
    16-31: "Destination Port"
    32-63: "Length"
    64-95: "Checksum"
```

## 21. Kanban — Harvest

```mermaid
kanban
  id1[Todo]
    docs[Create Documentation]
    blog[Write blog post]
  id2[In Progress]
    feature[Build feature]
  id3[Done]
    tests[Write tests]
```

## 22. Architecture — Harvest

```mermaid
architecture-beta
    group api(cloud)[API]

    service db(database)[Database] in api
    service disk1(disk)[Storage] in api
    service server(server)[Server] in api

    db:L -- R:server
    disk1:T -- B:server
```
