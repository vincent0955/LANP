import { useState } from "react";
import { Eye, EyeOff, Plus, Trash2, X } from "lucide-react";
import { toast } from "sonner";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Badge } from "@/components/ui/badge";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { api } from "@/api/endpoints";
import { useDeleteSecretKey, useSetSecrets, useSetupStatus } from "@/api/queries";
import { toastApiError } from "@/lib/errors";

// Secret keys the curated game templates reference via secretKeyRefs
// (CuratedGameTemplates.cs). Configured fields display MASK dots (fixed
// length — the real value is only fetched on explicit reveal); the eye swaps
// the dots for the stored value in place, where it can be edited. Only
// user-entered (non-empty) fields are sent on save.
const MASK = "••••••••";

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
  const deleteSecret = useDeleteSecretKey();
  const setup = useSetupStatus();
  // Drafts the user is editing; a key absent here means "untouched", which
  // renders as MASK dots when the key is configured. `fetched` remembers the
  // stored value pulled in on reveal so hiding an untouched reveal can fall
  // back to dots instead of re-sending the unchanged value on save.
  const [values, setValues] = useState<Record<string, string>>({});
  const [custom, setCustom] = useState<{ key: string; value: string }[]>([]);
  const [visible, setVisible] = useState<Record<string, boolean>>({});
  const [fetched, setFetched] = useState<Record<string, string>>({});
  const [revealing, setRevealing] = useState<string | null>(null);
  const [pendingDelete, setPendingDelete] = useState<string | null>(null);

  // Keys already stored in the cluster (names only; values are fetched one at a
  // time on explicit reveal). Custom keys not in KNOWN_SECRETS get their own
  // field so they remain visible and editable after saving.
  const configuredKeys = setup.data?.configuredSecretKeys ?? [];
  const configuredCustomKeys = configuredKeys.filter(
    (key) => !KNOWN_SECRETS.some((s) => s.key === key),
  );

  const toggleReveal = async (key: string) => {
    if (visible[key]) {
      if (key in fetched && (values[key] ?? "") === fetched[key]) {
        setValues(({ [key]: _, ...rest }) => rest);
        setFetched(({ [key]: _, ...rest }) => rest);
      }
      setVisible((v) => ({ ...v, [key]: false }));
      return;
    }
    // Untouched configured field (showing dots): pull the stored value into
    // the input for in-place editing. Otherwise just unmask the typed draft.
    if ((values[key] ?? "") === "" && configuredKeys.includes(key)) {
      setRevealing(key);
      try {
        const { value } = await api.getSecretValue(key);
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
        toast.success(`Secret ${key} deleted.`);
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

  const configuredControls = (key: string, deletable: boolean) => (
    <>
      <Badge variant="secondary">Configured</Badge>
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
      {deletable &&
        (pendingDelete === key ? (
          <Button
            variant="destructive"
            size="sm"
            className="h-6"
            disabled={deleteSecret.isPending}
            onClick={() => removeKey(key)}
          >
            Confirm delete
          </Button>
        ) : (
          <Button
            variant="ghost"
            size="icon"
            className="size-6"
            title="Delete this secret"
            onClick={() => removeKey(key)}
          >
            <Trash2 className="size-4" />
          </Button>
        ))}
    </>
  );

  const secretInput = (key: string) => {
    const untouched = !(key in values);
    const showsMask = untouched && configuredKeys.includes(key);
    return (
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
    );
  };

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
        setVisible({});
        setFetched({});
      },
      onError: toastApiError,
    });
  };

  return (
    <Card>
      <CardHeader>
        <CardTitle>Secrets</CardTitle>
        <CardDescription>
          Stored in the cluster's game-secrets Secret. Fields showing dots already have a value —
          click the eye to reveal and edit it in place. Leave a field untouched to keep its
          current value.
        </CardDescription>
      </CardHeader>
      <CardContent className="space-y-4">
        {KNOWN_SECRETS.map(({ key, label, hint }) => (
          <div key={key} className="space-y-1.5">
            <div className="flex items-center gap-2">
              <Label htmlFor={`secret-${key}`}>{label}</Label>
              {configuredKeys.includes(key) && configuredControls(key, false)}
            </div>
            {secretInput(key)}
            <p className="text-xs text-muted-foreground">{hint}</p>
          </div>
        ))}

        {configuredCustomKeys.map((key) => (
          <div key={key} className="space-y-1.5">
            <div className="flex items-center gap-2">
              <Label htmlFor={`secret-${key}`} className="font-mono">
                {key}
              </Label>
              {configuredControls(key, true)}
            </div>
            {secretInput(key)}
            <p className="text-xs text-muted-foreground">
              Custom secret — leave untouched to keep the current value.
            </p>
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
