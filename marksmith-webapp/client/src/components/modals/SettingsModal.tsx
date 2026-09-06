import React from "react";
import { SettingsIcon, CloseIcon } from "../icons/Icons";

export interface SettingsModalProps {
  isOpen: boolean;
  onClose: () => void;
  userId: string;
  documentId: string;
  wsUrl: string;
  apiUrl: string;
}

export const SettingsModal: React.FC<SettingsModalProps> = ({
  isOpen,
  onClose,
  userId,
  documentId,
  wsUrl,
  apiUrl,
}) => {
  if (!isOpen) return null;

  return (
    <div className="ms-modal-backdrop" onClick={onClose}>
      <div className="ms-modal" onClick={(e) => e.stopPropagation()} style={{ width: 440 }}>
        <div className="ms-modal-header">
          <div style={{ display: "flex", alignItems: "center", gap: 8 }}>
            <SettingsIcon size={18} />
            <h3 className="ms-modal-title">Session Settings</h3>
          </div>
          <button className="ms-btn ms-btn-icon ms-btn-sm" onClick={onClose} aria-label="Close">
            <CloseIcon size={14} />
          </button>
        </div>

        <div className="ms-modal-body">
          <div>
            <label style={{ fontSize: 12, fontWeight: 600, color: "var(--ms-text-secondary)", display: "block", marginBottom: 4 }}>
              Document Session ID
            </label>
            <input
              type="text"
              readOnly
              value={documentId}
              style={{
                width: "100%",
                padding: "8px 10px",
                background: "var(--ms-bg-surface)",
                border: "1px solid var(--ms-border)",
                borderRadius: "var(--ms-radius-sm)",
                color: "var(--ms-text-primary)",
                fontFamily: "var(--ms-font-mono)",
                fontSize: 12,
              }}
            />
          </div>

          <div>
            <label style={{ fontSize: 12, fontWeight: 600, color: "var(--ms-text-secondary)", display: "block", marginBottom: 4 }}>
              User ID / Author
            </label>
            <input
              type="text"
              readOnly
              value={userId}
              style={{
                width: "100%",
                padding: "8px 10px",
                background: "var(--ms-bg-surface)",
                border: "1px solid var(--ms-border)",
                borderRadius: "var(--ms-radius-sm)",
                color: "var(--ms-text-primary)",
                fontFamily: "var(--ms-font-mono)",
                fontSize: 12,
              }}
            />
          </div>

          <div>
            <label style={{ fontSize: 12, fontWeight: 600, color: "var(--ms-text-secondary)", display: "block", marginBottom: 4 }}>
              WebSocket Gateway
            </label>
            <input
              type="text"
              readOnly
              value={wsUrl}
              style={{
                width: "100%",
                padding: "8px 10px",
                background: "var(--ms-bg-surface)",
                border: "1px solid var(--ms-border)",
                borderRadius: "var(--ms-radius-sm)",
                color: "var(--ms-text-muted)",
                fontFamily: "var(--ms-font-mono)",
                fontSize: 11,
              }}
            />
          </div>

          <div>
            <label style={{ fontSize: 12, fontWeight: 600, color: "var(--ms-text-secondary)", display: "block", marginBottom: 4 }}>
              REST API Endpoint
            </label>
            <input
              type="text"
              readOnly
              value={apiUrl}
              style={{
                width: "100%",
                padding: "8px 10px",
                background: "var(--ms-bg-surface)",
                border: "1px solid var(--ms-border)",
                borderRadius: "var(--ms-radius-sm)",
                color: "var(--ms-text-muted)",
                fontFamily: "var(--ms-font-mono)",
                fontSize: 11,
              }}
            />
          </div>
        </div>

        <div className="ms-modal-footer">
          <button className="ms-btn ms-btn-primary" onClick={onClose}>
            Done
          </button>
        </div>
      </div>
    </div>
  );
};
