namespace MarkSmith.ViewModels.Mermaid;

/// <summary>
/// A starter diagram the user can load in one click from the Studio's template gallery.
/// Every <see cref="Code"/> value is round-trip-verified against <c>MermaidParser</c> so a
/// template can never load into a broken canvas.
/// </summary>
public sealed record MermaidTemplate(string Name, string Category, string Description, string Code);

/// <summary>
/// The built-in template library — one or more curated starters for each of the seven supported
/// diagram types, mirroring the template galleries of mermaid.live / draw.io.
/// </summary>
public static class MermaidTemplates
{
    public static IReadOnlyList<MermaidTemplate> All { get; } = new List<MermaidTemplate>
    {
        new("Process Flow", "Flowchart", "Start → decision → outcome",
"""
flowchart TD
    A[Start] --> B{Decision}
    B -->|Yes| C[Take Action]
    B -->|No| D[Do Nothing]
    C --> E[End]
    D --> E
"""),

        new("Approval Workflow", "Flowchart", "Submit → review → approve/reject",
"""
flowchart LR
    A[Submit Request] --> B[Manager Review]
    B -->|Approve| C[Process Order]
    B -->|Reject| D[Notify Requester]
    C --> E[Archive]
"""),

        new("API Request / Response", "Sequence", "Client calls a service and gets a reply",
"""
sequenceDiagram
    actor User
    participant App
    participant API
    User->>App: Click Login
    App->>API: POST /auth
    API-->>App: 200 OK + Token
    App-->>User: Welcome
"""),

        new("Order Checkout", "Sequence", "Numbered multi-party checkout flow",
"""
sequenceDiagram
    autonumber
    actor Customer
    participant Store
    participant Payment
    Customer->>Store: Add to Cart
    Store->>Payment: Charge Card
    Payment-->>Store: Payment OK
    Store-->>Customer: Order Confirmed
"""),

        new("Domain Model", "Class", "Classes with members and relationships",
"""
classDiagram
    class Customer {
        +String name
        +String email
        +placeOrder()
    }
    class Order {
        +double total
        +Date placed
    }
    class Item {
        +String sku
        +int qty
    }
    Customer "1" --> "*" Order : places
    Order "1" *-- "*" Item : contains
"""),

        new("Order Lifecycle", "State", "State machine with terminal states",
"""
stateDiagram-v2
    [*] --> Created
    Created --> Paid : payment ok
    Created --> Cancelled : timeout
    Paid --> Shipped : dispatch
    Shipped --> Delivered : arrival
    Delivered --> [*]
    Cancelled --> [*]
"""),

        new("E-commerce Schema", "ER", "Entities, attributes and cardinality",
"""
erDiagram
    CUSTOMER {
        int id PK
        string name
    }
    ORDER {
        int id PK
        double total
    }
    ORDER_ITEM {
        int id PK
        int qty
    }
    PRODUCT {
        int id PK
        double price
    }
    CUSTOMER ||--o{ ORDER : places
    ORDER ||--o{ ORDER_ITEM : contains
    PRODUCT ||--o{ ORDER_ITEM : includes
"""),

        new("Project Plan", "Gantt", "Sections, dependencies and a milestone",
"""
gantt
    title Product Launch Plan
    dateFormat YYYY-MM-DD
    section Discovery
    Research :active, r1, 2026-01-05, 10d
    Requirements :req1, after r1, 7d
    section Build
    Development :dev1, after req1, 20d
    QA :crit, qa1, after dev1, 7d
    section Launch
    Release :milestone, m1, 2026-03-01, 0d
"""),

        new("Brainstorm", "Mindmap", "Radiating idea tree",
"""
mindmap
    Product Idea
        Research
            Market Size
            Competitors
        Design
            Wireframes
            Prototype
        Engineering
            Frontend
            Backend
        Marketing
            Launch Plan
"""),
    };

    /// <summary>Template names grouped by category, preserving library order.</summary>
    public static IEnumerable<IGrouping<string, MermaidTemplate>> ByCategory =>
        All.GroupBy(t => t.Category);
}
