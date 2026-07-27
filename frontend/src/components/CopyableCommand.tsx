import { Copy } from "lucide-react";
import { toast } from "sonner";
import { Button } from "@/components/ui/button";

/**
 * A command shown in a monospace box with a copy button. Long commands wrap
 * rather than widening the box — min-w-0 stops the flex item from claiming its
 * intrinsic width, which would otherwise stretch whatever card contains it.
 */
export function CopyableCommand({ command }: { command: string }) {
  return (
    <div className="flex items-start gap-2">
      <code className="min-w-0 grow whitespace-pre-wrap break-words rounded bg-muted px-2 py-1.5 font-mono text-xs">
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
