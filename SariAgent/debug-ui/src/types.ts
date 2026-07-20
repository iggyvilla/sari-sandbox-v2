/** Mirrors the DebugEvent envelope from sari_agent/debug/events.py (schema v1). */
export interface DebugEvent {
  schema_version: number;
  seq: number;
  run_id: string;
  timestamp: string;
  type: string;
  stage: string | null;
  level: "info" | "warn" | "error";
  summary: string;
  payload: Record<string, any>;
}

export interface ErrorInfo {
  type: string;
  message: string;
  traceback?: string;
}

export type ContentBlock =
  | { kind: "text"; text: string }
  | {
      kind: "image";
      mime_type: string;
      path?: string;
      bytes?: number;
      thumbnail_data_url?: string;
      thumbnail_error?: string;
    };

export interface UnityExchange {
  command: string;
  sentRawJson?: string;
  receivedRaw?: { kind: string; raw_text?: string; bytes?: number; mime_type?: string };
  durationMs?: number;
  error?: ErrorInfo;
}

export interface ToolCall {
  callId: string;
  toolName: string;
  argumentsJson?: string | null;
  status: "running" | "completed" | "error" | "cancelled";
  result?: unknown;
  content?: ContentBlock[];
  error?: ErrorInfo;
  durationMs?: number;
  unity: UnityExchange[];
}

export interface LlmRequestInfo {
  model?: string;
  apiStyle?: string;
  toolNames?: string[];
  instructions?: string;
  inputItems?: unknown[];
  inputItemCount?: number;
}

export interface QuestionChoice {
  id: string;
  label: string;
  description?: string | null;
}

export interface QuestionInfo {
  questionId: string;
  question: string;
  context?: string | null;
  choices: QuestionChoice[];
  allowFreeText: boolean;
  defaultChoiceId?: string;
  status: "pending" | "answered";
  answer?: { choiceId: string; text?: string | null; source: string };
}

export type ChatItem =
  | { kind: "user"; text: string }
  | { kind: "reasoning"; text: string; open: boolean }
  | { kind: "assistant"; text: string; open: boolean }
  | { kind: "tool"; call: ToolCall }
  | { kind: "llm_request"; info: LlmRequestInfo }
  | { kind: "question"; question: QuestionInfo };

export interface UsageInfo {
  inputTokens?: number;
  outputTokens?: number;
  totalTokens?: number;
}

export interface StageOutput {
  kind: "text" | "code";
  text: string;
  language?: string;
  usage?: UsageInfo;
}

export interface StageSection {
  key: string;
  stage: string;
  occurrence: number;
  status: "running" | "completed" | "error" | "cancelled";
  durationMs?: number;
  output?: StageOutput;
  items: ChatItem[];
}

export interface SubGoalInfo {
  description: string;
  status: string;
}

export interface Run {
  runId: string;
  maxSeq: number;
  status: "running" | "completed" | "error" | "cancelled";
  userInput: string;
  subGoals: SubGoalInfo[];
  currentSubGoalIndex: number;
  currentStage: string | null;
  sections: StageSection[];
  error?: ErrorInfo;
  hitIterationLimit?: boolean;
  /** Set when the run was auto-stopped, e.g. "time_limit". */
  stopReason?: string;
  timeLimitS?: number;
  usage?: { perStage: Record<string, UsageInfo>; totals: UsageInfo };
  totalRuntimeMs?: number;
}

export interface ServerInfo {
  state: "idle" | "running" | "unknown";
  acceptsPrompts: boolean;
  mode?: string;
}

export interface Toast {
  id: number;
  text: string;
  level: "info" | "warn" | "error";
}

export interface State {
  runs: Record<string, Run>;
  runOrder: string[];
  activeRunId: string | null;
  server: ServerInfo;
  toasts: Toast[];
}
