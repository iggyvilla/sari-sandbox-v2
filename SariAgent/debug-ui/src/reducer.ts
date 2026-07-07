import type {
  ChatItem,
  DebugEvent,
  Run,
  StageOutput,
  StageSection,
  State,
  ToolCall,
  UsageInfo,
} from "./types";

export const initialState: State = {
  runs: {},
  runOrder: [],
  activeRunId: null,
  server: { state: "unknown", acceptsPrompts: false },
  toasts: [],
};

export type Action =
  | { type: "events"; events: DebugEvent[] }
  | { type: "dismiss_toast"; id: number };

let toastId = 0;

/**
 * Fold a batch of websocket events into UI state. The whole batch produces a
 * single new top-level state object; nested run structures are owned by the
 * reducer and mutated in place (the tree re-renders per batch, nothing memoizes
 * on nested identity).
 */
export function reducer(state: State, action: Action): State {
  if (action.type === "dismiss_toast") {
    return { ...state, toasts: state.toasts.filter((t) => t.id !== action.id) };
  }

  const next: State = { ...state, toasts: [...state.toasts] };
  for (const event of action.events) {
    applyEvent(next, event);
  }
  return next;
}

function applyEvent(state: State, event: DebugEvent): void {
  if (event.type.startsWith("server.")) {
    applyServerEvent(state, event);
    return;
  }
  if (!KNOWN_RUN_EVENTS.has(event.type)) return;

  const run = getOrCreateRun(state, event.run_id);
  if (event.seq <= run.maxSeq) return; // replay duplicate after reconnect
  run.maxSeq = event.seq;

  const payload = event.payload ?? {};
  switch (event.type) {
    case "run.started":
      run.userInput = payload.user_input ?? run.userInput;
      run.status = "running";
      state.activeRunId = run.runId;
      break;

    case "run.completed":
      run.status = "completed";
      run.hitIterationLimit = Boolean(payload.hit_iteration_limit);
      run.currentStage = null;
      if (payload.usage) {
        run.usage = {
          perStage: Object.fromEntries(
            Object.entries(payload.usage.per_stage ?? {}).map(([stage, usage]) => [
              stage,
              toUsageInfo(usage) ?? {},
            ]),
          ),
          totals: toUsageInfo(payload.usage.totals) ?? {},
        };
      }
      if (typeof payload.total_runtime_ms === "number") {
        run.totalRuntimeMs = payload.total_runtime_ms;
      }
      break;

    case "run.error":
      run.status = "error";
      run.error = payload.error;
      run.currentStage = null;
      break;

    case "pipeline.stage.started": {
      const stage = event.stage ?? "unknown";
      const occurrence =
        run.sections.filter((section) => section.stage === stage).length + 1;
      run.sections.push({
        key: `${stage}#${occurrence}`,
        stage,
        occurrence,
        status: "running",
        items: [],
      });
      run.currentStage = stage;
      break;
    }

    case "pipeline.stage.completed": {
      const section = lastSectionForStage(run, event.stage);
      if (section) {
        section.status = "completed";
        section.durationMs = payload.duration_ms;
        if (payload.output?.text) {
          section.output = toStageOutput(payload.output);
        }
      }
      break;
    }

    case "pipeline.stage.error": {
      const section = lastSectionForStage(run, event.stage);
      if (section) {
        section.status = "error";
        section.durationMs = payload.duration_ms;
      }
      break;
    }

    case "pipeline.subgoals.planned":
      run.subGoals = (payload.sub_goals ?? []).map((goal: any) => ({
        description: goal.description ?? "",
        status: goal.status ?? "pending",
      }));
      break;

    case "pipeline.subgoal.changed":
      run.currentSubGoalIndex = payload.current_sub_goal_index ?? run.currentSubGoalIndex;
      if (payload.current_sub_goal && run.subGoals[run.currentSubGoalIndex]) {
        run.subGoals[run.currentSubGoalIndex].status = payload.current_sub_goal.status;
      }
      break;

    case "llm.request.started":
      closeOpenTextItems(run);
      currentSection(run).items.push({
        kind: "llm_request",
        info: {
          model: payload.model,
          apiStyle: payload.api_style,
          toolNames: payload.tool_names,
          instructions: payload.instructions,
          inputItems: payload.input_items,
          inputItemCount: payload.input_item_count,
        },
      });
      break;

    case "llm.reasoning.delta":
      appendText(run, "reasoning", payload.text ?? "");
      break;

    case "llm.text.delta":
      appendText(run, "assistant", payload.text ?? "");
      break;

    case "llm.text.completed": {
      const item = openTextItem(run, "assistant");
      if (item) {
        item.text = payload.text ?? item.text;
        item.open = false;
      } else if (payload.text) {
        currentSection(run).items.push({ kind: "assistant", text: payload.text, open: false });
      }
      closeOpenTextItems(run);
      break;
    }

    case "tool.call.started":
      closeOpenTextItems(run);
      currentSection(run).items.push({
        kind: "tool",
        call: {
          callId: payload.call_id ?? "",
          toolName: payload.tool_name ?? "tool",
          argumentsJson: payload.arguments_json,
          status: "running",
          unity: [],
        },
      });
      break;

    case "tool.call.completed": {
      const call = findToolCall(run, payload.call_id);
      if (call) {
        call.status = "completed";
        call.result = payload.result;
        call.content = payload.content;
        call.durationMs = payload.duration_ms;
      }
      break;
    }

    case "tool.call.error": {
      const call = findToolCall(run, payload.call_id);
      if (call) {
        call.status = "error";
        call.error = payload.error;
        call.durationMs = payload.duration_ms;
      }
      break;
    }

    case "unity.command.sent": {
      const call = findToolCall(run, payload.tool_call?.call_id);
      call?.unity.push({ command: payload.command ?? "", sentRawJson: payload.raw_json });
      break;
    }

    case "unity.command.received": {
      const call = findToolCall(run, payload.tool_call?.call_id);
      const exchange = call?.unity.find(
        (entry) => entry.command === payload.command && !entry.receivedRaw && !entry.error,
      );
      if (exchange) {
        exchange.receivedRaw = payload.raw_response;
        exchange.durationMs = payload.duration_ms;
      }
      break;
    }

    case "unity.command.error": {
      const call = findToolCall(run, payload.tool_call?.call_id);
      const exchange = call?.unity.find(
        (entry) => entry.command === payload.command && !entry.receivedRaw && !entry.error,
      );
      if (exchange) {
        exchange.error = payload.error;
        exchange.durationMs = payload.duration_ms;
      }
      break;
    }
  }
}

