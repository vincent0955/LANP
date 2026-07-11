import { Badge } from "@/components/ui/badge";
import { cn } from "@/lib/utils";
import type { ServerStatus } from "@/api/types";

const styles: Record<ServerStatus, string> = {
  Running: "bg-emerald-600/15 text-emerald-600 dark:text-emerald-400 border-emerald-600/30",
  Stopped: "bg-muted text-muted-foreground border-border",
  Pending: "bg-amber-500/15 text-amber-600 dark:text-amber-400 border-amber-500/30",
  Error: "bg-destructive/15 text-destructive border-destructive/30",
  Unknown: "bg-muted text-muted-foreground border-border",
};

export function StatusBadge({ status, className }: { status: ServerStatus; className?: string }) {
  return (
    <Badge variant="outline" className={cn(styles[status], className)}>
      {status === "Pending" && (
        <span className="mr-1 inline-block size-2 animate-pulse rounded-full bg-current" />
      )}
      {status}
    </Badge>
  );
}
