import React from "react";
import { SparklesIcon, CloseIcon } from "../icons/Icons";

export interface TemplatesModalProps {
  isOpen: boolean;
  onClose: () => void;
  onSelectTemplate: (markdown: string) => void;
}

export const DOCUMENT_TEMPLATES = [
  {
    id: "rfc",
    title: "Technical Architecture RFC",
    desc: "Specification document with problem statement, design options, diagram and rollout plan.",
    icon: "🏗️",
    content: `# Technical RFC: Real-Time Operational Collaboration

## 1. Executive Summary
This document specifies the real-time operational transformation (OT) architecture for synchronous multi-user DOCX editing.

## 2. System Architecture
The system consists of a server-sequenced operation log and optimistic client-side execution.

### Key Components:
- **WebSocket Gateway**: Fast binary and JSON message streaming.
- **OT Transformation Engine**: Multi-user conflict resolution with deterministic convergence.
- **OpenXML Native Serializer**: Direct OOXML mutation without intermediary DOM degradation.

## 3. Implementation Schedule
| Phase | Scope | Status |
|---|---|---|
| Phase 1 | Core OT Engine & WebSockets | Complete |
| Phase 2 | Visual WYSIWYG Editor Surface | In Progress |
| Phase 3 | Native Word/PDF Export Pipeline | Scheduled |
`,
  },
  {
    id: "meeting",
    title: "Executive Meeting Notes",
    desc: "Structured agenda, key discussion points, decisions made, and action items table.",
    icon: "📝",
    content: `# Executive Strategy Meeting Notes
**Date:** September 2, 2026
**Attendees:** Product Team, Engineering Leads, Design Architecture

## Agenda
1. Q3 Roadmap Review & Milestones
2. WebApp Collaboration Launch Readiness
3. Performance Metrics & Latency Goals

## Key Decisions
- Adopt server-side operational transform as the single source of truth for Word documents.
- Deploy real-time presence awareness and suggestion mode across all client applications.

## Action Items
| Owner | Task | Deadline |
|---|---|---|
| Engineering | Finalize OT test coverage | Sept 5 |
| Design | Complete Looking Glass theme system | Sept 4 |
| QA | End-to-end document stress tests | Sept 8 |
`,
  },
  {
    id: "proposal",
    title: "Project Proposal & Brief",
    desc: "Formal business project proposal with background, objectives, budget, and deliverables.",
    icon: "💼",
    content: `# Project Proposal: Enterprise Document Platform

## Background & Objectives
Modern knowledge workers generate extensive documentation through AI workflows that require polished, branded outputs ready for executive presentation.

## Target Deliverables
- **Real-Time Collaboration**: Simultaneous multi-author editing with zero lock contention.
- **Universal Export**: 100% native Word (.docx) and PDF document generation.
- **Enterprise Security**: Token-authenticated sessions with encrypted sidecar persistence.

## Expected ROI
- 85% reduction in document formatting cycle time.
- Instant conversion from conversational AI chats into publication-grade documents.
`,
  },
];

export const TemplatesModal: React.FC<TemplatesModalProps> = ({ isOpen, onClose, onSelectTemplate }) => {
  if (!isOpen) return null;

  return (
    <div className="ms-modal-backdrop" onClick={onClose}>
      <div className="ms-modal" onClick={(e) => e.stopPropagation()} style={{ width: 560 }}>
        <div className="ms-modal-header">
          <div style={{ display: "flex", alignItems: "center", gap: 8 }}>
            <SparklesIcon size={18} />
            <h3 className="ms-modal-title">Choose a Document Blueprint</h3>
          </div>
          <button className="ms-btn ms-btn-icon ms-btn-sm" onClick={onClose} aria-label="Close">
            <CloseIcon size={14} />
          </button>
        </div>

        <div className="ms-modal-body">
          <p style={{ fontSize: 13, color: "var(--ms-text-muted)" }}>
            Select a starter blueprint to populate your document with rich sections, tables, and formatting:
          </p>

          <div style={{ display: "flex", flexDirection: "column", gap: 10 }}>
            {DOCUMENT_TEMPLATES.map((tmpl) => (
              <div
                key={tmpl.id}
                className="ms-source-card"
                style={{
                  cursor: "pointer",
                  display: "flex",
                  alignItems: "flex-start",
                  gap: 12,
                  transition: "all 0.15s ease",
                }}
                onClick={() => {
                  onSelectTemplate(tmpl.content);
                  onClose();
                }}
              >
                <div style={{ fontSize: 24 }}>{tmpl.icon}</div>
                <div style={{ flex: 1 }}>
                  <h4 style={{ fontSize: 13, color: "var(--ms-text-primary)", marginBottom: 2 }}>{tmpl.title}</h4>
                  <p style={{ fontSize: 12, color: "var(--ms-text-secondary)" }}>{tmpl.desc}</p>
                </div>
                <button className="ms-btn ms-btn-primary ms-btn-sm" style={{ alignSelf: "center" }}>
                  Use
                </button>
              </div>
            ))}
          </div>
        </div>

        <div className="ms-modal-footer">
          <button className="ms-btn" onClick={onClose}>
            Cancel
          </button>
        </div>
      </div>
    </div>
  );
};
