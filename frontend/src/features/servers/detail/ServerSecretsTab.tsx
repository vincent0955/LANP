import { useState } from "react";
import { Eye, EyeOff, Trash2, TriangleAlert } from "lucide-react";
import { toast } from "sonner";
import { Button } from "@/components/ui/button";
import { Badge } from "@/components/ui/badge";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Skeleton } from "@/components/ui/skeleton";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import { api } from "@/api/endpoints";
import {
  useDeleteServerSecret,
  useServerSecrets,
  useSetServerSecrets,
} from "@/api/queries";
import { toastApiError } from "@/lib/errors";

// Friendly copy for the curated store keys a game template can reference
// (CuratedGameTemplates.cs → secretKeyRefs). Unknown keys fall back to the raw
// key name with a generic hint.
const SECRET_META: Record<string, { label: string; hint: string }> = {
  SRCDS_TOKEN: {
    label: "Steam GSLT token",
    hint: "Game server login token from steamcommunity.com/dev/managegameservers. Steam issues one per server, so give each server its own.",
  },
  CS2_RCONPW: {
    label: "CS2 RCON password",
    hint: "Password for RCON admin commands on this CS2 server.",
  },
  RCON_PASSWORD: {
    label: "RCON password",
    hint: "Password for RCON admin commands on this Minecraft server.",
  },
};

// Fixed-length dots stand in for a configured value; the real value is only
// fetched on an explicit reveal.
const MASK = "••••••••";

