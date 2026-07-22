import { useEffect, useMemo, useRef, useState } from "react";
import { useVirtualizer } from "@tanstack/react-virtual";
import { ArrowDownToLine, Eraser } from "lucide-react";
import { api } from "@/api/endpoints";
import { ApiError } from "@/api/http";
import { appendLogLine, subscribeLogs, unsubscribeLogs, useLogStore } from "@/realtime/logStore";
import { formatTime } from "@/lib/format";
import type { ServerDetail } from "@/api/types";

const NO_LINES: never[] = [];

/**
 * One dark console panel (design handoff → Logs tab): the live log stream, and —
 * since the RCON tab was folded in here — a command prompt at the bottom for
 * servers that expose RCON. Commands and their responses are appended into the
 * same stream.
 */
export function LogsTab({ server }: { server: ServerDetail }) {
  const serverName = server.name;
  const lines = useLogStore((s) => s.buffers[serverName]) ?? NO_LINES;
  const clear = useLogStore((s) => s.clear);
  const [filter, setFilter] = useState("");
  const [autoScroll, setAutoScroll] = useState(true);
  const [command, setCommand] = useState("");
  const [sending, setSending] = useState(false);
  const parentRef = useRef<HTMLDivElement>(null);

  const running = server.replicas > 0;
  const hasRcon = server.ports.some((p) => p.name.toLowerCase().includes("rcon"));

  useEffect(() => {
    subscribeLogs(serverName);
    return () => unsubscribeLogs(serverName);
  }, [serverName]);

  const visible = useMemo(() => {
    if (!filter.trim()) return lines;
    const needle = filter.toLowerCase();
    return lines.filter((entry) => entry.line.toLowerCase().includes(needle));
  }, [lines, filter]);

  const virtualizer = useVirtualizer({
    count: visible.length,
    getScrollElement: () => parentRef.current,
    estimateSize: () => 20,
    overscan: 30,
  });

  // Follow the tail while auto-scroll is on.
  useEffect(() => {
    if (autoScroll && visible.length > 0) {
      virtualizer.scrollToIndex(visible.length - 1, { align: "end" });
    }
  }, [visible.length, autoScroll, virtualizer]);

  // Scrolling up pauses tailing; scrolling back to the bottom resumes it.
  const onScroll = () => {
    const el = parentRef.current;
    if (!el) return;
    const atBottom = el.scrollHeight - el.scrollTop - el.clientHeight < 40;
    setAutoScroll(atBottom);
  };

  const send = async () => {
    const cmd = command.trim();
    if (!cmd || sending) return;
    setSending(true);
    setCommand("");
    const stamp = () => new Date().toISOString();
    appendLogLine({ serverName, line: `> ${cmd}`, timestamp: stamp() });
    try {
      const result = await api.sendRcon(serverName, cmd);
      for (const line of (result.response || "(empty response)").split("\n")) {
        appendLogLine({ serverName, line, timestamp: stamp() });
      }
    } catch (error) {
      const detail =
        error instanceof ApiError
          ? `${error.title}${error.detail ? ` ${error.detail}` : ""}`
          : "Request failed.";
      appendLogLine({ serverName, line: `! ${detail}`, timestamp: stamp() });
    }
    setSending(false);
    setAutoScroll(true);
  };

  return (
    <div className="flex h-[calc(100vh-19rem)] min-h-[480px] flex-col overflow-hidden rounded-[10px] bg-[#10161f]">
      {/* Console toolbar */}
      <div className="flex items-center gap-2 border-b border-[#1c2836] px-4 py-2.5">
        <input
          className="w-56 rounded-[6px] border border-[#2c3a4a] bg-transparent px-3 py-1.5 font-mono text-xs text-[#a8e6ff] outline-none placeholder:text-[#4a5a6b] focus:border-primary"
          placeholder="Filter lines…"
          value={filter}
          onChange={(e) => setFilter(e.target.value)}
        />
        {!autoScroll && (
          <button
            type="button"
            onClick={() => setAutoScroll(true)}
            className="flex cursor-pointer items-center gap-1.5 rounded-[5px] border border-[#2c3a4a] px-3 py-1.5 text-xs font-semibold text-[#a8e6ff] transition-colors hover:border-primary hover:text-white"
          >
            <ArrowDownToLine className="size-3.5" />
            Follow
          </button>
        )}
        <button
          type="button"
          onClick={() => clear(serverName)}
          className="flex cursor-pointer items-center gap-1.5 rounded-[5px] px-2.5 py-1.5 text-xs font-semibold text-[#4a5a6b] transition-colors hover:text-[#a8e6ff]"
        >
          <Eraser className="size-3.5" />
          Clear
        </button>
        <span className="ml-auto font-mono text-[11px] text-[#4a5a6b]">
          {visible.length === lines.length
            ? `${lines.length} lines`
            : `${visible.length} of ${lines.length} lines`}
        </span>
      </div>

      {/* Log stream */}
      <div
        ref={parentRef}
        onScroll={onScroll}
        className="flex-1 overflow-y-auto p-3 font-mono text-xs text-zinc-100"
      >
        {visible.length === 0 ? (
          <p className="p-2 font-mono text-[13px] text-[#4a5a6b]">
            Waiting for log output… (stopped servers produce no logs)
          </p>
        ) : (
          <div className="relative w-full" style={{ height: virtualizer.getTotalSize() }}>
            {virtualizer.getVirtualItems().map((item) => {
              const entry = visible[item.index];
              const isCommand = entry.line.startsWith("> ");
              const isCommandError = entry.line.startsWith("! ");
              return (
                <div
                  key={item.key}
                  className="absolute left-0 top-0 flex w-full gap-2 whitespace-pre-wrap break-all leading-5"
                  style={{ transform: `translateY(${item.start}px)` }}
                  ref={virtualizer.measureElement}
                  data-index={item.index}
                >
                  <span className="shrink-0 select-none text-[#4a5a6b]">
                    {formatTime(entry.timestamp)}
                  </span>
                  <span
                    className={
                      isCommand ? "text-[#a8e6ff]" : isCommandError ? "text-red-400" : undefined
                    }
                  >
                    {entry.line}
                  </span>
                </div>
              );
            })}
          </div>
        )}
      </div>

      {/* RCON prompt (the console covers the old RCON tab) */}
      {hasRcon && (
        <form
          className="flex items-center gap-2.5 border-t border-[#1c2836] px-4 py-2.5"
          onSubmit={(e) => {
            e.preventDefault();
            void send();
          }}
        >
          <span className="select-none font-mono text-[13px] font-semibold text-primary">❯</span>
          <input
            className="flex-1 bg-transparent font-mono text-[13px] text-[#a8e6ff] outline-none placeholder:text-[#4a5a6b]"
            placeholder={
              running
                ? "Server command — e.g. list on Minecraft, status on Source servers"
                : "Start the server to send commands"
            }
            value={command}
            onChange={(e) => setCommand(e.target.value)}
            disabled={sending || !running}
          />
          <button
            type="submit"
            disabled={sending || !running || !command.trim()}
            className="cursor-pointer rounded-[5px] border border-[#2c3a4a] px-3 py-1.5 text-xs font-semibold text-[#a8e6ff] transition-colors hover:border-primary hover:text-white disabled:cursor-default disabled:opacity-40"
          >
            {sending ? "Sending…" : "Send"}
          </button>
        </form>
      )}
    </div>
  );
}
