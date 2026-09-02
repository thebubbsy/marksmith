import React from "react";
import {
  LogoIcon,
  WordIcon,
  PdfIcon,
  SunIcon,
  MoonIcon,
  SettingsIcon,
  GalaxyIcon,
} from "../icons/Icons";

export interface AppTitleBarProps {
  documentId: string;
  userId: string;
  theme: "dark" | "light";
  onToggleTheme: () => void;
  syncState: "synced" | "syncing" | "offline";
  onExportDocx: () => void;
  onExportPdf: () => void;
  onOpenSettings: () => void;
  onOpenGalaxy: () => void;
  activeUsersCount: number;
}

export const AppTitleBar: React.FC<AppTitleBarProps> = ({
  documentId,
  userId,
  theme,
  onToggleTheme,
  syncState,
  onExportDocx,
  onExportPdf,
  onOpenSettings,
  onOpenGalaxy,
  activeUsersCount,
}) => {
  return (
    <header className="ms-titlebar">
      <div className="ms-titlebar-left">
        <div className="ms-brand">
          <LogoIcon size={26} />
          <span>MarkSmith</span>
          <span className="ms-brand-tagline">AI chats → polished documents</span>
        </div>

        <div className="ms-doc-badge" title={`Active Document: ${documentId} (User: ${userId})`}>
          <span>📄</span>
          <strong>{documentId}</strong>
        </div>

        <div className="ms-status-indicator" title={`Sync status: ${syncState}`}>
          <div
            className={`ms-status-dot ${syncState === "syncing" ? "syncing" : ""}`}
            style={{
              backgroundColor:
                syncState === "synced"
                  ? "var(--ms-success)"
                  : syncState === "syncing"
                  ? "var(--ms-warning)"
                  : "var(--ms-danger)",
            }}
          />
          <span style={{ fontSize: 11, color: "var(--ms-text-muted)" }}>
            {syncState === "synced" ? "Live" : syncState === "syncing" ? "Syncing…" : "Offline"}
          </span>
        </div>
      </div>

      <div className="ms-titlebar-right">
        {activeUsersCount > 0 && (
          <div
            className="ms-btn ms-btn-sm"
            style={{
              backgroundColor: "rgba(56, 239, 125, 0.12)",
              borderColor: "rgba(56, 239, 125, 0.3)",
              color: "#38ef7d",
              cursor: "default",
            }}
            title={`${activeUsersCount} active collaborators in this session`}
          >
            <span style={{ fontSize: 13 }}>👥</span>
            <span>{activeUsersCount} online</span>
          </div>
        )}

        <button className="ms-btn ms-btn-sm" onClick={onExportDocx} title="Export as Microsoft Word (.docx)">
          <WordIcon size={14} />
          <span>Export Word</span>
        </button>

        <button className="ms-btn ms-btn-sm" onClick={onExportPdf} title="Export as PDF (.pdf)">
          <PdfIcon size={14} />
          <span>Export PDF</span>
        </button>

        <div style={{ width: 1, height: 16, backgroundColor: "var(--ms-border)", margin: "0 4px" }} />

        <button className="ms-btn ms-btn-icon ms-btn-sm" onClick={onOpenGalaxy} title="Document Galaxy Map (Ctrl+G)">
          <GalaxyIcon size={14} />
        </button>

        <button
          className="ms-btn ms-btn-icon ms-btn-sm"
          onClick={onToggleTheme}
          title={`Switch to ${theme === "dark" ? "Light" : "Dark"} theme`}
        >
          {theme === "dark" ? <SunIcon size={14} /> : <MoonIcon size={14} />}
        </button>

        <button className="ms-btn ms-btn-icon ms-btn-sm" onClick={onOpenSettings} title="Settings">
          <SettingsIcon size={14} />
        </button>
      </div>
    </header>
  );
};
