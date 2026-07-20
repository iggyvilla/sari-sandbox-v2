import { useCallback, useReducer, useState } from "react";
import { Transcript } from "./components/Transcript";
import { initialState, reducer } from "./reducer";
import type { DebugEvent } from "./types";
import { useDebugSocket, resolveWsUrl } from "./useDebugSocket";

function promptPlaceholder(
  connection: string,
  serverState: string,
  acceptsPrompts: boolean,
  mode: string | undefined,
): string {
  if (connection !== "open") return `Connecting to ${resolveWsUrl()}…`;
  if (acceptsPrompts) return "Send a task to the agent…";
  if (mode === "cli") return "Agent was started from the CLI — view only";
  if (serverState === "running") return "Run in progress…";
  return "Waiting for server status…";
}

export default function App() {
  const [state, dispatch] = useReducer(reducer, initialState);
  const [draft, setDraft] = useState("");

  const onEvents = useCallback(
    (events: DebugEvent[]) => dispatch({ type: "events", events }),
    [],
  );
  const { connection, sendRunStart, sendRunStop, sendQuestionAnswer } =
    useDebugSocket(onEvents);

  const activeRun = state.activeRunId ? state.runs[state.activeRunId] : null;
  const canSend = connection === "open" && state.server.acceptsPrompts;
  const canStop =
    connection === "open" &&
    state.server.mode === "serve" &&
    state.server.state === "running";

  const submit = () => {
    const prompt = draft.trim();
    if (!prompt || !canSend) return;
    if (sendRunStart(prompt)) setDraft("");
  };

  return (
    <div className="app">
      <header className="status-bar">
        <span className="title">SariAgent Debug</span>
        <span className="pill">
          <span className={`dot ${connection}`} />
          {connection}
        </span>
        {state.server.state !== "unknown" && (
          <span className="pill">
            server {state.server.state}
            {state.server.mode ? ` · ${state.server.mode}` : ""}
          </span>
        )}
        {activeRun?.currentStage && (
          <span className="pill stage">{activeRun.currentStage}</span>
        )}
        <span className="spacer" />
        {activeRun && (
          <>
            {activeRun.subGoals.length > 0 && (
              <span className="pill">
                sub-goal{" "}
                {Math.min(activeRun.currentSubGoalIndex + 1, activeRun.subGoals.length)}/
                {activeRun.subGoals.length}
              </span>
            )}
            <span className="pill">
              <span className={`dot ${activeRun.status}`} />
              run {activeRun.status}
            </span>
          </>
        )}
      </header>

      {state.toasts.length > 0 && (
        <div className="toasts">
          {state.toasts.map((toast) => (
            <div
              key={toast.id}
              className={`toast ${toast.level}`}
              onClick={() => dispatch({ type: "dismiss_toast", id: toast.id })}
            >
              {toast.text}
            </div>
          ))}
        </div>
      )}

      <Transcript
        runs={state.runs}
        runOrder={state.runOrder}
        activeRunId={state.activeRunId}
        onAnswer={sendQuestionAnswer}
      />

      <footer className="prompt-bar">
        <form
          onSubmit={(event) => {
            event.preventDefault();
            submit();
          }}
        >
          <textarea
            value={draft}
            rows={1}
            disabled={!canSend}
            placeholder={promptPlaceholder(
              connection,
              state.server.state,
              state.server.acceptsPrompts,
              state.server.mode,
            )}
            onChange={(event) => setDraft(event.target.value)}
            onKeyDown={(event) => {
              if (event.key === "Enter" && !event.shiftKey) {
                event.preventDefault();
                submit();
              }
            }}
          />
          <button
            type="button"
            className="stop"
            disabled={!canStop}
            title="Stop the current run"
            onClick={() => sendRunStop()}
          >
            Stop
          </button>
          <button type="submit" disabled={!canSend || !draft.trim()}>
            Send
          </button>
        </form>
      </footer>
    </div>
  );
}
