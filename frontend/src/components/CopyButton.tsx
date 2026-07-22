import { useEffect, useRef, useState } from "react";
import { cn } from "@/lib/utils";

/**
 * Small copy chip from the design handoff: writes to the clipboard and flips its
 * label to "✓ Copied" for 1.5 s. `dark` renders the terminal-block variant.
 */
export function CopyButton({
  text,
  dark = false,
  className,
}: {
  text: string;
  dark?: boolean;
  className?: string;
}) {
  const [copied, setCopied] = useState(false);
  const timer = useRef<ReturnType<typeof setTimeout> | null>(null);
  useEffect(
    () => () => {
      if (timer.current) clearTimeout(timer.current);
    },
    [],
  );

  const copy = async () => {
    try {
      await navigator.clipboard.writeText(text);
    } catch {
      // Clipboard denied (browser dev without permission): still flip the label
      // is wrong — just bail silently; the address stays selectable.
      return;
    }
    setCopied(true);
    if (timer.current) clearTimeout(timer.current);
    timer.current = setTimeout(() => setCopied(false), 1500);
  };

  return (
    <button
      type="button"
      onClick={() => void copy()}
      className={cn(
        "shrink-0 cursor-pointer rounded-[5px] border px-3 py-1 text-xs font-semibold transition-colors",
        dark
          ? "border-[#2c3a4a] text-[#a8e6ff] hover:border-primary hover:text-white"
          : "border-input bg-white text-[#45596b] hover:border-primary hover:text-primary-foreground",
        className,
      )}
    >
      {copied ? "✓ Copied" : "Copy"}
    </button>
  );
}
