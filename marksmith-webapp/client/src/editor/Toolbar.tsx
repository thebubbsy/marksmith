import React, { useState, useRef, useEffect } from "react";
import type { CollabClient } from "../collab/CollabClient";
import type { Operation } from "../collab/protocol";
import { blocks, rangeToPosition, blockText } from "./positions";
import {
  BoldIcon,
  ItalicIcon,
  UnderlineIcon,
  StrikeIcon,
  HeadingIcon,
  ListBulletIcon,
  ListNumberIcon,
  ListTaskIcon,
  QuoteIcon,
  TableIcon,
  ImageIcon,
  LinkIcon,
  CommentIcon,
  UndoIcon,
  RedoIcon,
  SyncIcon,
  ChevronDownIcon,
  DiagramIcon,
  SmartArtIcon,
  CodeIcon,
} from "../components/icons/Icons";

export interface ToolbarProps {
  collab: CollabClient;
  getRoot: () => HTMLElement | null;
  suggestionMode: boolean;
  onToggleSuggestion: () => void;
  onUndo: () => void;
  onResync: () => void;
  onOpenInsertTableModal?: () => void;
}

let opCounter = 1000;
function op(type: Operation["type"]): Operation {
  opCounter++;
  return { id: `tb-${Date.now().toString(36)}-${opCounter}`, clientId: "", type };
}

