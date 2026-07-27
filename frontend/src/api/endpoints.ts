import { http } from "./http";
import { useSettings } from "@/lib/settings";
import type {
  BackupInfo,
  BackupSettings,
  ClusterHealth,
  DeployServerRequest,
  ForwardingGuide,
  GameTemplate,
  MetricsSnapshot,
  MinecraftVersionsResponse,
  ModrinthSearchResponse,
  NetworkInfo,
  RangeReachability,
  RconCommandResponse,
  RuntimeMode,
  RuntimeStatus,
  ServerDetail,
  ServerReachability,
  ServerSecretInfo,
  ServerSummary,
  SetupStatus,
} from "./types";

export const api = {
  health: () => http.get<ClusterHealth>("/api/health"),

  setupStatus: () => http.get<SetupStatus>("/api/setup/status"),

  listServers: () => http.get<ServerSummary[]>("/api/servers"),
  getServer: (name: string) => http.get<ServerDetail>(`/api/servers/${encodeURIComponent(name)}`),
  deployServer: (req: DeployServerRequest) => http.post<ServerDetail>("/api/servers", req),
  scaleServer: (name: string, replicas: 0 | 1) =>
    http.post<ServerDetail>(`/api/servers/${encodeURIComponent(name)}/scale`, { replicas }),
  deleteServer: (name: string, deleteData: boolean) =>
    http.del<void>(`/api/servers/${encodeURIComponent(name)}?deleteData=${deleteData}`),

  getConfig: (name: string) =>
    http.get<Record<string, string>>(`/api/servers/${encodeURIComponent(name)}/config`),
  updateConfig: (name: string, values: Record<string, string>) =>
    http.put<void>(`/api/servers/${encodeURIComponent(name)}/config`, values),

  getServerSecrets: (name: string) =>
    http.get<ServerSecretInfo[]>(`/api/servers/${encodeURIComponent(name)}/secrets`),
  setServerSecrets: (name: string, values: Record<string, string>) =>
    http.post<void>(`/api/servers/${encodeURIComponent(name)}/secrets`, values),
  getServerSecretValue: (name: string, key: string) =>
    http.get<{ value: string }>(
      `/api/servers/${encodeURIComponent(name)}/secrets/${encodeURIComponent(key)}/value`,
    ),
  deleteServerSecret: (name: string, key: string) =>
    http.del<void>(
      `/api/servers/${encodeURIComponent(name)}/secrets/${encodeURIComponent(key)}`,
    ),
  regenerateServerSecret: (name: string, key: string) =>
    http.post<void>(
      `/api/servers/${encodeURIComponent(name)}/secrets/${encodeURIComponent(key)}/regenerate`,
    ),

  sendRcon: (name: string, command: string) =>
    http.post<RconCommandResponse>(`/api/servers/${encodeURIComponent(name)}/rcon`, { command }),

  listBackups: (name: string) =>
    http.get<BackupInfo[]>(`/api/servers/${encodeURIComponent(name)}/backups`),
  createBackup: (name: string) =>
    http.post<BackupInfo>(`/api/servers/${encodeURIComponent(name)}/backups`, {}),
  restoreBackup: (name: string, id: string) =>
    http.post<void>(
      `/api/servers/${encodeURIComponent(name)}/backups/${encodeURIComponent(id)}/restore`,
      {},
    ),
  deleteBackup: (name: string, id: string) =>
    http.del<void>(`/api/servers/${encodeURIComponent(name)}/backups/${encodeURIComponent(id)}`),
  // Binary download: fetched as a blob (with the auth token when set) and saved
  // via a temporary object URL, rather than routed through http() which parses JSON.
  downloadBackup: async (name: string, id: string) => {
    const { baseUrl, apiToken } = useSettings.getState();
    const res = await fetch(
      `${baseUrl}/api/servers/${encodeURIComponent(name)}/backups/${encodeURIComponent(id)}/download`,
      { headers: apiToken ? { "X-Api-Token": apiToken } : undefined },
    );
    if (!res.ok) throw new Error(`Download failed (${res.status}).`);
    const blob = await res.blob();
    const url = URL.createObjectURL(blob);
    const anchor = document.createElement("a");
    anchor.href = url;
    anchor.download = `${name}-${id}.tar.gz`;
    document.body.appendChild(anchor);
    anchor.click();
    anchor.remove();
    URL.revokeObjectURL(url);
  },

  listGames: (search?: string) =>
    http.get<GameTemplate[]>(`/api/games${search ? `?search=${encodeURIComponent(search)}` : ""}`),

  minecraftVersions: (type: string) =>
    http.get<MinecraftVersionsResponse>(`/api/minecraft/versions?type=${encodeURIComponent(type)}`),
  minecraftContentSearch: (params: {
    q?: string;
    kind: "mod" | "plugin" | "modpack";
    loader?: string;
    mcVersion?: string;
  }) => {
    const search = new URLSearchParams({ kind: params.kind });
    if (params.q) search.set("q", params.q);
    if (params.loader) search.set("loader", params.loader);
    if (params.mcVersion) search.set("mcVersion", params.mcVersion);
    return http.get<ModrinthSearchResponse>(`/api/minecraft/content/search?${search}`);
  },

  runtimeStatus: () => http.get<RuntimeStatus>("/api/runtime/status"),
  runtimeInstall: () => http.post<void>("/api/runtime/install", {}),
  runtimeEnableMirrored: () =>
    http.post<{ applied: boolean; shutdownRequired: boolean }>(
      "/api/runtime/networking/mirrored",
      {},
    ),
  /** Persists the container-engine choice; the app must restart to apply it. */
  runtimeSetMode: (mode: RuntimeMode) =>
    http.put<{ mode: RuntimeMode; restartRequired: boolean; runningServers?: number }>(
      "/api/runtime/mode",
      { mode },
    ),

  metrics: () => http.get<MetricsSnapshot>("/api/metrics"),

  networkInfo: () => http.get<NetworkInfo>("/api/network"),
  reachability: (name: string) =>
    http.get<ServerReachability>(`/api/network/reachability/${encodeURIComponent(name)}`),
  rangeReachability: () => http.get<RangeReachability>("/api/network/reachability"),
  forwardingGuide: (name: string) =>
    http.get<ForwardingGuide>(
      `/api/network/servers/${encodeURIComponent(name)}/forwarding-guide`,
    ),

  backupSettings: () => http.get<BackupSettings>("/api/backups/settings"),
  updateBackupSettings: (settings: BackupSettings) =>
    http.put<BackupSettings>("/api/backups/settings", settings),
};