const KNOWN_RUN_EVENTS = new Set([
  "run.started",
  "run.completed",
  "run.error",
  "pipeline.stage.started",
  "pipeline.stage.completed",
  "pipeline.stage.error",
  "pipeline.subgoals.planned",
  "pipeline.subgoal.changed",
  "llm.request.started",
  "llm.reasoning.delta",
  "llm.text.delta",
  "llm.text.completed",
  "tool.call.started",
  "tool.call.completed",
  "tool.call.error",
  "unity.command.sent",
  "unity.command.received",
  "unity.command.error",
]);

function applyServerEvent(state: State, event: DebugEvent): void {
  const payload = event.payload ?? {};
  switch (event.type) {
    case "server.status":
      state.server = {
        state: payload.state ?? "unknown",
        acceptsPrompts: Boolean(payload.accepts_prompts),
        mode: payload.mode,
      };
      break;
    case "server.run.rejected":
      state.toasts.push({
        id: ++toastId,
        text: `Prompt rejected: ${payload.reason ?? "unknown"}`,
        level: "warn",
      });
      break;
    // server.run.accepted needs no UI state; run.started follows immediately.
  }
}

function toUsageInfo(raw: any): UsageInfo | undefined {
  if (!raw || typeof raw !== "object") return undefined;
  return {
    inputTokens: raw.input_tokens,
    outputTokens: raw.output_tokens,
    totalTokens: raw.total_tokens,
  };
}

function toStageOutput(raw: any): StageOutput {
  return {
    kind: raw.kind === "code" ? "code" : "text",
    text: raw.text ?? "",
    language: raw.language ?? undefined,
    usage: toUsageInfo(raw.usage),
  };
}

function getOrCreateRun(state: State, runId: string): Run {
  let run = state.runs[runId];
  if (!run) {
    run = {
      runId,
      maxSeq: 0,
      status: "running",
      userInput: "",
      subGoals: [],
      currentSubGoalIndex: 0,
      currentStage: null,
      sections: [],
    };
    state.runs = { ...state.runs, [runId]: run };
    state.runOrder = [...state.runOrder, runId];
    state.activeRunId = runId;
  }
  return run;
}

function currentSection(run: Run): StageSection {
  let section = run.sections[run.sections.length - 1];
  if (!section) {
    // Replay was truncated mid-run; park items under a synthetic section.
    section = { key: "unknown#1", stage: "…", occurrence: 1, status: "running", items: [] };
    run.sections.push(section);
  }
  return section;
}

function lastSectionForStage(run: Run, stage: string | null): StageSection | undefined {
  for (let i = run.sections.length - 1; i >= 0; i--) {
    if (run.sections[i].stage === stage) return run.sections[i];
  }
  return undefined;
}

function openTextItem(
  run: Run,
  kind: "reasoning" | "assistant",
): Extract<ChatItem, { kind: "reasoning" | "assistant" }> | undefined {
  const items = currentSection(run).items;
  const last = items[items.length - 1];
  if (last && last.kind === kind && last.open) return last;
  return undefined;
}

function appendText(run: Run, kind: "reasoning" | "assistant", text: string): void {
  const existing = openTextItem(run, kind);
  if (existing) {
    existing.text += text;
    return;
  }
  // Reasoning arriving while an assistant item streams (or vice versa) starts
  // a new block; interleaved deltas of different kinds are rare in practice.
  currentSection(run).items.push({ kind, text, open: true });
}

function closeOpenTextItems(run: Run): void {
  for (const item of currentSection(run).items) {
    if ((item.kind === "reasoning" || item.kind === "assistant") && item.open) {
      item.open = false;
    }
  }
}

function findToolCall(run: Run, callId: string | undefined): ToolCall | undefined {
  if (!callId) return undefined;
  for (let s = run.sections.length - 1; s >= 0; s--) {
    const items = run.sections[s].items;
    for (let i = items.length - 1; i >= 0; i--) {
      const item = items[i];
      if (item.kind === "tool" && item.call.callId === callId) return item.call;
    }
  }
  return undefined;
}
