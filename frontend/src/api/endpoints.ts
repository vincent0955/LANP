import { http } from "./http";
import type {
  ClusterHealth,
  DeployServerRequest,
  GameTemplate,
  MetricsSnapshot,
  MinecraftVersionsResponse,
  ModrinthSearchResponse,
  NetworkInfo,
  RconCommandResponse,
  RuntimeStatus,
  ServerDetail,
  ServerSummary,
  SetupStatus,
} from "./types";

export const api = {
  health: () => http.get<ClusterHealth>("/api/health"),

  setupStatus: () => http.get<SetupStatus>("/api/setup/status"),
  setSecrets: (values: Record<string, string>) => http.post<void>("/api/setup/secrets", values),
  getSecretValue: (key: string) =>
    http.get<{ value: string }>(`/api/setup/secrets/${encodeURIComponent(key)}/value`),
  deleteSecretKey: (key: string) =>
    http.del<void>(`/api/setup/secrets/${encodeURIComponent(key)}`),

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

  sendRcon: (name: string, command: string) =>
    http.post<RconCommandResponse>(`/api/servers/${encodeURIComponent(name)}/rcon`, { command }),

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

  metrics: () => http.get<MetricsSnapshot>("/api/metrics"),

  networkInfo: () => http.get<NetworkInfo>("/api/network"),
};
