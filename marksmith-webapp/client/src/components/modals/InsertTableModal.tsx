import React, { useState } from "react";
import { TableIcon, CloseIcon } from "../icons/Icons";

export interface InsertTableModalProps {
  isOpen: boolean;
  onClose: () => void;
  onInsert: (rows: number, cols: number) => void;
}

export const InsertTableModal: React.FC<InsertTableModalProps> = ({ isOpen, onClose, onInsert }) => {
  const [hoveredRows, setHoveredRows] = useState(3);
  const [hoveredCols, setHoveredCols] = useState(3);

  if (!isOpen) return null;

  return (
    <div className="ms-modal-backdrop" onClick={onClose}>
      <div className="ms-modal" onClick={(e) => e.stopPropagation()} style={{ width: 360 }}>
        <div className="ms-modal-header">
          <div style={{ display: "flex", alignItems: "center", gap: 8 }}>
            <TableIcon size={18} />
            <h3 className="ms-modal-title">Insert Table</h3>
          </div>
          <button className="ms-btn ms-btn-icon ms-btn-sm" onClick={onClose} aria-label="Close">
            <CloseIcon size={14} />
          </button>
        </div>

        <div className="ms-modal-body" style={{ alignItems: "center", textAlign: "center" }}>
          <div style={{ fontSize: 13, fontWeight: 600, color: "var(--ms-text-primary)", marginBottom: 4 }}>
            {hoveredRows} rows × {hoveredCols} columns
          </div>

          <div
            style={{
              display: "grid",
              gridTemplateColumns: "repeat(8, 24px)",
              gap: 4,
              padding: 12,
              backgroundColor: "var(--ms-bg-surface)",
              borderRadius: "var(--ms-radius-md)",
              border: "1px solid var(--ms-border)",
            }}
          >
            {Array.from({ length: 8 }).map((_, r) =>
              Array.from({ length: 8 }).map((_, c) => {
                const isHighlighted = r < hoveredRows && c < hoveredCols;
                return (
                  <div
                    key={`${r}-${c}`}
                    style={{
                      width: 24,
                      height: 24,
                      borderRadius: 3,
                      border: "1px solid",
                      borderColor: isHighlighted ? "var(--ms-accent)" : "var(--ms-border)",
                      backgroundColor: isHighlighted ? "rgba(17, 153, 142, 0.25)" : "var(--ms-bg-card)",
                      cursor: "pointer",
                      transition: "all 0.05s ease",
                    }}
                    onMouseEnter={() => {
                      setHoveredRows(r + 1);
                      setHoveredCols(c + 1);
                    }}
                    onClick={() => {
                      onInsert(r + 1, c + 1);
                      onClose();
                    }}
                  />
                );
              }),
            )}
          </div>
        </div>

        <div className="ms-modal-footer">
          <button className="ms-btn" onClick={onClose}>
            Cancel
          </button>
          <button
            className="ms-btn ms-btn-primary"
            onClick={() => {
              onInsert(hoveredRows, hoveredCols);
              onClose();
            }}
          >
            Insert {hoveredRows}×{hoveredCols} Table
          </button>
        </div>
      </div>
    </div>
  );
};
