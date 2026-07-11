import { AlertTriangle, CheckCircle2, XCircle } from "lucide-react";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Skeleton } from "@/components/ui/skeleton";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import { useSetupStatus } from "@/api/queries";
import { SecretsForm } from "./SecretsForm";

interface CheckProps {
  ok: boolean;
  /** Optional checks show a warning icon instead of a failure when missing. */
  optional?: boolean;
  label: string;
  description: string;
}

function Check({ ok, optional, label, description }: CheckProps) {
  const Icon = ok ? CheckCircle2 : optional ? AlertTriangle : XCircle;
  const color = ok ? "text-emerald-500" : optional ? "text-amber-500" : "text-destructive";
  return (
    <div className="flex items-start gap-3 py-2">
      <Icon className={`mt-0.5 size-5 shrink-0 ${color}`} />
      <div>
        <p className="text-sm font-medium">{label}</p>
        <p className="text-xs text-muted-foreground">{description}</p>
      </div>
    </div>
  );
}

export function SetupPage() {
  const setup = useSetupStatus();

  return (
    <div className="mx-auto max-w-2xl space-y-6 p-6">
      <div>
        <h1 className="text-xl font-semibold">Setup</h1>
        <p className="text-sm text-muted-foreground">Cluster readiness and secret configuration.</p>
      </div>

      <Card>
        <CardHeader>
          <CardTitle>Cluster readiness</CardTitle>
          <CardDescription>What the backend can see from this machine.</CardDescription>
        </CardHeader>
        <CardContent className="divide-y">
          {setup.isPending && (
            <div className="space-y-3 py-2">
              <Skeleton className="h-6" />
              <Skeleton className="h-6" />
              <Skeleton className="h-6" />
            </div>
          )}
          {setup.isError && (
            <Alert variant="destructive">
              <AlertTitle>Could not read setup status</AlertTitle>
              <AlertDescription>{setup.error.message}</AlertDescription>
            </Alert>
          )}
          {setup.isSuccess && (
            <>
              <Check
                ok={setup.data.kubeconfigPresent}
                label="Kubeconfig present"
                description="A kubeconfig was found, so the backend knows how to reach a cluster."
              />
              <Check
                ok={setup.data.clusterReachable}
                label="Cluster reachable"
                description="The Kubernetes API responds (Docker Desktop must be running)."
              />
              <Check
                ok={setup.data.namespaceReady}
                label="Namespace ready"
                description="The game-servers namespace exists."
              />
              <Check
                ok={setup.data.metricsServerPresent}
                optional
                label="metrics-server installed (optional)"
                description="Without it, CPU/memory usage is unavailable — everything else still works."
              />
              <Check
                ok={setup.data.secretsConfigured}
                optional
                label="Secrets configured (optional)"
                description="Steam tokens and RCON passwords. Required by some games (e.g. CS2)."
              />
            </>
          )}
        </CardContent>
      </Card>

      {setup.isSuccess && setup.data.warnings.length > 0 && (
        <Alert>
          <AlertTriangle className="size-4" />
          <AlertTitle>Warnings</AlertTitle>
          <AlertDescription>
            <ul className="list-disc pl-4">
              {setup.data.warnings.map((warning) => (
                <li key={warning}>{warning}</li>
              ))}
            </ul>
          </AlertDescription>
        </Alert>
      )}

      <SecretsForm />
    </div>
  );
}
