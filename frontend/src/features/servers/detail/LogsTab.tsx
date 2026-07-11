import { useEffect, useMemo, useRef, useState } from "react";
import { useVirtualizer } from "@tanstack/react-virtual";
import { ArrowDownToLine, Eraser } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { subscribeLogs, unsubscribeLogs, useLogStore } from "@/realtime/logStore";
import { formatTime } from "@/lib/format";

const NO_LINES: never[] = [];

export function LogsTab({ serverName }: { serverName: string }) {
  const lines = useLogStore((s) => s.buffers[serverName]) ?? NO_LINES;
  const clear = useLogStore((s) => s.clear);
  const [filter, setFilter] = useState("");
  const [autoScroll, setAutoScroll] = useState(true);
  const parentRef = useRef<HTMLDivElement>(null);

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

  return (
    <div className="flex h-[calc(100vh-14rem)] flex-col gap-2">
      <div className="flex items-center gap-2">
        <Input
          className="max-w-xs"
          placeholder="Filter lines…"
          value={filter}
          onChange={(e) => setFilter(e.target.value)}
        />
        {!autoScroll && (
          <Button size="sm" variant="outline" onClick={() => setAutoScroll(true)}>
            <ArrowDownToLine className="size-4" />
            Follow
          </Button>
        )}
        <Button size="sm" variant="ghost" onClick={() => clear(serverName)}>
          <Eraser className="size-4" />
          Clear
        </Button>
        <span className="ml-auto text-xs text-muted-foreground">
          {visible.length === lines.length
            ? `${lines.length} lines`
            : `${visible.length} of ${lines.length} lines`}
        </span>
      </div>

      <div
        ref={parentRef}
        onScroll={onScroll}
        className="flex-1 overflow-y-auto rounded-md border bg-zinc-950 p-2 font-mono text-xs text-zinc-100"
      >
        {visible.length === 0 ? (
          <p className="p-2 text-zinc-500">
            Waiting for log output… (stopped servers produce no logs)
          </p>
        ) : (
          <div className="relative w-full" style={{ height: virtualizer.getTotalSize() }}>
            {virtualizer.getVirtualItems().map((item) => {
              const entry = visible[item.index];
              return (
                <div
                  key={item.key}
                  className="absolute left-0 top-0 flex w-full gap-2 whitespace-pre-wrap break-all leading-5"
                  style={{ transform: `translateY(${item.start}px)` }}
                  ref={virtualizer.measureElement}
                  data-index={item.index}
                >
                  <span className="shrink-0 select-none text-zinc-500">
                    {formatTime(entry.timestamp)}
                  </span>
                  <span>{entry.line}</span>
                </div>
              );
            })}
          </div>
        )}
      </div>
    </div>
  );
}
