import { useRef, useState } from "react";
import { SendHorizonal, TerminalSquare } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import { api } from "@/api/endpoints";
import { ApiError } from "@/api/http";
import type { ServerDetail } from "@/api/types";

interface TranscriptEntry {
  command: string;
  response: string;
  isError: boolean;
}

export function RconTab({ server }: { server: ServerDetail }) {
  const [command, setCommand] = useState("");
  const [transcript, setTranscript] = useState<TranscriptEntry[]>([]);
  const [sending, setSending] = useState(false);
  const scrollRef = useRef<HTMLDivElement>(null);

  const running = server.status === "Running";

  const send = async () => {
    const cmd = command.trim();
    if (!cmd || sending) return;
    setSending(true);
    setCommand("");
    let entry: TranscriptEntry;
    try {
      const result = await api.sendRcon(server.name, cmd);
      entry = { command: cmd, response: result.response || "(empty response)", isError: false };
    } catch (error) {
      entry = {
        command: cmd,
        response:
          error instanceof ApiError
            ? `${error.title}${error.detail ? ` ${error.detail}` : ""}`
            : "Request failed.",
        isError: true,
      };
    }
    setTranscript((t) => [...t, entry]);
    setSending(false);
    queueMicrotask(() =>
      scrollRef.current?.scrollTo({ top: scrollRef.current.scrollHeight }),
    );
  };

  if (!running) {
    return (
      <Alert>
        <TerminalSquare className="size-4" />
        <AlertTitle>RCON needs a running server</AlertTitle>
        <AlertDescription>
          The server is currently {server.status.toLowerCase()}. Start it to send commands.
        </AlertDescription>
      </Alert>
    );
  }

  return (
    <div className="flex h-[calc(100vh-14rem)] flex-col gap-2">
      <div
        ref={scrollRef}
        className="flex-1 space-y-3 overflow-y-auto rounded-md border bg-zinc-950 p-3 font-mono text-xs text-zinc-100"
      >
        {transcript.length === 0 && (
          <p className="text-zinc-500">
            Send a command below — e.g. <span className="text-zinc-300">status</span> on Source
            servers, <span className="text-zinc-300">list</span> on Minecraft.
          </p>
        )}
        {transcript.map((entry, i) => (
          <div key={i}>
            <p className="text-sky-400">&gt; {entry.command}</p>
            <p className={`whitespace-pre-wrap break-all ${entry.isError ? "text-red-400" : ""}`}>
              {entry.response}
            </p>
          </div>
        ))}
      </div>
      <form
        className="flex gap-2"
        onSubmit={(e) => {
          e.preventDefault();
          void send();
        }}
      >
        <Input
          className="font-mono"
          placeholder="RCON command…"
          value={command}
          onChange={(e) => setCommand(e.target.value)}
          disabled={sending}
          autoFocus
        />
        <Button type="submit" disabled={sending || !command.trim()}>
          <SendHorizonal className="size-4" />
          {sending ? "Sending…" : "Send"}
        </Button>
      </form>
    </div>
  );
}
