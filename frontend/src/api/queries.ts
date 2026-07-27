import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { api } from "./endpoints";
import type { BackupSettings, DeployServerRequest, RuntimeMode } from "./types";

// Central key factory — the SignalR event bridge patches these same keys, so
// they must never be constructed ad hoc in components.
export const queryKeys = {
  health: ["health"] as const,
  setup: ["setup"] as const,
  servers: ["servers"] as const,
  server: (name: string) => ["servers", name] as const,
  config: (name: string) => ["servers", name, "config"] as const,
  serverSecrets: (name: string) => ["servers", name, "secrets"] as const,
  backups: (name: string) => ["servers", name, "backups"] as const,
  games: (search: string) => ["games", search] as const,
  metrics: ["metrics"] as const,
  network: ["network"] as const,
  forwardingGuide: (name: string) => ["network", "forwarding-guide", name] as const,
  backupSettings: ["backups", "settings"] as const,
  runtime: ["runtime"] as const,
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

// Forwarding instructions are derived from the server's fixed allocated ports —
// they don't change unless the server is redeployed, so cache generously.
export function useForwardingGuide(name: string) {
  return useQuery({
    queryKey: queryKeys.forwardingGuide(name),
    queryFn: () => api.forwardingGuide(name),
    staleTime: 5 * 60_000,
  });
}

// On-demand connectivity test: a GET run through a mutation so the "Test" button
// gets explicit pending/result state instead of auto-fetching on mount.
export function useReachabilityTest(name: string) {
  return useMutation({ mutationFn: () => api.reachability(name) });
}

// Same idea, but for the whole forwarded port window (Setup → open all ports).
export function useRangeReachabilityTest() {
  return useMutation({ mutationFn: () => api.rangeReachability() });
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

/**
 * Bundled runtime state. Polls while an install is in flight (the backend
 * runs it in the background; progress only surfaces via this endpoint) and
 * goes quiet once the phase settles.
 */
export function useRuntimeStatus() {
  return useQuery({
    queryKey: queryKeys.runtime,
    queryFn: api.runtimeStatus,
    refetchInterval: (query) => {
      const data = query.state.data;
      const phase = data?.phase;
      const installing =
        phase === "InstallingWsl" ||
        phase === "DownloadingDistro" ||
        phase === "ImportingDistro" ||
        phase === "StartingEngine";
      // Also poll while an already-imported runtime is booting (auto-start
      // leaves the phase at Idle, so key off engine reachability instead) so
      // the startup splash and the Setup screen refresh once it comes up.
      const booting = data !== undefined && data.distroImported && !data.engineReachable;
      return installing || booting ? 2_000 : false;
    },
  });
}

export function useInstallRuntime() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: () => api.runtimeInstall(),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: queryKeys.runtime }),
  });
}

export function useEnableMirroredNetworking() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: () => api.runtimeEnableMirrored(),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: queryKeys.runtime }),
  });
}

/**
 * Switches the container engine. Deliberately does NOT invalidate the runtime
 * query: the setting is only half-applied until the app restarts (the backend
 * decides its endpoint and auto-start behaviour at boot), so the caller drives
 * the restart rather than the UI briefly rendering a mixed state.
 */
export function useSetRuntimeMode() {
  return useMutation({
    mutationFn: (mode: RuntimeMode) => api.runtimeSetMode(mode),
  });
}

export function useServerSecrets(name: string) {
  return useQuery({
    queryKey: queryKeys.serverSecrets(name),
    queryFn: () => api.getServerSecrets(name),
  });
}

export function useSetServerSecrets(name: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (values: Record<string, string>) => api.setServerSecrets(name, values),
    onSuccess: () => {
      // The container is recreated on set, so its status can change too.
      queryClient.invalidateQueries({ queryKey: queryKeys.serverSecrets(name) });
      queryClient.invalidateQueries({ queryKey: queryKeys.server(name) });
    },
  });
}

export function useDeleteServerSecret(name: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (key: string) => api.deleteServerSecret(name, key),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: queryKeys.serverSecrets(name) });
      queryClient.invalidateQueries({ queryKey: queryKeys.server(name) });
    },
  });
}

export function useBackups(name: string) {
  return useQuery({ queryKey: queryKeys.backups(name), queryFn: () => api.listBackups(name) });
}

export function useBackupSettings() {
  return useQuery({ queryKey: queryKeys.backupSettings, queryFn: api.backupSettings });
}

export function useUpdateBackupSettings() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (settings: BackupSettings) => api.updateBackupSettings(settings),
    onSuccess: (updated) => queryClient.setQueryData(queryKeys.backupSettings, updated),
  });
}

export function useCreateBackup(name: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: () => api.createBackup(name),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: queryKeys.backups(name) }),
  });
}

export function useRestoreBackup(name: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => api.restoreBackup(name, id),
    // Restore replaces the volume; the server's status is unaffected (it must be
    // stopped), but refetch its detail in case the UI shows volume-derived info.
    onSuccess: () => queryClient.invalidateQueries({ queryKey: queryKeys.server(name) }),
  });
}

export function useDeleteBackup(name: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => api.deleteBackup(name, id),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: queryKeys.backups(name) }),
  });
}

export function useRegenerateServerSecret(name: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (key: string) => api.regenerateServerSecret(name, key),
    onSuccess: () => {
      // The container is recreated on regenerate, so its status can change too.
      queryClient.invalidateQueries({ queryKey: queryKeys.serverSecrets(name) });
      queryClient.invalidateQueries({ queryKey: queryKeys.server(name) });
    },
  });
}
