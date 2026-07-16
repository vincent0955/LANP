import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { api } from "./endpoints";
import type { DeployServerRequest } from "./types";

// Central key factory — the SignalR event bridge patches these same keys, so
// they must never be constructed ad hoc in components.
export const queryKeys = {
  health: ["health"] as const,
  setup: ["setup"] as const,
  servers: ["servers"] as const,
  server: (name: string) => ["servers", name] as const,
  config: (name: string) => ["servers", name, "config"] as const,
  games: (search: string) => ["games", search] as const,
  metrics: ["metrics"] as const,
  network: ["network"] as const,
  minecraftVersions: (type: string) => ["minecraft", "versions", type] as const,
  minecraftSearch: (params: Record<string, string>) => ["minecraft", "search", params] as const,
};

// Health is the one polled query: it's the app's liveness heartbeat toward the
// backend itself (everything else is pushed over SignalR), and it must keep
// firing while the backend is down so the Disconnected screen can self-heal.
export function useHealth() {
  return useQuery({
    queryKey: queryKeys.health,
    queryFn: api.health,
    refetchInterval: 10_000,
    retry: false,
  });
}

export function useSetupStatus() {
  return useQuery({ queryKey: queryKeys.setup, queryFn: api.setupStatus });
}

export function useServers() {
  return useQuery({ queryKey: queryKeys.servers, queryFn: api.listServers });
}

export function useServer(name: string) {
  return useQuery({ queryKey: queryKeys.server(name), queryFn: () => api.getServer(name) });
}

export function useServerConfig(name: string) {
  return useQuery({ queryKey: queryKeys.config(name), queryFn: () => api.getConfig(name) });
}

export function useGames(search: string) {
  return useQuery({ queryKey: queryKeys.games(search), queryFn: () => api.listGames(search) });
}

// Version lists change on Minecraft's release cadence, not ours — cache long and
// don't hammer retries; the deploy form degrades to free-text on error.
export function useMinecraftVersions(type: string | null) {
  return useQuery({
    queryKey: queryKeys.minecraftVersions(type ?? "none"),
    queryFn: () => api.minecraftVersions(type as string),
    enabled: type !== null,
    staleTime: 30 * 60_000,
    retry: 1,
  });
}

export function useMinecraftContentSearch(params: {
  q: string;
  kind: "mod" | "plugin" | "modpack" | null;
  loader?: string;
  mcVersion?: string;
}) {
  const { q, kind, loader, mcVersion } = params;
  return useQuery({
    queryKey: queryKeys.minecraftSearch({
      q,
      kind: kind ?? "",
      loader: loader ?? "",
      mcVersion: mcVersion ?? "",
    }),
    queryFn: () => api.minecraftContentSearch({ q, kind: kind as "mod", loader, mcVersion }),
    enabled: kind !== null,
    staleTime: 5 * 60_000,
    // Keep showing the previous hits while a new keystroke's search is in
    // flight, so the results list doesn't flicker empty between queries.
    placeholderData: (prev) => prev,
    retry: 1,
  });
}

export function useMetrics() {
  return useQuery({ queryKey: queryKeys.metrics, queryFn: api.metrics });
}

// The machine's LAN address changes rarely (new network, DHCP renewal), so one
// fetch per session is plenty.
export function useNetworkInfo() {
  return useQuery({
    queryKey: queryKeys.network,
    queryFn: api.networkInfo,
    staleTime: Infinity,
  });
}

export function useDeployServer() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (req: DeployServerRequest) => api.deployServer(req),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: queryKeys.servers }),
  });
}

export function useScaleServer(name: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (replicas: 0 | 1) => api.scaleServer(name, replicas),
    onSuccess: (updated) => {
      queryClient.setQueryData(queryKeys.server(name), updated);
      queryClient.invalidateQueries({ queryKey: queryKeys.servers });
    },
  });
}

export function useDeleteServer() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ name, deleteData }: { name: string; deleteData: boolean }) =>
      api.deleteServer(name, deleteData),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: queryKeys.servers }),
  });
}

export function useUpdateConfig(name: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (values: Record<string, string>) => api.updateConfig(name, values),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: queryKeys.config(name) }),
  });
}

export function useSetSecrets() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (values: Record<string, string>) => api.setSecrets(values),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: queryKeys.setup }),
  });
}

export function useDeleteSecretKey() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (key: string) => api.deleteSecretKey(key),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: queryKeys.setup }),
  });
}
