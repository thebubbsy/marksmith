## 2024-07-31 - Accessibility of decorative elements
**Learning:** Found decorative SVGs and emoji icons used as illustrations next to text headings in the landing page (`website/index.html`). Screen readers announce these explicitly if not hidden, creating noise.
**Action:** Always add `aria-hidden="true"` to decorative icons (SVGs, emojis, symbol characters) that are purely visual alongside descriptive text.
