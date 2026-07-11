import { useState } from "react";
import { toast } from "sonner";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { useQueryClient } from "@tanstack/react-query";
import { api } from "@/api/endpoints";
import { DEFAULT_BASE_URL, useSettings } from "@/lib/settings";

export function SettingsPage() {
  const settings = useSettings();
  const queryClient = useQueryClient();
  const [baseUrl, setBaseUrl] = useState(settings.baseUrl);
  const [apiToken, setApiToken] = useState(settings.apiToken);
  const [testing, setTesting] = useState(false);

  const save = () => {
    settings.setBaseUrl(baseUrl);
    settings.setApiToken(apiToken);
    // Everything cached was fetched from the old backend/token; start over.
    queryClient.invalidateQueries();
    toast.success("Settings saved.");
  };

  const testConnection = async () => {
    // Persist first so the http client picks the values up.
    settings.setBaseUrl(baseUrl);
    settings.setApiToken(apiToken);
    setTesting(true);
    try {
      const health = await api.health();
      toast.success(
        health.clusterReachable
          ? "Backend and cluster reachable."
          : "Backend reachable — cluster is not (see Setup).",
      );
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Connection failed.");
    } finally {
      setTesting(false);
    }
  };

  return (
    <div className="mx-auto max-w-2xl space-y-6 p-6">
      <div>
        <h1 className="text-xl font-semibold">Settings</h1>
        <p className="text-sm text-muted-foreground">How the app connects to the dashboard backend.</p>
      </div>

      <Card>
        <CardHeader>
          <CardTitle>Backend connection</CardTitle>
          <CardDescription>
            The backend binds to localhost by default; the token is only needed if you exposed it
            beyond this machine.
          </CardDescription>
        </CardHeader>
        <CardContent className="space-y-4">
          <div className="space-y-2">
            <Label htmlFor="baseUrl">Base URL</Label>
            <Input
              id="baseUrl"
              value={baseUrl}
              onChange={(e) => setBaseUrl(e.target.value)}
              placeholder={DEFAULT_BASE_URL}
            />
          </div>
          <div className="space-y-2">
            <Label htmlFor="apiToken">API token (optional)</Label>
            <Input
              id="apiToken"
              type="password"
              value={apiToken}
              onChange={(e) => setApiToken(e.target.value)}
              placeholder="X-Api-Token value"
            />
          </div>
          <div className="flex gap-2">
            <Button onClick={save}>Save</Button>
            <Button variant="outline" onClick={testConnection} disabled={testing}>
              {testing ? "Testing…" : "Test connection"}
            </Button>
          </div>
        </CardContent>
      </Card>
    </div>
  );
}
