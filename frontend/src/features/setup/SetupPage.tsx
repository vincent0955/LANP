import { AlertTriangle, CheckCircle2 } from "lucide-react";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import { useSetupStatus } from "@/api/queries";
import { RuntimeCard } from "./RuntimeCard";

export function SetupPage() {
  const setup = useSetupStatus();
  const runtimeOk = setup.isSuccess && setup.data.dockerEngineReachable;
  const warnings = setup.isSuccess ? setup.data.warnings : [];

  return (
    <div className="mx-auto max-w-2xl space-y-6 p-6">
      <div>
        <h1 className="text-xl font-semibold">Setup</h1>
        <p className="text-sm text-muted-foreground">
          Anything that needs your attention shows up here.
        </p>
      </div>

      {/* Self-hides once the runtime is installed and running. */}
      <RuntimeCard />

      {runtimeOk && warnings.length === 0 && (
        <div className="flex items-center gap-2 text-sm text-muted-foreground">
          <CheckCircle2 className="size-5 shrink-0 text-emerald-500" />
          <span>The container runtime is running — nothing to set up.</span>
        </div>
      )}

      {warnings.length > 0 && (
        <Alert>
          <AlertTriangle className="size-4" />
          <AlertTitle>Warnings</AlertTitle>
          <AlertDescription>
            <ul className="list-disc pl-4">
              {warnings.map((warning) => (
                <li key={warning}>{warning}</li>
              ))}
            </ul>
          </AlertDescription>
        </Alert>
      )}
    </div>
  );
}
