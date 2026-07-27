import { CopyableCommand } from "@/components/CopyableCommand";

/**
 * Admin commands the app deliberately won't run itself (firewall rules), shown
 * as copyable snippets. Lives outside RuntimeCard because both engine modes
 * need it — the rules are about the host's ports, whichever engine publishes
 * them — and RuntimeCard hides itself in Docker Desktop mode.
 */
export function FirewallCommands({ commands }: { commands: string[] }) {
  if (commands.length === 0) {
    return null;
  }

  return (
    <details className="text-sm">
      <summary className="cursor-pointer text-muted-foreground">
        Firewall commands (run once as administrator so players can connect)
      </summary>
      <div className="mt-2 space-y-2">
        {commands.map((command) => (
          <CopyableCommand key={command} command={command} />
        ))}
      </div>
    </details>
  );
}
