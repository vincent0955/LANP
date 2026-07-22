import {
  HubConnection,
  HubConnectionBuilder,
  HubConnectionState,
  LogLevel,
} from "@microsoft/signalr";
import type { QueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { queryKeys } from "@/api/queries";
import { useSettings } from "@/lib/settings";
import { useConnectionStore } from "./connectionStore";
import {
  activeLogSubscriptions,
  appendLogLine,
  resetBuffersForResubscribe,
  setLogHubInvoker,
} from "./logStore";
import { useProgressStore } from "./progressStore";
import type {
  AutoScaleActionMessage,
  BackupCompletedMessage,
  DownloadProgressMessage,
  LogLineMessage,
  MetricsSnapshot,
  ServerStatusChangedMessage,
  ServerSummary,
} from "@/api/types";

// REST responses are the snapshot, hub events are deltas applied to the query
// cache. On reconnect, REST truth overwrites anything missed while disconnected.

let connection: HubConnection | null = null;
let started = false;

export function initRealtime(queryClient: QueryClient) {
  if (started) return;
  started = true;

  setLogHubInvoker(async (method, serverName) => {
    if (connection?.state === HubConnectionState.Connected) {
      await connection.invoke(method, serverName);
    }
    // Not connected: onConnected() below (re)subscribes everything active.
  });

  // Rebuild the connection whenever the backend address or token changes.
  useSettings.subscribe((state, prev) => {
    if (state.baseUrl !== prev.baseUrl || state.apiToken !== prev.apiToken) {
      void restart(queryClient);
    }
  });

  void connect(queryClient);
}

async function connect(queryClient: QueryClient) {
  const { baseUrl, apiToken } = useSettings.getState();
  const setHubState = useConnectionStore.getState().setHubState;

  const builder = new HubConnectionBuilder()
    .withUrl(`${baseUrl}/hubs/dashboard`, {
      // Applies to the negotiate POST and long-polling. Browsers cannot attach
      // custom headers to the WebSocket upgrade itself, so in token-auth mode
      // (backend exposed beyond loopback) SignalR falls back through transports
      // until long-polling succeeds. Localhost (no token) uses WebSockets.
      headers: apiToken ? { "X-Api-Token": apiToken } : undefined,
    })
    .withAutomaticReconnect({
      // Never give up: back off to 15s and keep trying while the app is open.
      nextRetryDelayInMilliseconds: (ctx) =>
        Math.min(15_000, 1000 * 2 ** Math.min(ctx.previousRetryCount, 4)),
    })
    .configureLogging(LogLevel.Warning);

  connection = builder.build();
  registerHandlers(connection, queryClient);

  connection.onreconnecting(() => setHubState("reconnecting"));
  connection.onreconnected(() => void onConnected(queryClient));
  connection.onclose(() => {
    setHubState("disconnected");
    // withAutomaticReconnect only runs after a *successful* start; if the very
    // first start fails, or reconnection is aborted, retry from scratch.
    scheduleRestart(queryClient);
  });

  setHubState("connecting");
  try {
    await connection.start();
    await onConnected(queryClient);
  } catch {
    setHubState("disconnected");
    scheduleRestart(queryClient);
  }
}

let restartTimer: ReturnType<typeof setTimeout> | null = null;

function scheduleRestart(queryClient: QueryClient) {
  restartTimer ??= setTimeout(() => {
    restartTimer = null;
    void restart(queryClient);
  }, 5000);
}

async function restart(queryClient: QueryClient) {
  const old = connection;
  connection = null;
  try {
    await old?.stop();
  } catch {
    // Ignore: the old connection may already be dead.
  }
  await connect(queryClient);
}

async function onConnected(queryClient: QueryClient) {
  const conn = connection;
  if (!conn || conn.state !== HubConnectionState.Connected) return;

  useConnectionStore.getState().setHubState("connected");

  try {
    // Group membership does not survive a new connection id.
    await conn.invoke("SubscribeEvents");
    await conn.invoke("SubscribeMetrics");
    resetBuffersForResubscribe();
    for (const serverName of activeLogSubscriptions()) {
      await conn.invoke("SubscribeLogs", serverName);
    }
  } catch {
    // Connection dropped mid-handshake; onclose/onreconnecting take over.
    return;
  }

  // Events missed while disconnected are unrecoverable — refetch REST truth.
  void queryClient.invalidateQueries({ queryKey: queryKeys.servers });
  void queryClient.invalidateQueries({ queryKey: queryKeys.setup });
  void queryClient.invalidateQueries({ queryKey: queryKeys.metrics });
}

function registerHandlers(conn: HubConnection, queryClient: QueryClient) {
  // Every hub event carries a single message object (see HubEvents.cs).

  conn.on("LogLine", (message: LogLineMessage) => appendLogLine(message));

  conn.on("ServerStatusChanged", (message: ServerStatusChangedMessage) => {
    const list = queryClient.getQueryData<ServerSummary[]>(queryKeys.servers);
    const known = list?.some((s) => s.name === message.serverName);

    if (list && known) {
      queryClient.setQueryData<ServerSummary[]>(
        queryKeys.servers,
        list.map((s) => (s.name === message.serverName ? { ...s, status: message.status } : s)),
      );
    } else {
      // A server we don't have yet (deployed elsewhere, or list never loaded).
      void queryClient.invalidateQueries({ queryKey: queryKeys.servers });
    }

    // Detail view carries replicas/ports/players that may change with status.
    void queryClient.invalidateQueries({ queryKey: queryKeys.server(message.serverName) });
  });

  conn.on("MetricsUpdate", (snapshot: MetricsSnapshot) => {
    queryClient.setQueryData(queryKeys.metrics, snapshot);
  });

  conn.on("DownloadProgress", (message: DownloadProgressMessage) => {
    useProgressStore.getState().record(message);
  });

  conn.on("AutoScaleAction", (message: AutoScaleActionMessage) => {
    toast.info(`Auto-scale: ${message.action} ${message.serverName}`, {
      description: message.reason,
    });
  });

  conn.on("BackupCompleted", (message: BackupCompletedMessage) => {
    // A scheduled backup landed — refresh that server's list if it's on screen.
    void queryClient.invalidateQueries({ queryKey: queryKeys.backups(message.serverName) });
  });
}
