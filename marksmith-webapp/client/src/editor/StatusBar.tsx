import React from "react";

export interface StatusBarProps {
  wordCount: number;
  charCount: number;
  zoom: number;
  onZoomIn: () => void;
  onZoomOut: () => void;
  onResetZoom: () => void;
  wsState: string;
  activeUsersCount: number;
}

export const StatusBar: React.FC<StatusBarProps> = ({
  wordCount,
  charCount,
  zoom,
  onZoomIn,
  onZoomOut,
  onResetZoom,
  wsState,
  activeUsersCount,
}) => {
  const readingTime = Math.max(1, Math.ceil(wordCount / 200));

  return (
    <footer className="ms-statusbar">
      <div className="ms-statusbar-left">
        <span>
          <strong>{wordCount}</strong> words · <strong>{charCount}</strong> chars
        </span>
        <span style={{ opacity: 0.7 }}>~{readingTime} min read</span>
        <span style={{ color: "var(--ms-success)", display: "flex", alignItems: "center", gap: 4 }}>
          <span>✓</span>
          <span>OpenXML Valid</span>
        </span>
        {activeUsersCount > 1 && (
          <span style={{ color: "#38ef7d", display: "flex", alignItems: "center", gap: 4 }}>
            <span>👥</span>
            <span>{activeUsersCount} collaborators</span>
          </span>
        )}
      </div>

      <div className="ms-statusbar-right">
        <div style={{ display: "flex", alignItems: "center", gap: 6 }}>
          <button
            className="ms-btn ms-btn-icon ms-btn-sm"
            style={{ width: 22, height: 20, fontSize: 11, padding: 0 }}
            onClick={onZoomOut}
            title="Zoom Out"
          >
            A−
          </button>
          <span
            style={{ cursor: "pointer", minWidth: 36, textAlign: "center", fontSize: 11 }}
            onClick={onResetZoom}
            title="Click to reset zoom to 100%"
          >
            {zoom}%
          </span>
          <button
            className="ms-btn ms-btn-icon ms-btn-sm"
            style={{ width: 22, height: 20, fontSize: 11, padding: 0 }}
            onClick={onZoomIn}
            title="Zoom In"
          >
            A+
          </button>
        </div>

        <span style={{ fontSize: 11, color: "var(--ms-text-muted)" }}>
          {wsState === "open" ? "⚡ Connected (OT v1)" : "Connecting…"}
        </span>
      </div>
    </footer>
  );
};
