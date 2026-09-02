import React, { useState } from "react";
import { SparklesIcon } from "../icons/Icons";

export interface LeftSourcePaneProps {
  isCollapsed: boolean;
  onToggleCollapse: () => void;
  onInsertMarkdown: (text: string) => void;
  onOpenTemplates: () => void;
}

export const LeftSourcePane: React.FC<LeftSourcePaneProps> = ({
  isCollapsed,
  onToggleCollapse,
  onInsertMarkdown,
  onOpenTemplates,
}) => {
  const [chatInput, setChatInput] = useState("");
  const [copiedSuccess, setCopiedSuccess] = useState(false);

  const handleIngestChat = () => {
    if (!chatInput.trim()) return;
    onInsertMarkdown(chatInput);
    setChatInput("");
    setCopiedSuccess(true);
    setTimeout(() => setCopiedSuccess(false), 2000);
  };

  if (isCollapsed) {
    return (
      <aside className="ms-pane-left collapsed">
        <div style={{ padding: "12px 6px", display: "flex", flexDirection: "column", alignItems: "center", gap: 12 }}>
          <button
            className="ms-step-badge"
            onClick={onToggleCollapse}
            title="Step 1: Source (Click to expand)"
            style={{ cursor: "pointer" }}
          >
            1
          </button>
          <div
            style={{
              writingMode: "vertical-rl",
              transform: "rotate(180deg)",
              fontSize: 12,
              fontWeight: 600,
              color: "var(--ms-text-muted)",
              letterSpacing: "0.1em",
              cursor: "pointer",
            }}
            onClick={onToggleCollapse}
          >
            SOURCE & INGEST
          </div>
        </div>
      </aside>
    );
  }

  return (
    <aside className="ms-pane-left">
      <div className="ms-pane-header">
        <div className="ms-step-info">
          <div className="ms-step-badge">1</div>
          <div>
            <div className="ms-step-title">Source</div>
            <div className="ms-step-caption">Pick or paste AI content</div>
          </div>
        </div>
        <button
          className="ms-btn ms-btn-icon ms-btn-sm"
          onClick={onToggleCollapse}
          title="Collapse Source Pane"
          style={{ width: 24, height: 24 }}
        >
          ◀
        </button>
      </div>

      <div className="ms-pane-body">
        {/* AI Chat Ingestion */}
        <div className="ms-source-card">
          <div style={{ display: "flex", alignItems: "center", justifyContent: "space-between", marginBottom: 6 }}>
            <h4>AI Chat Ingest</h4>
            <span style={{ fontSize: 11, color: "var(--ms-accent)" }}>⚡ Fast Ingest</span>
          </div>
          <p style={{ fontSize: 11, color: "var(--ms-text-muted)", marginBottom: 8 }}>
            Paste a reply from ChatGPT, Gemini, or Claude to clean and insert into the document:
          </p>
          <textarea
            className="ms-chat-ingest-box"
            placeholder="Paste raw AI response here…"
            value={chatInput}
            onChange={(e) => setChatInput(e.target.value)}
          />
          <button
            className="ms-btn ms-btn-primary ms-btn-sm"
            style={{ width: "100%", marginTop: 8 }}
            onClick={handleIngestChat}
            disabled={!chatInput.trim()}
          >
            <SparklesIcon size={13} />
            <span>{copiedSuccess ? "Inserted!" : "Clean & Ingest to Doc"}</span>
          </button>
        </div>

        {/* Templates */}
        <div className="ms-source-card">
          <h4>Starter Templates</h4>
          <p style={{ fontSize: 11, color: "var(--ms-text-muted)", marginBottom: 8 }}>
            Launch standard document blueprints:
          </p>
          <div style={{ display: "flex", flexDirection: "column", gap: 6 }}>
            <button
              className="ms-btn ms-btn-sm"
              style={{ justifyContent: "flex-start" }}
              onClick={onOpenTemplates}
            >
              <span>📋</span>
              <span>Browse Templates…</span>
            </button>
          </div>
        </div>

        {/* File Drop Info */}
        <div
          style={{
            marginTop: "auto",
            padding: 12,
            backgroundColor: "var(--ms-bg-surface)",
            borderRadius: "var(--ms-radius-md)",
            border: "1px dashed var(--ms-border)",
            textAlign: "center",
          }}
        >
          <div style={{ fontSize: 20, marginBottom: 4 }}>📥</div>
          <div style={{ fontSize: 12, fontWeight: 600, color: "var(--ms-text-primary)" }}>Drop .md or .docx here</div>
          <div style={{ fontSize: 11, color: "var(--ms-text-muted)", marginTop: 2 }}>
            Instant parsing & live conversion
          </div>
        </div>
      </div>
    </aside>
  );
};
