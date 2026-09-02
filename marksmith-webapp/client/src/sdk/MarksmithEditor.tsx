import { useCallback, useEffect, useRef, useState } from "react";
import { CollabClient } from "../collab/CollabClient";
import type { WsState } from "../collab/WsClient";
import type { CaretPosition, SelectionSpan, Operation } from "../collab/protocol";
import { EditorSurface, PresenceRail } from "../editor/EditorSurface";
import { Toolbar } from "../editor/Toolbar";
import { StatusBar } from "../editor/StatusBar";
import { AppTitleBar } from "../components/layout/AppTitleBar";
import { LeftSourcePane } from "../components/layout/LeftSourcePane";
import { RightStylePane, type TocHeading } from "../components/layout/RightStylePane";
import { InsertTableModal } from "../components/modals/InsertTableModal";
import { TemplatesModal } from "../components/modals/TemplatesModal";
import { SettingsModal } from "../components/modals/SettingsModal";
import type { CommentEntry } from "../editor/CommentsPanel";
import type { TrackChangeEntry } from "../editor/TrackChangesPanel";
import { colorFor, type PresencePeer } from "../editor/presence";

export interface MarksmithEditorProps {
  wsUrl: string;
  apiUrl: string;
  documentId: string;
  userId: string;
  token?: string;
  suggestionMode?: boolean;
  showToolbar?: boolean;
  showPanels?: boolean;
  onReady?: (info: { sessionId: string; seq: number; clientId: string }) => void;
  onError?: (code: string, message: string) => void;
}

