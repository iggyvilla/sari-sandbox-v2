import { useEffect, useRef, useState } from "react";
import type { ChatItem, Run, StageSection } from "../types";
import { LlmRequestRow, ToolCallRow } from "./ToolCallRow";

function ReasoningBlock({ item }: { item: Extract<ChatItem, { kind: "reasoning" }> }) {
  const [expanded, setExpanded] = useState(false);
  const open = expanded || item.open; // auto-open while streaming, collapse when done
  const text = item.text.trim();
  return (
    <div className="reasoning-block">
      <div className="reasoning-summary" onClick={() => setExpanded((value) => !value)}>
        <span className="chevron">{open ? "▾" : "▸"}</span>
        <span className="tag">reasoning{item.open ? "…" : ""}</span>
        {!open && <span className="reasoning-preview">{text}</span>}
      </div>
      {open && <div className="reasoning-text">{text}</div>}
    </div>
  );
}

function ChatItemView({ item }: { item: ChatItem }) {
  switch (item.kind) {
    case "user":
      return <div className="user-bubble">{item.text}</div>;
    case "reasoning":
      return <ReasoningBlock item={item} />;
    case "assistant": {
      // Models often open with blank lines (and reasoning-only responses can
      // leave no text at all); trim for display, omit the bubble when empty.
      const text = item.text.trim();
      if (!text) return null;
      return (
        <div className="assistant-block">
          {text}
          {item.open && <span className="caret" />}
        </div>
      );
    }
    case "tool":
      return <ToolCallRow call={item.call} />;
    case "llm_request":
      return <LlmRequestRow info={item.info} />;
  }
}

function StageSectionView({ section }: { section: StageSection }) {
  const duration =
    section.durationMs !== undefined ? ` · ${Math.round(section.durationMs)} ms` : "";
  const tokens = section.output?.usage?.totalTokens;
  const tokenBadge = tokens !== undefined ? ` · ${tokens.toLocaleString()} tok` : "";
  return (
    <>
      <div className={`stage-header ${section.status === "error" ? "error" : ""}`}>
        <span className="stage-name">
          {section.stage}
          {section.occurrence > 1 ? ` — pass ${section.occurrence}` : ""}
        </span>
        <span className="stage-meta">
          {section.status === "running" ? "running…" : section.status + duration + tokenBadge}
        </span>
      </div>
      {section.items.map((item, index) => (
        <ChatItemView key={index} item={item} />
      ))}
      {section.output &&
        (section.output.kind === "code" ? (
          <pre className="stage-output code">{section.output.text}</pre>
        ) : (
          <div className="stage-output">{section.output.text}</div>
        ))}
    </>
  );
}

function RunView({ run, isActive }: { run: Run; isActive: boolean }) {
  const [collapsed, setCollapsed] = useState(!isActive);
  useEffect(() => {
    if (isActive) setCollapsed(false);
  }, [isActive]);

  if (collapsed) {
    return (
      <div className="run collapsed" onClick={() => setCollapsed(false)}>
        <div className="run-header">
          <span className={`dot ${run.status}`} />
          <span>{run.userInput || run.runId.slice(0, 8)}</span>
          <span className="spacer" />
          <span>{run.sections.length} stage entries · click to expand</span>
        </div>
      </div>
    );
  }

  const subGoalCount = run.subGoals.length;
  return (
    <div className="run">
      <div className="run-header" onClick={() => !isActive && setCollapsed(true)}>
        <span className={`dot ${run.status}`} />
        <span>
          run {run.runId.slice(0, 8)} · {run.status}
          {run.hitIterationLimit ? " (iteration limit)" : ""}
        </span>
        {run.usage?.totals.totalTokens !== undefined && run.usage.totals.totalTokens > 0 && (
          <span>· {run.usage.totals.totalTokens.toLocaleString()} tokens total</span>
        )}
        {run.totalRuntimeMs !== undefined && (
          <span>· {(run.totalRuntimeMs / 1000).toFixed(1)}s</span>
        )}
        {subGoalCount > 0 && (
          <span>
            · sub-goal {Math.min(run.currentSubGoalIndex + 1, subGoalCount)}/{subGoalCount}
          </span>
        )}
      </div>
      {run.userInput && <div className="user-bubble">{run.userInput}</div>}
      {run.sections.map((section) => (
        <StageSectionView key={section.key} section={section} />
      ))}
      {run.error && (
        <div className="tool-row error">
          <div className="tool-detail">
            <h4>Run error</h4>
            <pre className="error-text">
              {run.error.type}: {run.error.message}
            </pre>
            {run.error.traceback && (
              <details className="collapsible">
                <summary className="img-caption">traceback</summary>
                <pre>{run.error.traceback}</pre>
              </details>
            )}
          </div>
        </div>
      )}
    </div>
  );
}

export function Transcript({
  runs,
  runOrder,
  activeRunId,
}: {
  runs: Record<string, Run>;
  runOrder: string[];
  activeRunId: string | null;
}) {
  const scrollRef = useRef<HTMLDivElement>(null);
  const pinnedRef = useRef(true);

  const handleScroll = () => {
    const el = scrollRef.current;
    if (!el) return;
    pinnedRef.current = el.scrollHeight - el.scrollTop - el.clientHeight < 60;
  };

  useEffect(() => {
    const el = scrollRef.current;
    if (el && pinnedRef.current) el.scrollTop = el.scrollHeight;
  });

  return (
    <div className="transcript" ref={scrollRef} onScroll={handleScroll}>
      <div className="transcript-inner">
        {runOrder.length === 0 && (
          <div className="empty-hint">
            <p>No agent activity yet.</p>
            <p>
              Start the backend with <code>sari-agent --serve</code> and submit a prompt
              below, or attach to a CLI run started with{" "}
              <code>sari-agent --debug "…"</code>.
            </p>
          </div>
        )}
        {runOrder.map((runId) => (
          <RunView key={runId} run={runs[runId]} isActive={runId === activeRunId} />
        ))}
      </div>
    </div>
  );
}