export function ServerSecretsTab({ serverName }: { serverName: string }) {
  const secrets = useServerSecrets(serverName);
  const setSecrets = useSetServerSecrets(serverName);
  const deleteSecret = useDeleteServerSecret(serverName);

  // Typed drafts, keyed by secret key. A key absent here is "untouched", which
  // renders as dots when configured. `fetched` remembers a revealed value so
  // hiding an untouched reveal can drop back to dots instead of re-sending the
  // unchanged value on save.
  const [values, setValues] = useState<Record<string, string>>({});
  const [visible, setVisible] = useState<Record<string, boolean>>({});
  const [fetched, setFetched] = useState<Record<string, string>>({});
  const [revealing, setRevealing] = useState<string | null>(null);
  const [pendingDelete, setPendingDelete] = useState<string | null>(null);

  if (secrets.isPending) return <Skeleton className="h-48" />;
  if (secrets.isError) {
    return (
      <Alert variant="destructive">
        <AlertTitle>Could not load secrets</AlertTitle>
        <AlertDescription>{secrets.error.message}</AlertDescription>
      </Alert>
    );
  }

  const configured = (key: string) => secrets.data.some((s) => s.key === key && s.configured);
  const missing = secrets.data.filter((s) => !s.configured).map((s) => s.key);

  const toggleReveal = async (key: string) => {
    if (visible[key]) {
      if (key in fetched && (values[key] ?? "") === fetched[key]) {
        setValues(({ [key]: _, ...rest }) => rest);
        setFetched(({ [key]: _, ...rest }) => rest);
      }
      setVisible((v) => ({ ...v, [key]: false }));
      return;
    }
    // Untouched configured field (showing dots): pull the stored value into the
    // input for in-place editing. Otherwise just unmask the typed draft.
    if ((values[key] ?? "") === "" && configured(key)) {
      setRevealing(key);
      try {
        const { value } = await api.getServerSecretValue(serverName, key);
        setValues((v) => ({ ...v, [key]: value }));
        setFetched((f) => ({ ...f, [key]: value }));
      } catch (error) {
        toastApiError(error);
        return;
      } finally {
        setRevealing(null);
      }
    }
    setVisible((v) => ({ ...v, [key]: true }));
  };

  const removeKey = (key: string) => {
    if (pendingDelete !== key) {
      setPendingDelete(key);
      return;
    }
    deleteSecret.mutate(key, {
      onSuccess: () => {
        toast.success(`${key} cleared.`);
        setPendingDelete(null);
        setVisible(({ [key]: _, ...rest }) => rest);
        setFetched(({ [key]: _, ...rest }) => rest);
        setValues(({ [key]: _, ...rest }) => rest);
      },
      onError: (error) => {
        setPendingDelete(null);
        toastApiError(error);
      },
    });
  };

  const save = () => {
    const payload: Record<string, string> = {};
    for (const [key, value] of Object.entries(values)) {
      if (value.trim()) payload[key] = value;
    }
    if (Object.keys(payload).length === 0) {
      toast.error("Enter a value for at least one secret.");
      return;
    }
    setSecrets.mutate(payload, {
      onSuccess: () => {
        toast.success("Secrets saved.", {
          description: "The container was recreated so the new values take effect.",
        });
        setValues({});
        setVisible({});
        setFetched({});
      },
      onError: toastApiError,
    });
  };

  return (
    <div className="space-y-4">
      {missing.length > 0 && (
        <Alert>
          <TriangleAlert className="size-4" />
          <AlertTitle>This server needs {missing.length === 1 ? "a secret" : "secrets"} before it can start</AlertTitle>
          <AlertDescription>
            Set <span className="font-mono">{missing.join(", ")}</span> below. The server won't start until{" "}
            {missing.length === 1 ? "it's" : "they're"} set.
          </AlertDescription>
        </Alert>
      )}

      <div className="space-y-4 rounded-md border p-4">
        {secrets.data.map(({ key }) => {
          const meta = SECRET_META[key];
          const untouched = !(key in values);
          const showsMask = untouched && configured(key);
          return (
            <div key={key} className="space-y-1.5">
              <div className="flex items-center gap-2">
                <Label htmlFor={`secret-${key}`}>
                  {meta ? meta.label : key} <span className="font-mono text-xs text-muted-foreground">({key})</span>
                </Label>
                {configured(key) && (
                  <>
                    <Badge variant="secondary">Set</Badge>
                    <Button
                      variant="ghost"
                      size="icon"
                      className="size-6"
                      title={visible[key] ? "Hide value" : "Show current value"}
                      disabled={revealing === key}
                      onClick={() => toggleReveal(key)}
                    >
                      {visible[key] ? <EyeOff className="size-4" /> : <Eye className="size-4" />}
                    </Button>
                    {pendingDelete === key ? (
                      <Button
                        variant="destructive"
                        size="sm"
                        className="h-6"
                        disabled={deleteSecret.isPending}
                        onClick={() => removeKey(key)}
                      >
                        Confirm clear
                      </Button>
                    ) : (
                      <Button
                        variant="ghost"
                        size="icon"
                        className="size-6"
                        title="Clear this secret"
                        onClick={() => removeKey(key)}
                      >
                        <Trash2 className="size-4" />
                      </Button>
                    )}
                  </>
                )}
              </div>
              <Input
                id={`secret-${key}`}
                type={visible[key] ? "text" : "password"}
                className={visible[key] ? "font-mono" : undefined}
                value={showsMask ? MASK : (values[key] ?? "")}
                onFocus={() => {
                  // Clear the dots so typing starts a fresh draft instead of
                  // appending to the mask characters.
                  if (showsMask) setValues((v) => ({ ...v, [key]: "" }));
                }}
                onBlur={() => {
                  // An emptied field reverts to the untouched state (dots when
                  // configured); empty drafts are never sent on save anyway.
                  if (!untouched && values[key] === "") {
                    setValues(({ [key]: _, ...rest }) => rest);
                    setFetched(({ [key]: _, ...rest }) => rest);
                    setVisible((v) => ({ ...v, [key]: false }));
                  }
                }}
                onChange={(e) => setValues((v) => ({ ...v, [key]: e.target.value }))}
              />
              {meta && <p className="text-xs text-muted-foreground">{meta.hint}</p>}
            </div>
          );
        })}

        <Button onClick={save} disabled={setSecrets.isPending}>
          {setSecrets.isPending ? "Saving…" : "Save secrets"}
        </Button>
      </div>

      <p className="text-xs text-muted-foreground">
        Stored encrypted on this machine, never in plain text, and scoped to this server. Fields showing dots
        already have a value — click the eye to reveal and edit it. Saving recreates the container, so restart
        the server if it's running.
      </p>
    </div>
  );
}
