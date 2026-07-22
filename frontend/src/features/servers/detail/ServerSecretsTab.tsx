import { useState } from "react";
import { Eye, EyeOff, RefreshCw, Trash2 } from "lucide-react";
import { toast } from "sonner";
import { Skeleton } from "@/components/ui/skeleton";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import { api } from "@/api/endpoints";
import {
  useDeleteServerSecret,
  useRegenerateServerSecret,
  useServerSecrets,
  useSetServerSecrets,
} from "@/api/queries";
import { toastApiError } from "@/lib/errors";
import { primaryBtn } from "./ui";

// Friendly copy for the curated store keys a game template can reference
// (CuratedGameTemplates.cs → secretKeyRefs). Unknown keys fall back to the raw
// key name with a generic hint. `managed` keys (RCON passwords) are auto-filled
// by the app, so their hints describe reveal/regenerate rather than entry.
const SECRET_META: Record<string, { label: string; hint: string }> = {
  SRCDS_TOKEN: {
    label: "Steam GSLT token",
    hint: "Game server login token from steamcommunity.com/dev/managegameservers. Steam issues one per server, so give each server its own.",
  },
  CS2_RCONPW: {
    label: "CS2 RCON password",
    hint: "Auto-generated for RCON admin commands on this CS2 server. Reveal it to use an external tool, regenerate it, or type your own to override.",
  },
  RCON_PASSWORD: {
    label: "RCON password",
    hint: "Auto-generated for RCON admin commands on this Minecraft server. Reveal it to use an external tool, regenerate it, or type your own to override.",
  },
};

// Fixed-length dots stand in for a configured value; the real value is only
// fetched on an explicit reveal.
const MASK = "••••••••";