export function MarksmithEditor(props: MarksmithEditorProps) {
  const {
    wsUrl,
    apiUrl,
    documentId,
    userId,
    suggestionMode = false,
    showToolbar = true,
    showPanels = true,
    onReady,
    onError,
  } = props;

  const collabRef = useRef<CollabClient | null>(null);
  const [collab, setCollab] = useState<CollabClient | null>(null);
  const [authToken, setAuthToken] = useState<string>(props.token ?? "");
  const rootRef = useRef<HTMLDivElement>(null);
  const [wsState, setWsState] = useState<WsState>("connecting");
  const [html, setHtml] = useState("");
  const [suggestion, setSuggestion] = useState(suggestionMode);
  const [comments, setComments] = useState<CommentEntry[]>([]);
  const [changes, setChanges] = useState<TrackChangeEntry[]>([]);
  const [peers, setPeers] = useState<PresencePeer[]>([]);
  const peersRef = useRef<Map<string, PresencePeer>>(new Map());

  // UI state
  const [theme, setTheme] = useState<"dark" | "light">("dark");
  const [selectedThemePreset, setSelectedThemePreset] = useState("slate");
  const [fontFamily, setFontFamily] = useState("sans");
  const [zoom, setZoom] = useState(100);
  const [isLeftCollapsed, setIsLeftCollapsed] = useState(false);
  const [headings, setHeadings] = useState<TocHeading[]>([]);
  const [wordCount, setWordCount] = useState(0);
  const [charCount, setCharCount] = useState(0);

  // Modals
  const [isTableModalOpen, setIsTableModalOpen] = useState(false);
  const [isTemplatesModalOpen, setIsTemplatesModalOpen] = useState(false);
  const [isSettingsModalOpen, setIsSettingsModalOpen] = useState(false);

  // Sync theme to root data-theme attribute
  useEffect(() => {
    document.documentElement.setAttribute("data-theme", theme);
  }, [theme]);

  const toggleTheme = () => {
    setTheme((t) => (t === "dark" ? "light" : "dark"));
  };

  /** Extracts document statistics, headings, comments, and track changes from the HTML */
  const extractDocumentMetadata = useCallback((h: string) => {
    const tmp = document.createElement("div");
    tmp.innerHTML = h;

    // Text & Stats
    const text = tmp.textContent || "";
    setCharCount(text.length);
    const words = text.trim().split(/\s+/).filter(Boolean);
    setWordCount(words.length);

    // Headings for TOC
    const headingEls = Array.from(tmp.querySelectorAll("h1, h2, h3, h4, h5, h6"));
    const tocList: TocHeading[] = headingEls.map((el, i) => {
      const level = parseInt(el.tagName.replace("H", ""), 10) || 1;
      const headingId = `heading-${i}`;
      el.id = headingId;
      return {
        id: headingId,
        level,
        text: el.textContent ?? `Heading ${i + 1}`,
      };
    });
    setHeadings(tocList);

    // Comments
    const commentEls = Array.from(tmp.querySelectorAll("[data-ms-comment]"));
    const seen = new Set<string>();
    const clist: CommentEntry[] = [];
    for (const el of commentEls) {
      const id = el.getAttribute("data-ms-comment") ?? "";
      if (seen.has(id)) continue;
      seen.add(id);
      clist.push({ id, author: "Reviewer", text: el.textContent ?? "", resolved: false });
    }
    setComments(clist);

    // Changes
    const changeEls = Array.from(tmp.querySelectorAll("[data-ms-change]"));
    const chlist: TrackChangeEntry[] = changeEls.map((el) => ({
      id: el.getAttribute("data-ms-change") ?? "",
      changeId: el.getAttribute("data-ms-change") ?? "",
      author: el.getAttribute("data-ms-author") ?? "Author",
      kind: (el.getAttribute("data-ms-change-type") as TrackChangeEntry["kind"]) ?? "insert",
    }));
    setChanges(chlist);
  }, []);

  // ---- boot: REST token + session, then WebSocket ----
  useEffect(() => {
    let cancelled = false;
    const boot = async () => {
      try {
        let token = props.token;
        if (!token) {
          const res = await fetch(`${apiUrl}/api/auth/token`, {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ userId, documentId }),
          });
          const data = (await res.json()) as { token?: string; error?: string };
          if (!res.ok || !data.token) throw new Error(data.error ?? "token request failed");
          token = data.token;
          setAuthToken(token);
        }

        const client = new CollabClient(wsUrl, token, documentId, {
          onInitialState: (h, seq) => {
            setHtml(h);
            onReady?.({ sessionId: documentId, seq, clientId: client.getClientId() });
            extractDocumentMetadata(h);
          },
          onRemoteOps: () => {
            // Keep stats fresh on remote changes
            const root = rootRef.current?.querySelector(".ms-editor-surface");
            if (root) extractDocumentMetadata(root.innerHTML);
          },
          onAck: () => {
            const root = rootRef.current?.querySelector(".ms-editor-surface");
            if (root) extractDocumentMetadata(root.innerHTML);
          },
          onRejected: (code, message) => onError?.(code, message),
          onStateChange: (state) => setWsState(state),
          onPresence: (clientId, caret, selection) => {
            const map = new Map(peersRef.current);
            const existing = map.get(clientId) ?? { clientId, color: colorFor(clientId) };
            existing.caret = caret;
            existing.selection = selection;
            map.set(clientId, existing);
            peersRef.current = map;
            setPeers([...map.values()]);
          },
        });
        collabRef.current = client;
        setCollab(client);
        if (!cancelled) client.connect();
      } catch (err) {
        onError?.("boot", err instanceof Error ? err.message : String(err));
        setWsState("failed");
      }
    };
    void boot();
    return () => {
      cancelled = true;
      collabRef.current?.disconnect();
      collabRef.current = null;
    };
  }, [wsUrl, apiUrl, documentId, userId, props.token, onReady, onError, extractDocumentMetadata]);

  const handleCaretChange = useCallback((caret: CaretPosition, selection?: SelectionSpan) => {
    collabRef.current?.sendPresence(caret, selection);
  }, []);

  const handleUndo = useCallback(() => {
    collabRef.current?.undo();
  }, []);

  const handleResync = useCallback(() => {
    collabRef.current?.resync();
  }, []);

  const handleToggleSuggestion = useCallback(() => {
    setSuggestion((s) => !s);
  }, []);

  const handleInsertMarkdown = (rawText: string) => {
    if (!collabRef.current) return;
    const lines = rawText.split("\n").filter((l) => l.trim().length > 0);
    lines.forEach((line, idx) => {
      const op: Operation = {
        id: `ingest-${Date.now().toString(36)}-${idx}`,
        clientId: "",
        type: "insertParagraph",
        block: idx,
        style: line.startsWith("# ") ? "Heading1" : line.startsWith("## ") ? "Heading2" : "Normal",
      };
      collabRef.current?.submit(op);

      const textOp: Operation = {
        id: `text-${Date.now().toString(36)}-${idx}`,
        clientId: "",
        type: "insertText",
        block: idx,
        offset: 0,
        text: line.replace(/^#+\s*/, ""),
      };
      collabRef.current?.submit(textOp);
    });
    collabRef.current.resync();
  };

  const handleInsertTableFromModal = (rows: number, cols: number) => {
    if (!collabRef.current) return;
    const op: Operation = {
      id: `table-${Date.now().toString(36)}`,
      clientId: "",
      type: "insertTable",
      block: 1,
      rows,
      cols,
    };
    collabRef.current.submit(op);
  };

  const handleExportDocx = async () => {
    try {
      const res = await fetch(`${apiUrl}/api/sessions/${documentId}/docx`, {
        headers: authToken ? { Authorization: `Bearer ${authToken}` } : {},
      });
      if (!res.ok) throw new Error("Failed to generate DOCX file");
      const blob = await res.blob();
      const url = window.URL.createObjectURL(blob);
      const a = document.createElement("a");
      a.href = url;
      a.download = `${documentId}.docx`;
      document.body.appendChild(a);
      a.click();
      a.remove();
      window.URL.revokeObjectURL(url);
    } catch (err) {
      alert(`DOCX Export error: ${err instanceof Error ? err.message : String(err)}`);
    }
  };

  const handleExportPdf = () => {
    window.print();
  };

  const handleHeadingClick = (headingId: string) => {
    const el = document.getElementById(headingId);
    if (el) {
      el.scrollIntoView({ behavior: "smooth", block: "center" });
      el.style.backgroundColor = "rgba(56, 239, 125, 0.2)";
      setTimeout(() => {
        el.style.backgroundColor = "";
      }, 1200);
    }
  };

  if (!collab) {
    return (
      <div className="ms-app-container" style={{ alignItems: "center", justifyContent: "center" }}>
        <div style={{ textAlign: "center", padding: 32 }}>
          <div style={{ fontSize: 32, marginBottom: 12 }}>⚡</div>
          <h2 style={{ fontSize: 18, fontWeight: 600, marginBottom: 6 }}>Connecting to Marksmith Collaboration Server</h2>
          <p style={{ fontSize: 13, color: "var(--ms-text-muted)" }}>Session: {documentId} · Status: {wsState}</p>
        </div>
      </div>
    );
  }

  return (
    <div className="ms-app-container">
      {/* Step 0: App TitleBar */}
      <AppTitleBar
        documentId={documentId}
        userId={userId}
        theme={theme}
        onToggleTheme={toggleTheme}
        syncState={wsState === "open" ? "synced" : wsState === "connecting" ? "syncing" : "offline"}
        onExportDocx={handleExportDocx}
        onExportPdf={handleExportPdf}
        onOpenSettings={() => setIsSettingsModalOpen(true)}
        onOpenGalaxy={() => alert("Document Galaxy: Explore interconnected document knowledge graph")}
        activeUsersCount={peers.length}
      />

      {/* Main 3-Pane Workspace */}
      <div className="ms-workspace">
        {/* Step 1: Left Pane (Source & Ingest) */}
        <LeftSourcePane
          isCollapsed={isLeftCollapsed}
          onToggleCollapse={() => setIsLeftCollapsed(!isLeftCollapsed)}
          onInsertMarkdown={handleInsertMarkdown}
          onOpenTemplates={() => setIsTemplatesModalOpen(true)}
        />

        {/* Step 2: Center Pane (Looking Glass Editor) */}
        <main className="ms-pane-center" ref={rootRef}>
          {showToolbar && (
            <Toolbar
              collab={collab}
              getRoot={() => rootRef.current?.querySelector<HTMLElement>(".ms-editor-surface") ?? null}
              suggestionMode={suggestion}
              onToggleSuggestion={handleToggleSuggestion}
              onUndo={handleUndo}
              onResync={handleResync}
              onOpenInsertTableModal={() => setIsTableModalOpen(true)}
            />
          )}

          <div className="ms-page-viewport">
            {html === "" ? (
              <div className="ms-loading">Authoritative OpenXML state loading…</div>
            ) : (
              <EditorSurface
                collab={collab}
                initialHtml={html}
                presence={peers}
                suggestionMode={suggestion}
                onCaretChange={handleCaretChange}
                zoom={zoom}
                fontFamily={fontFamily}
              />
            )}
            {showPanels && <PresenceRail peers={peers} />}
          </div>

          <StatusBar
            wordCount={wordCount}
            charCount={charCount}
            zoom={zoom}
            onZoomIn={() => setZoom((z) => Math.min(180, z + 10))}
            onZoomOut={() => setZoom((z) => Math.max(50, z - 10))}
            onResetZoom={() => setZoom(100)}
            wsState={wsState}
            activeUsersCount={peers.length}
          />
        </main>

        {/* Step 3: Right Pane (Style, TOC & Review) */}
        <RightStylePane
          selectedTheme={selectedThemePreset}
          onSelectTheme={setSelectedThemePreset}
          headings={headings}
          onHeadingClick={handleHeadingClick}
          commentsCount={comments.length}
          changesCount={changes.length}
          onExportDocx={handleExportDocx}
          onExportPdf={handleExportPdf}
          fontFamily={fontFamily}
          onChangeFontFamily={setFontFamily}
        />
      </div>

      {/* Modals */}
      <InsertTableModal
        isOpen={isTableModalOpen}
        onClose={() => setIsTableModalOpen(false)}
        onInsert={handleInsertTableFromModal}
      />

      <TemplatesModal
        isOpen={isTemplatesModalOpen}
        onClose={() => setIsTemplatesModalOpen(false)}
        onSelectTemplate={handleInsertMarkdown}
      />

      <SettingsModal
        isOpen={isSettingsModalOpen}
        onClose={() => setIsSettingsModalOpen(false)}
        userId={userId}
        documentId={documentId}
        wsUrl={wsUrl}
        apiUrl={apiUrl}
      />
    </div>
  );
}
