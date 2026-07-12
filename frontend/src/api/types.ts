// Hand-written mirrors of the backend's C# records (GameDashboard.Api/Models).
// The backend has no OpenAPI document; these shapes are locked by its contract
// tests (ApiContractTests.cs), camelCase on the wire, enums as strings.

export type ServerStatus = "Running" | "Stopped" | "Pending" | "Error" | "Unknown";

/** A container port exposed via a NodePort Service (players connect to nodePort). */
export interface PortMapping {
  name: string;
  protocol: string;
  containerPort: number;
  nodePort: number;
}

export interface ResourceSpec {
  cpuRequest: string;
  cpuLimit: string;
  memoryRequest: string;
  memoryLimit: string;
}

export interface ServerSummary {
  name: string;
  game: string;
  image: string;
  status: ServerStatus;
  replicas: number;
  createdAt: string;
}

export interface PlayerInfo {
  currentPlayers: number;
  maxPlayers: number;
  currentMap: string | null;
}

export interface ServerDetail extends ServerSummary {
  ports: PortMapping[];
  resources: ResourceSpec | null;
  players: PlayerInfo | null;
}

export interface DeployServerRequest {
  name: string;
  imageTag: string;
  resources?: ResourceSpec | null;
  configOverrides?: Record<string, string> | null;
}

/** A port a game template needs, before a NodePort has been assigned. */
export interface TemplatePort {
  name: string;
  protocol: string;
  containerPort: number;
}

export interface GameTemplate {
  displayName: string;
  imageTag: string;
  steamAppId: number | null;
  dataMountPath: string;
  defaultStorageBytes: number;
  defaultPorts: TemplatePort[];
  defaultResources: ResourceSpec;
  defaultConfig: Record<string, string>;
  /** Config keys sourced from the game-secrets Secret; not editable via config. */
  secretKeyRefs: Record<string, string>;
}

export interface ClusterHealth {
  clusterReachable: boolean;
  namespaceReady: boolean;
  namespace: string;
  error: string | null;
}

export interface SetupStatus {
  kubeconfigPresent: boolean;
  clusterReachable: boolean;
  namespaceReady: boolean;
  metricsServerPresent: boolean;
  secretsConfigured: boolean;
  /** Key names present in the game-secrets Secret; values are never exposed. */
  configuredSecretKeys: string[];
  warnings: string[];
}

export interface NodeMetrics {
  cpuUsedMillicores: number;
  cpuCapacityMillicores: number;
  memUsedBytes: number;
  memCapacityBytes: number;
}

export interface PodMetricsInfo {
  serverName: string;
  cpuUsedMillicores: number;
  memUsedBytes: number;
}

export interface MetricsSnapshot {
  available: boolean;
  node: NodeMetrics | null;
  pods: PodMetricsInfo[];
  unavailableReason: string | null;
}

export interface RconCommandResponse {
  response: string;
}

/**
 * The host machine's shareable addresses — used to render copyable "ip:port" join
 * addresses. lanAddresses are the LAN IPv4s in preference order (empty when no LAN
 * adapter is up). publicAddress is the internet-facing IPv4, only reachable by
 * players after the node port is forwarded on the router; null if lookup failed.
 * nodePortRangeStart/End is the backend's whole allocation window — forwarding
 * that range once on the router covers every current and future server.
 */
export interface NetworkInfo {
  lanAddresses: string[];
  publicAddress: string | null;
  nodePortRangeStart: number;
  nodePortRangeEnd: number;
}

// SignalR hub payloads (GameDashboard.Api/RealTime/HubEvents.cs). Every hub
// event carries a single message object argument, not positional arguments.

export interface LogLineMessage {
  serverName: string;
  line: string;
  timestamp: string;
}

export interface ServerStatusChangedMessage {
  serverName: string;
  status: ServerStatus;
  timestamp: string;
}

export interface AutoScaleActionMessage {
  serverName: string;
  action: string;
  reason: string;
  timestamp: string;
}

/**
 * Bytes accumulating on a server's data volume during first deploy (measured by
 * `du` inside the pod). capacityBytes is the PVC size, not the expected install
 * size — show bytesUsed as an absolute, never as a percentage.
 */
export interface DownloadProgressMessage {
  serverName: string;
  bytesUsed: number;
  capacityBytes: number;
  timestamp: string;
}