export function ServerSecretsTab({ serverName }: { serverName: string }) {
  const secrets = useServerSecrets(serverName);
  const setSecrets = useSetServerSecrets(serverName);
  const deleteSecret = useDeleteServerSecret(serverName);
  const regenerateSecret = useRegenerateServerSecret(serverName);

  // Typed drafts, keyed by secret key. A key absent here is "untouched", which
  // renders as dots when configured. `fetched` remembers a revealed value so
  // hiding an untouched reveal can drop back to dots instead of re-sending the
  // unchanged value on save.
  const [values, setValues] = useState<Record<string, string>>({});
  const [visible, setVisible] = useState<Record<string, boolean>>({});
  const [fetched, setFetched] = useState<Record<string, string>>({});
  const [revealing, setRevealing] = useState<string | null>(null);
  const [pendingDelete, setPendingDelete] = useState<string | null>(null);
  const [pendingRegen, setPendingRegen] = useState<string | null>(null);

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
  // Only user-supplied secrets gate start; app-managed ones are auto-generated.
  const missing = secrets.data.filter((s) => !s.managed && !s.configured).map((s) => s.key);

  // Drops any local reveal/draft state for a key so it falls back to dots.
  const resetLocal = (key: string) => {
    setValues(({ [key]: _, ...rest }) => rest);
    setFetched(({ [key]: _, ...rest }) => rest);
    setVisible((v) => ({ ...v, [key]: false }));
  };

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
        resetLocal(key);
      },
      onError: (error) => {
        setPendingDelete(null);
        toastApiError(error);
      },
    });
  };

  const regenerateKey = (key: string) => {
    if (pendingRegen !== key) {
      setPendingRegen(key);
      return;
    }
    regenerateSecret.mutate(key, {
      onSuccess: () => {
        toast.success(`${key} regenerated.`, {
          description: "A new value was generated and the container recreated.",
        });
        setPendingRegen(null);
        // The previously revealed value is now stale — drop back to dots.
        resetLocal(key);
      },
      onError: (error) => {
        setPendingRegen(null);
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

  const smallIconBtn =
    "flex size-7 cursor-pointer items-center justify-center rounded-[6px] text-[#8795a3] transition-colors hover:bg-secondary hover:text-foreground disabled:cursor-default disabled:opacity-50";

  return (
    <div>
      {missing.length > 0 && (
        <div className="mb-4 rounded-lg border border-[#f3e2bd] bg-[#fff8ec] px-[18px] py-[13px] text-[13.5px] text-[#7a5c1e]">
          <span className="font-bold">
            This server needs {missing.length === 1 ? "a secret" : "secrets"} before it can start.
          </span>{" "}
          Set <span className="font-mono text-xs">{missing.join(", ")}</span> below — the server
          won't start until {missing.length === 1 ? "it's" : "they're"} set.
        </div>
      )}

      <div className="space-y-7 rounded-[10px] border bg-card px-[30px] py-[26px]">
        {secrets.data.map(({ key, managed }) => {
          const meta = SECRET_META[key];
          const untouched = !(key in values);
          const showsMask = untouched && configured(key);
          return (
            <div key={key}>
              <div className="flex flex-wrap items-center gap-2.5">
                <span className="text-[15.5px] font-bold">{meta ? meta.label : key}</span>
                <span className="font-mono text-[11px] text-[#9aa7b4]">{key}</span>
                {managed ? (
                  <span className="rounded-full bg-secondary px-2.5 py-0.5 text-[11.5px] font-semibold text-secondary-foreground">
                    Managed
                  </span>
                ) : (
                  configured(key) && (
                    <span className="rounded-full bg-[#e4f7ee] px-2.5 py-0.5 text-[11.5px] font-semibold text-[#1d7a51]">
                      Set
                    </span>
                  )
                )}
                {configured(key) && (
                  <button
                    type="button"
                    className={`${smallIconBtn} ml-1`}
                    title={visible[key] ? "Hide value" : "Show current value"}
                    disabled={revealing === key}
                    onClick={() => void toggleReveal(key)}
                  >
                    {visible[key] ? <EyeOff className="size-4" /> : <Eye className="size-4" />}
                  </button>
                )}
                {managed &&
                  (pendingRegen === key ? (
                    <button
                      type="button"
                      className="cursor-pointer rounded-[6px] bg-secondary px-3 py-1 text-xs font-bold text-secondary-foreground transition hover:brightness-105"
                      disabled={regenerateSecret.isPending}
                      onClick={() => regenerateKey(key)}
                    >
                      Confirm regenerate
                    </button>
                  ) : (
                    <button
                      type="button"
                      className={smallIconBtn}
                      title="Generate a new value"
                      onClick={() => regenerateKey(key)}
                    >
                      <RefreshCw className="size-4" />
                    </button>
                  ))}
                {!managed &&
                  configured(key) &&
                  (pendingDelete === key ? (
                    <button
                      type="button"
                      className="cursor-pointer rounded-[6px] bg-[#b0433f] px-3 py-1 text-xs font-bold text-white transition hover:brightness-105"
                      disabled={deleteSecret.isPending}
                      onClick={() => removeKey(key)}
                    >
                      Confirm clear
                    </button>
                  ) : (
                    <button
                      type="button"
                      className={`${smallIconBtn} hover:bg-[#fbf1f0] hover:text-[#b0433f]`}
                      title="Clear this secret"
                      onClick={() => removeKey(key)}
                    >
                      <Trash2 className="size-4" />
                    </button>
                  ))}
              </div>
              <input
                id={`secret-${key}`}
                type={visible[key] ? "text" : "password"}
                className={`mt-3 w-full rounded-[6px] border border-input bg-[#fbfdfe] px-4 py-[11px] text-sm text-foreground outline-none transition-shadow placeholder:text-[#9aa7b4] focus:border-primary focus:shadow-[0_0_0_3px_rgba(92,200,255,0.25)] ${visible[key] ? "font-mono" : ""}`}
                placeholder={configured(key) ? "••••••••  (keep current)" : undefined}
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
              {meta && <p className="mt-2.5 text-[13px] text-[#8795a3]">{meta.hint}</p>}
            </div>
          );
        })}

        <button type="button" className={primaryBtn} onClick={save} disabled={setSecrets.isPending}>
          {setSecrets.isPending ? "Saving…" : "Save secrets"}
        </button>
      </div>

      <p className="mt-3.5 max-w-[780px] text-[13px] leading-[1.6] text-[#8795a3]">
        Stored encrypted on this machine, never in plain text, and scoped to this server. Fields
        marked <b>Managed</b> are generated automatically — reveal, regenerate, or type your own to
        override. Fields showing dots already have a value. Saving restarts the server if it's
        running.
      </p>
    </div>
  );
}
