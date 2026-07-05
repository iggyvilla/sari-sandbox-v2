import { useCallback, useEffect, useRef, useState } from "react";
import type { DebugEvent } from "./types";

export type ConnectionState = "connecting" | "open" | "closed";

const FLUSH_INTERVAL_MS = 50;

export function resolveWsUrl(): string {
  const param = new URLSearchParams(window.location.search).get("ws");
  if (param) return param;
  const env = import.meta.env.VITE_SARI_DEBUG_WS as string | undefined;
  if (env) return env;
  return `ws://${window.location.hostname || "localhost"}:8765/debug`;
}

/**
 * Connect to the SariAgent debug websocket with auto-reconnect. Incoming
 * frames are buffered and delivered in ~50ms batches so per-token deltas
 * produce one render per batch instead of one per frame.
 */
export function useDebugSocket(onEvents: (events: DebugEvent[]) => void) {
  const [connection, setConnection] = useState<ConnectionState>("connecting");
  const wsRef = useRef<WebSocket | null>(null);
  const onEventsRef = useRef(onEvents);
  onEventsRef.current = onEvents;

  useEffect(() => {
    const url = resolveWsUrl();
    let ws: WebSocket | null = null;
    let disposed = false;
    let retryDelay = 500;
    let retryTimer: number | undefined;
    let flushTimer: number | undefined;
    let buffer: DebugEvent[] = [];

    const flush = () => {
      flushTimer = undefined;
      if (buffer.length === 0) return;
      const batch = buffer;
      buffer = [];
      onEventsRef.current(batch);
    };

    const connect = () => {
      if (disposed) return;
      setConnection("connecting");
      const socket = new WebSocket(url);
      ws = socket;
      wsRef.current = socket;

      socket.onopen = () => {
        retryDelay = 500;
        setConnection("open");
      };
      socket.onmessage = (message) => {
        try {
          const event = JSON.parse(message.data);
          if (event && typeof event === "object") buffer.push(event);
        } catch {
          return;
        }
        if (flushTimer === undefined) {
          flushTimer = window.setTimeout(flush, FLUSH_INTERVAL_MS);
        }
      };
      socket.onclose = () => {
        // A stale socket (e.g. StrictMode's first mount) must not clobber the
        // ref of a newer connection that closes it superseded.
        if (wsRef.current === socket) wsRef.current = null;
        if (disposed || wsRef.current !== null) return;
        setConnection("closed");
        retryTimer = window.setTimeout(connect, retryDelay);
        retryDelay = Math.min(retryDelay * 2, 5000);
      };
      socket.onerror = () => socket.close();
    };

    connect();
    return () => {
      disposed = true;
      window.clearTimeout(retryTimer);
      window.clearTimeout(flushTimer);
      ws?.close();
    };
  }, []);

  const sendRunStart = useCallback((prompt: string): boolean => {
    const ws = wsRef.current;
    if (!ws || ws.readyState !== WebSocket.OPEN) return false;
    ws.send(JSON.stringify({ type: "client.run.start", prompt }));
    return true;
  }, []);

  return { connection, sendRunStart };
}
