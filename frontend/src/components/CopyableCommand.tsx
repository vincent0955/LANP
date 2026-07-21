import { Copy } from "lucide-react";
import { toast } from "sonner";
import { Button } from "@/components/ui/button";

/** A one-line command shown in a monospace box with a copy button. */
export function CopyableCommand({ command }: { command: string }) {
  return (
    <div className="flex items-start gap-2">
      <code className="grow overflow-x-auto whitespace-pre rounded bg-muted px-2 py-1.5 font-mono text-xs">
        {command}
      </code>
      <Button
        variant="ghost"
        size="icon"
        className="shrink-0"
        onClick={() => {
          void navigator.clipboard.writeText(command);
          toast.success("Copied.");
        }}
      >
        <Copy className="size-4" />
      </Button>
    </div>
  );
}
