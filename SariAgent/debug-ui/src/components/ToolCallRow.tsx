import { useState } from "react";
import type { ContentBlock, LlmRequestInfo, ToolCall } from "../types";

function prettyJson(raw: string | null | undefined): string {
  if (!raw) return "{}";
  try {
    return JSON.stringify(JSON.parse(raw), null, 2);
  } catch {
    return raw;
  }
}

function formatDuration(ms: number | undefined): string | null {
  if (ms === undefined || ms === null) return null;
  return ms >= 1000 ? `${(ms / 1000).toFixed(2)} s` : `${Math.round(ms)} ms`;
}

function ImageBlock({ block }: { block: Extract<ContentBlock, { kind: "image" }> }) {
  const [full, setFull] = useState(false);
  if (!block.thumbnail_data_url) {
    return (
      <div className="img-caption">
        image · {block.mime_type} · {block.bytes ?? "?"} bytes ·{" "}
        {block.thumbnail_error ? `thumbnail error: ${block.thumbnail_error}` : block.path}
      </div>
    );
  }
  return (
    <div>
      <img
        src={block.thumbnail_data_url}
        alt={block.path ?? "tool screenshot"}
        className={full ? "full" : ""}
        onClick={() => setFull((value) => !value)}
      />
      <div className="img-caption">
        {block.path} · {block.bytes ?? "?"} bytes · {block.mime_type}
      </div>
    </div>
  );
}

function ContentBlocks({ content }: { content: ContentBlock[] | undefined }) {
  if (!content || content.length === 0) return null;
  return (
    <div>
      <h4>Result</h4>
      {content.map((block, index) =>
        block.kind === "image" ? (
          <ImageBlock key={index} block={block} />
        ) : (
          <pre key={index}>{prettyJson(block.text)}</pre>
        ),
      )}
    </div>
  );
}

export function ToolCallRow({ call }: { call: ToolCall }) {
  const [expanded, setExpanded] = useState(false);
  const duration = formatDuration(call.durationMs);
  return (
    <div className={`tool-row ${call.status === "error" ? "error" : ""}`}>
      <div className="tool-summary" onClick={() => setExpanded((value) => !value)}>
        <span className="chevron">{expanded ? "▾" : "▸"}</span>
        <span className="tool-name">{call.toolName}</span>
        {duration && <span className="tool-duration">· {duration}</span>}
        <span className={`dot ${call.status}`} title={call.status} />
      </div>
      {expanded && (
        <div className="tool-detail">
          <div>
            <h4>Sent</h4>
            <pre>{prettyJson(call.argumentsJson)}</pre>
            {call.unity.map(
              (exchange, index) =>
                exchange.sentRawJson && (
                  <pre key={index} title={`raw JSON sent to Unity (${exchange.command})`}>
                    {exchange.sentRawJson}
                  </pre>
                ),
            )}
          </div>
          {call.unity.some((exchange) => exchange.receivedRaw || exchange.error) && (
            <div>
              <h4>Received</h4>
              {call.unity.map((exchange, index) => {
                if (exchange.error) {
                  return (
                    <pre key={index} className="error-text">
                      {exchange.error.type}: {exchange.error.message}
                    </pre>
                  );
                }
                const raw = exchange.receivedRaw;
                if (!raw) return null;
                return raw.raw_text !== undefined ? (
                  <pre key={index}>{raw.raw_text}</pre>
                ) : (
                  <pre key={index}>
                    {raw.kind} · {raw.bytes ?? "?"} bytes · {raw.mime_type ?? ""}
                  </pre>
                );
              })}
            </div>
          )}
          <ContentBlocks content={call.content} />
          {call.error && (
            <div>
              <h4>Error</h4>
              <pre className="error-text">
                {call.error.type}: {call.error.message}
              </pre>
              {call.error.traceback && (
                <details className="collapsible">
                  <summary className="img-caption">traceback</summary>
                  <pre>{call.error.traceback}</pre>
                </details>
              )}
            </div>
          )}
        </div>
      )}
    </div>
  );
}

export function LlmRequestRow({ info }: { info: LlmRequestInfo }) {
  const [expanded, setExpanded] = useState(false);
  return (
    <div className="tool-row">
      <div className="tool-summary" onClick={() => setExpanded((value) => !value)}>
        <span className="chevron">{expanded ? "▾" : "▸"}</span>
        <span className="tag" style={{ margin: 0 }}>
          llm request
        </span>
        <span className="tool-name">{info.model ?? "model"}</span>
        {info.toolNames && (
          <span className="tool-duration">· {info.toolNames.length} tools</span>
        )}
      </div>
      {expanded && (
        <div className="tool-detail">
          <div>
            <h4>Request</h4>
            <pre>
              {JSON.stringify(
                {
                  model: info.model,
                  api_style: info.apiStyle,
                  tool_names: info.toolNames,
                  input_item_count: info.inputItemCount,
                },
                null,
                2,
              )}
            </pre>
          </div>
          {info.instructions && (
            <div>
              <h4>Instructions</h4>
              <pre>{info.instructions}</pre>
            </div>
          )}
          {info.inputItems && (
            <div>
              <h4>Input items</h4>
              <pre>{JSON.stringify(info.inputItems, null, 2)}</pre>
            </div>
          )}
        </div>
      )}
    </div>
  );
}
