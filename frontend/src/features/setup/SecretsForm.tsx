import { useState } from "react";
import { Plus, X } from "lucide-react";
import { toast } from "sonner";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { useSetSecrets } from "@/api/queries";
import { toastApiError } from "@/lib/errors";

// Secret keys the curated game templates reference via secretKeyRefs
// (CuratedGameTemplates.cs). Values are write-only: the backend never exposes
// them back, so fields always start empty and only non-empty fields are sent.
const KNOWN_SECRETS: { key: string; label: string; hint: string }[] = [
  {
    key: "SRCDS_TOKEN",
    label: "Steam GSLT token (SRCDS_TOKEN)",
    hint: "Game server login token from steamcommunity.com/dev/managegameservers — required by CS2.",
  },
  {
    key: "CS2_RCONPW",
    label: "CS2 RCON password (CS2_RCONPW)",
    hint: "Password for RCON commands on CS2 servers.",
  },
  {
    key: "RCON_PASSWORD",
    label: "RCON password (RCON_PASSWORD)",
    hint: "Password for RCON commands on Minecraft servers.",
  },
];

export function SecretsForm() {
  const setSecrets = useSetSecrets();
  const [values, setValues] = useState<Record<string, string>>({});
  const [custom, setCustom] = useState<{ key: string; value: string }[]>([]);

  const save = () => {
    const payload: Record<string, string> = {};
    for (const [key, value] of Object.entries(values)) {
      if (value.trim()) payload[key] = value;
    }
    for (const { key, value } of custom) {
      if (key.trim() && value.trim()) payload[key.trim()] = value;
    }
    if (Object.keys(payload).length === 0) {
      toast.error("Fill in at least one secret value.");
      return;
    }
    setSecrets.mutate(payload, {
      onSuccess: () => {
        toast.success("Secrets saved to the cluster.");
        setValues({});
        setCustom([]);
      },
      onError: toastApiError,
    });
  };

  return (
    <Card>
      <CardHeader>
        <CardTitle>Secrets</CardTitle>
        <CardDescription>
          Stored in the cluster's game-secrets Secret. Write-only: existing values are never shown
          here — leave a field empty to keep its current value.
        </CardDescription>
      </CardHeader>
      <CardContent className="space-y-4">
        {KNOWN_SECRETS.map(({ key, label, hint }) => (
          <div key={key} className="space-y-1.5">
            <Label htmlFor={`secret-${key}`}>{label}</Label>
            <Input
              id={`secret-${key}`}
              type="password"
              value={values[key] ?? ""}
              onChange={(e) => setValues((v) => ({ ...v, [key]: e.target.value }))}
            />
            <p className="text-xs text-muted-foreground">{hint}</p>
          </div>
        ))}

        {custom.map((entry, i) => (
          <div key={i} className="flex items-end gap-2">
            <div className="flex-1 space-y-1.5">
              {i === 0 && <Label>Custom key</Label>}
              <Input
                placeholder="KEY_NAME"
                className="font-mono"
                value={entry.key}
                onChange={(e) =>
                  setCustom((c) => c.map((x, j) => (j === i ? { ...x, key: e.target.value } : x)))
                }
              />
            </div>
            <div className="flex-1 space-y-1.5">
              {i === 0 && <Label>Value</Label>}
              <Input
                type="password"
                value={entry.value}
                onChange={(e) =>
                  setCustom((c) => c.map((x, j) => (j === i ? { ...x, value: e.target.value } : x)))
                }
              />
            </div>
            <Button
              variant="ghost"
              size="icon"
              onClick={() => setCustom((c) => c.filter((_, j) => j !== i))}
            >
              <X className="size-4" />
            </Button>
          </div>
        ))}

        <div className="flex gap-2">
          <Button
            variant="outline"
            size="sm"
            onClick={() => setCustom((c) => [...c, { key: "", value: "" }])}
          >
            <Plus className="size-4" />
            Add custom secret
          </Button>
        </div>

        <Button onClick={save} disabled={setSecrets.isPending}>
          {setSecrets.isPending ? "Saving…" : "Save secrets"}
        </Button>
      </CardContent>
    </Card>
  );
}
