import React, { useState } from "react";
import { PaletteIcon, OutlineIcon, CommentIcon, WordIcon, PdfIcon, CheckIcon } from "../icons/Icons";

export interface TocHeading {
  id: string;
  level: number;
  text: string;
}

export interface RightStylePaneProps {
  selectedTheme: string;
  onSelectTheme: (themeName: string) => void;
  headings: TocHeading[];
  onHeadingClick: (id: string) => void;
  commentsCount: number;
  changesCount: number;
  onExportDocx: () => void;
  onExportPdf: () => void;
  fontFamily: string;
  onChangeFontFamily: (font: string) => void;
}

export const THEME_PRESETS = [
  { id: "slate", name: "Nordic Slate", primary: "#11998e", secondary: "#38ef7d", bg: "#181b24" },
  { id: "fluent", name: "Fluent Modern", primary: "#0b63ce", secondary: "#4c9aff", bg: "#ffffff" },
  { id: "emerald", name: "Emerald Executive", primary: "#065f46", secondary: "#10b981", bg: "#064e3b" },
  { id: "midnight", name: "Midnight Cobalt", primary: "#1e3a8a", secondary: "#3b82f6", bg: "#0f172a" },
  { id: "cyberpunk", name: "Cyberpunk Amber", primary: "#f59e0b", secondary: "#ef4444", bg: "#18181b" },
  { id: "obsidian", name: "Obsidian Pro", primary: "#6366f1", secondary: "#a855f7", bg: "#09090b" },
];

export const RightStylePane: React.FC<RightStylePaneProps> = ({
  selectedTheme,
  onSelectTheme,
  headings,
  onHeadingClick,
  commentsCount,
  changesCount,
  onExportDocx,
  onExportPdf,
  fontFamily,
  onChangeFontFamily,
}) => {
  const [activeTab, setActiveTab] = useState<"appearance" | "outline" | "revisions">("appearance");

  return (
    <aside className="ms-pane-right">
      <div className="ms-pane-header">
        <div className="ms-step-info">
          <div className="ms-step-badge">3</div>
          <div>
            <div className="ms-step-title">Style & Export</div>
            <div className="ms-step-caption">Themes, TOC & Revisions</div>
          </div>
        </div>
      </div>

      <div className="ms-tabs-bar">
        <button
          className={`ms-tab-btn ${activeTab === "appearance" ? "active" : ""}`}
          onClick={() => setActiveTab("appearance")}
        >
          <div style={{ display: "flex", alignItems: "center", justifyContent: "center", gap: 5 }}>
            <PaletteIcon size={14} />
            <span>Style</span>
          </div>
        </button>
        <button
          className={`ms-tab-btn ${activeTab === "outline" ? "active" : ""}`}
          onClick={() => setActiveTab("outline")}
        >
          <div style={{ display: "flex", alignItems: "center", justifyContent: "center", gap: 5 }}>
            <OutlineIcon size={14} />
            <span>Outline ({headings.length})</span>
          </div>
        </button>
        <button
          className={`ms-tab-btn ${activeTab === "revisions" ? "active" : ""}`}
          onClick={() => setActiveTab("revisions")}
        >
          <div style={{ display: "flex", alignItems: "center", justifyContent: "center", gap: 5 }}>
            <CommentIcon size={14} />
            <span>Review ({commentsCount + changesCount})</span>
          </div>
        </button>
      </div>

      <div className="ms-pane-body">
        {activeTab === "appearance" && (
          <>
            <div className="ms-source-card">
              <h4>Document Theme</h4>
              <div className="ms-theme-grid">
                {THEME_PRESETS.map((t) => (
                  <div
                    key={t.id}
                    className={`ms-theme-card ${selectedTheme === t.id ? "active" : ""}`}
                    onClick={() => onSelectTheme(t.id)}
                  >
                    <div style={{ display: "flex", alignItems: "center", justifyContent: "space-between" }}>
                      <span style={{ fontSize: 11, fontWeight: 600 }}>{t.name}</span>
                      {selectedTheme === t.id && <CheckIcon size={12} color="var(--ms-accent)" />}
                    </div>
                    <div className="ms-theme-preview-dots">
                      <div className="ms-theme-dot" style={{ backgroundColor: t.primary }} />
                      <div className="ms-theme-dot" style={{ backgroundColor: t.secondary }} />
                      <div className="ms-theme-dot" style={{ backgroundColor: t.bg, border: "1px solid #444" }} />
                    </div>
                  </div>
                ))}
              </div>
            </div>

            <div className="ms-source-card">
              <h4>Typography</h4>
              <label style={{ fontSize: 11, color: "var(--ms-text-muted)", display: "block", marginBottom: 4 }}>
                Font Pairing:
              </label>
              <select
                style={{
                  width: "100%",
                  padding: "6px 8px",
                  borderRadius: "var(--ms-radius-sm)",
                  background: "var(--ms-bg-card)",
                  color: "var(--ms-text-primary)",
                  border: "1px solid var(--ms-border)",
                  fontSize: 12,
                }}
                value={fontFamily}
                onChange={(e) => onChangeFontFamily(e.target.value)}
              >
                <option value="sans">Modern Sans (Segoe UI / Inter)</option>
                <option value="serif">Editorial Serif (Merriweather / Georgia)</option>
                <option value="mono">Technical Mono (Cascadia / JetBrains)</option>
              </select>
            </div>

            <div className="ms-source-card">
              <h4>Export Outputs</h4>
              <div style={{ display: "flex", flexDirection: "column", gap: 8 }}>
                <button className="ms-btn ms-btn-primary" onClick={onExportDocx}>
                  <WordIcon size={15} />
                  <span>Download Word Document (.docx)</span>
                </button>
                <button className="ms-btn" onClick={onExportPdf}>
                  <PdfIcon size={15} />
                  <span>Download PDF Document (.pdf)</span>
                </button>
              </div>
            </div>
          </>
        )}

        {activeTab === "outline" && (
          <div className="ms-source-card" style={{ flex: 1 }}>
            <h4>Table of Contents</h4>
            {headings.length === 0 ? (
              <p style={{ fontSize: 12, color: "var(--ms-text-muted)", fontStyle: "italic", marginTop: 8 }}>
                No headings found in the document. Add Headings (H1, H2, H3) to see the outline.
              </p>
            ) : (
              <ul className="ms-outline-list" style={{ marginTop: 8 }}>
                {headings.map((h, i) => (
                  <li
                    key={`${h.id}-${i}`}
                    className={`ms-outline-item h${Math.min(h.level, 3)}`}
                    onClick={() => onHeadingClick(h.id)}
                    title={h.text}
                  >
                    {h.text}
                  </li>
                ))}
              </ul>
            )}
          </div>
        )}

        {activeTab === "revisions" && (
          <div className="ms-source-card" style={{ flex: 1 }}>
            <h4>Discussion & Changes</h4>
            <div style={{ display: "flex", flexDirection: "column", gap: 8, marginTop: 8 }}>
              <div style={{ fontSize: 12, color: "var(--ms-text-secondary)" }}>
                💬 <strong>{commentsCount}</strong> Comments
              </div>
              <div style={{ fontSize: 12, color: "var(--ms-text-secondary)" }}>
                📝 <strong>{changesCount}</strong> Tracked Changes
              </div>
              <p style={{ fontSize: 11, color: "var(--ms-text-muted)", marginTop: 6 }}>
                Highlight text in the editor and click "Comment" or turn on "Suggestions Mode" in the ribbon to track edits.
              </p>
            </div>
          </div>
        )}
      </div>
    </aside>
  );
};
