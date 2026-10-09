## 2026-08-18 - [Add Keyboard Focus Outlines]
**Learning:** Custom UI elements like toggle switches that use hidden native inputs (`opacity: 0`) require explicit `:focus-visible` handling on sibling visual elements to remain accessible for keyboard users.
**Action:** Always check for `:focus-visible` support when replacing native interactive elements with custom designs.