export const Toolbar: React.FC<ToolbarProps> = (props) => {
  const { collab, getRoot, suggestionMode, onToggleSuggestion, onUndo, onResync, onOpenInsertTableModal } = props;

  const [activeDropdown, setActiveDropdown] = useState<"style" | "lists" | "insert" | "tools" | null>(null);
  const toolbarRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    const handleClickOutside = (e: MouseEvent) => {
      if (toolbarRef.current && !toolbarRef.current.contains(e.target as Node)) {
        setActiveDropdown(null);
      }
    };
    document.addEventListener("mousedown", handleClickOutside);
    return () => document.removeEventListener("mousedown", handleClickOutside);
  }, []);

  const toggleDropdown = (name: "style" | "lists" | "insert" | "tools") => {
    setActiveDropdown((prev) => (prev === name ? null : name));
  };

  const caretBlock = (): number => {
    const r = getRoot();
    if (!r) return 0;
    const sel = window.getSelection();
    if (!sel || sel.rangeCount === 0) return 0;
    return rangeToPosition(r, sel.getRangeAt(0), true)?.block ?? 0;
  };

  const selectedRange = (): { block: number; start: number; end: number } | null => {
    const r = getRoot();
    if (!r) return null;
    const sel = window.getSelection();
    if (!sel || sel.rangeCount === 0 || sel.isCollapsed) return null;
    const range = sel.getRangeAt(0);
    const start = rangeToPosition(r, range, true);
    const end = rangeToPosition(r, range, false);
    if (!start || !end) return null;
    if (start.block !== end.block) return null;
    return { block: start.block, start: start.offset, end: end.offset };
  };

  const submit = (o: Operation) => collab.submit(o);

  const applyStyle = (style: string) => {
    const block = caretBlock();
    const o = op("insertParagraph");
    o.block = block;
    o.style = style;
    submit(o);
    setActiveDropdown(null);
  };

  const applyFormat = (partial: Partial<NonNullable<Operation["format"]>>) => {
    const sel = selectedRange();
    if (!sel) return;
    const o = op("applyFormatting");
    o.block = sel.block;
    o.offset = sel.start;
    o.length = sel.end - sel.start;
    o.format = partial;
    submit(o);
  };

  const insertTable = (rows = 3, cols = 3) => {
    const o = op("insertTable");
    o.block = caretBlock() + 1;
    o.rows = rows;
    o.cols = cols;
    submit(o);
    setActiveDropdown(null);
  };

  const pickImage = () => {
    const input = document.createElement("input");
    input.type = "file";
    input.accept = "image/png,image/jpeg,image/gif";
    input.onchange = () => {
      const file = input.files?.[0];
      if (!file) return;
      const reader = new FileReader();
      reader.onload = () => {
        const dataUri = String(reader.result ?? "");
        const o = op("insertImage");
        o.block = caretBlock();
        o.offset = 0;
        o.alt = file.name;
        o.dataUri = dataUri;
        o.width = 240;
        o.height = 180;
        submit(o);
        collab.resync();
      };
      reader.readAsDataURL(file);
    };
    input.click();
    setActiveDropdown(null);
  };

  const insertLink = () => {
    const url = window.prompt("Link URL (https://…)");
    if (!url) return;
    const sel = selectedRange();
    if (!sel) return;
    const text = blockTextForRange(sel);
    const o = op("insertHyperlink");
    o.block = sel.block;
    o.offset = sel.start;
    o.text = text;
    o.href = url;
    submit(o);
    collab.resync();
    setActiveDropdown(null);
  };

  const blockTextForRange = (sel: { block: number; start: number; end: number }): string => {
    const r = getRoot();
    if (!r) return "link";
    const bl = blocks(r);
    const el = bl[sel.block];
    if (!el) return "link";
    const full = blockText(el);
    return full.slice(sel.start, sel.end) || "link";
  };

  const addComment = () => {
    const sel = selectedRange();
    const text = window.prompt("Comment text");
    if (!text) return;
    const o = op("addComment");
    if (sel) {
      o.block = sel.block;
      o.offset = sel.start;
      o.length = sel.end - sel.start;
    } else {
      o.block = caretBlock();
      o.offset = 0;
      o.length = 1;
    }
    o.text = text;
    submit(o);
    setActiveDropdown(null);
  };

  return (
    <div className="ms-ribbon" role="toolbar" aria-label="Marksmith Ribbon Toolbar" ref={toolbarRef}>
      {/* Cluster 1: Text Style */}
      <div className="ms-ribbon-group" style={{ position: "relative" }}>
        <button
          className={`ms-ribbon-btn ${activeDropdown === "style" ? "active" : ""}`}
          onClick={() => toggleDropdown("style")}
          title="Paragraph Style (Headings / Normal)"
        >
          <HeadingIcon size={14} />
          <span>Style</span>
          <ChevronDownIcon size={11} />
        </button>
        {activeDropdown === "style" && (
          <div className="ms-dropdown-menu">
            <button className="ms-dropdown-item" onClick={() => applyStyle("Normal")}>
              <span>Normal</span>
              <span style={{ fontSize: 10, opacity: 0.6 }}>Text</span>
            </button>
            <button className="ms-dropdown-item" onClick={() => applyStyle("Heading1")}>
              <strong style={{ fontSize: 15 }}>Heading 1</strong>
              <span style={{ fontSize: 10, opacity: 0.6 }}># H1</span>
            </button>
            <button className="ms-dropdown-item" onClick={() => applyStyle("Heading2")}>
              <strong style={{ fontSize: 14 }}>Heading 2</strong>
              <span style={{ fontSize: 10, opacity: 0.6 }}>## H2</span>
            </button>
            <button className="ms-dropdown-item" onClick={() => applyStyle("Heading3")}>
              <strong style={{ fontSize: 13 }}>Heading 3</strong>
              <span style={{ fontSize: 10, opacity: 0.6 }}>### H3</span>
            </button>
            <button className="ms-dropdown-item" onClick={() => applyStyle("Heading4")}>
              <span>Heading 4</span>
              <span style={{ fontSize: 10, opacity: 0.6 }}>#### H4</span>
            </button>
          </div>
        )}
      </div>

      <div className="ms-ribbon-sep" />

      {/* Basic Formatting Group */}
      <div className="ms-ribbon-group">
        <button className="ms-ribbon-btn" title="Bold (Ctrl+B)" onClick={() => applyFormat({ bold: true })}>
          <BoldIcon size={13} />
        </button>
        <button className="ms-ribbon-btn" title="Italic (Ctrl+I)" onClick={() => applyFormat({ italic: true })}>
          <ItalicIcon size={13} />
        </button>
        <button className="ms-ribbon-btn" title="Underline (Ctrl+U)" onClick={() => applyFormat({ underline: true })}>
          <UnderlineIcon size={13} />
        </button>
        <button className="ms-ribbon-btn" title="Strikethrough" onClick={() => applyFormat({ strikethrough: true })}>
          <StrikeIcon size={13} />
        </button>
        <input
          type="color"
          title="Text Color"
          defaultValue="#111111"
          style={{ width: 22, height: 22, border: "none", background: "none", cursor: "pointer", marginLeft: 2 }}
          onChange={(e) => applyFormat({ color: e.target.value })}
        />
      </div>

      <div className="ms-ribbon-sep" />

      {/* Cluster 2: Lists */}
      <div className="ms-ribbon-group" style={{ position: "relative" }}>
        <button
          className={`ms-ribbon-btn ${activeDropdown === "lists" ? "active" : ""}`}
          onClick={() => toggleDropdown("lists")}
          title="Lists & Quotations"
        >
          <ListBulletIcon size={14} />
          <span>Lists</span>
          <ChevronDownIcon size={11} />
        </button>
        {activeDropdown === "lists" && (
          <div className="ms-dropdown-menu">
            <button className="ms-dropdown-item" onClick={() => applyStyle("ListBullet")}>
              <div style={{ display: "flex", alignItems: "center", gap: 6 }}>
                <ListBulletIcon size={13} />
                <span>Bullet List</span>
              </div>
            </button>
            <button className="ms-dropdown-item" onClick={() => applyStyle("ListNumber")}>
              <div style={{ display: "flex", alignItems: "center", gap: 6 }}>
                <ListNumberIcon size={13} />
                <span>Numbered List</span>
              </div>
            </button>
            <button className="ms-dropdown-item" onClick={() => applyStyle("ListBullet")}>
              <div style={{ display: "flex", alignItems: "center", gap: 6 }}>
                <ListTaskIcon size={13} />
                <span>Task List</span>
              </div>
            </button>
            <div style={{ height: 1, backgroundColor: "var(--ms-border)", margin: "3px 0" }} />
            <button className="ms-dropdown-item" onClick={() => applyStyle("Quote")}>
              <div style={{ display: "flex", alignItems: "center", gap: 6 }}>
                <QuoteIcon size={13} />
                <span>Blockquote</span>
              </div>
            </button>
          </div>
        )}
      </div>

      <div className="ms-ribbon-sep" />

      {/* Cluster 3: Insert Components */}
      <div className="ms-ribbon-group" style={{ position: "relative" }}>
        <button
          className={`ms-ribbon-btn ${activeDropdown === "insert" ? "active" : ""}`}
          onClick={() => toggleDropdown("insert")}
          title="Insert Tables, Images, Diagrams, SmartArt"
        >
          <span style={{ fontWeight: 700, color: "var(--ms-accent)", fontSize: 14 }}>+</span>
          <span>Insert</span>
          <ChevronDownIcon size={11} />
        </button>
        {activeDropdown === "insert" && (
          <div className="ms-dropdown-menu" style={{ minWidth: 200 }}>
            <button className="ms-dropdown-item" onClick={() => onOpenInsertTableModal ? onOpenInsertTableModal() : insertTable(3, 3)}>
              <div style={{ display: "flex", alignItems: "center", gap: 8 }}>
                <TableIcon size={14} />
                <span>Table…</span>
              </div>
            </button>
            <button className="ms-dropdown-item" onClick={pickImage}>
              <div style={{ display: "flex", alignItems: "center", gap: 8 }}>
                <ImageIcon size={14} />
                <span>Image…</span>
              </div>
            </button>
            <button className="ms-dropdown-item" onClick={insertLink}>
              <div style={{ display: "flex", alignItems: "center", gap: 8 }}>
                <LinkIcon size={14} />
                <span>Hyperlink</span>
              </div>
            </button>
            <div style={{ height: 1, backgroundColor: "var(--ms-border)", margin: "3px 0" }} />
            <button className="ms-dropdown-item" onClick={() => applyStyle("CodeBlock")}>
              <div style={{ display: "flex", alignItems: "center", gap: 8 }}>
                <CodeIcon size={14} />
                <span>Code Block</span>
              </div>
            </button>
            <button className="ms-dropdown-item" onClick={() => applyStyle("SmartArt")}>
              <div style={{ display: "flex", alignItems: "center", gap: 8 }}>
                <SmartArtIcon size={14} />
                <span>SmartArt Component</span>
              </div>
            </button>
            <button className="ms-dropdown-item" onClick={() => applyStyle("Mermaid")}>
              <div style={{ display: "flex", alignItems: "center", gap: 8 }}>
                <DiagramIcon size={14} />
                <span>Mermaid Diagram</span>
              </div>
            </button>
          </div>
        )}
      </div>

      <div className="ms-ribbon-sep" />

      {/* Collaboration / Review Group */}
      <div className="ms-ribbon-group">
        <button className="ms-ribbon-btn" title="Add Comment (selected text)" onClick={addComment}>
          <CommentIcon size={13} />
          <span>Comment</span>
        </button>
        <button
          className={`ms-ribbon-btn ${suggestionMode ? "active" : ""}`}
          title="Track Changes / Suggestion Mode"
          onClick={onToggleSuggestion}
        >
          <span>✍️</span>
          <span>Suggesting</span>
        </button>
      </div>

      <div className="ms-ribbon-sep" />

      {/* Undo / Redo / Resync */}
      <div className="ms-ribbon-group" style={{ marginLeft: "auto" }}>
        <button className="ms-ribbon-btn" title="Undo (Ctrl+Z)" onClick={onUndo}>
          <UndoIcon size={13} />
        </button>
        <button className="ms-ribbon-btn" title="Redo" onClick={onResync}>
          <RedoIcon size={13} />
        </button>
        <button className="ms-ribbon-btn" title="Resync from Server (Fetch Authoritative OOXML)" onClick={onResync}>
          <SyncIcon size={13} />
        </button>
      </div>
    </div>
  );
};
