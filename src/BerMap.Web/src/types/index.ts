/* ===== Authentication ===== */

export interface User {
  id: string;
  username: string;
  email: string;
  displayName: string;
  role: string;
  avatarUrl?: string;
  lastLogin?: string;
}

/** POST /api/auth/change-password: a fresh token for the current session. */
export interface ChangePasswordResponse {
  token: string;
  expiresAt: string;
}

export interface AuthState {
  user: User | null;
  token: string | null;
  isAuthenticated: boolean;
}

/* ===== Graph / inventory ===== */

export type NodeStatus = 'healthy' | 'warning' | 'critical' | 'unknown';

export interface InfrastructureNode {
  id: string;
  nodeType: string;
  name: string;
  fqdn: string;
  ipAddress?: string | null;
  macAddress?: string | null;
  status: NodeStatus | string;
  discoverySource: string;
  biosUuid: string;
  vmInstanceUuid: string;
  domainSid: string;
  isStale: boolean;
  staleSince?: string | null;
  firstSeen: string;
  lastSeen: string;
  sourceSystems: string[];
  properties: Record<string, string>;
}

export interface InfrastructureRelationship {
  id: string;
  sourceNodeId: string;
  sourceNodeName: string;
  targetNodeId: string;
  targetNodeName: string;
  relationshipType: string;
  confidenceLevel: string;
  evidence: string;
  sourceSystem: string;
  isStale: boolean;
}

export interface GraphQueryResult {
  nodes: InfrastructureNode[];
  relationships: InfrastructureRelationship[];
  /** True when the server capped the graph (500 nodes); newer backends only. */
  isTruncated?: boolean;
}

export interface InventoryNode {
  id: string;
  nodeType: string;
  name: string;
  fqdn: string;
  ipAddress?: string | null;
  macAddress?: string | null;
  status: string;
  discoverySource: string;
  biosUuid: string;
  vmInstanceUuid: string;
  domainSid: string;
  isStale: boolean;
  staleSince?: string | null;
  firstSeen: string;
  lastSeen: string;
  sourceCount: number;
  sourceSystems: string[];
  properties: Record<string, unknown>;
  needsReview: boolean;
}

export interface InventoryPage {
  items: InventoryNode[];
  totalCount: number;
  page: number;
  pageSize: number;
  totalPages: number;
  reconciledCount: number;
  singleSourceCount: number;
  pendingReviewCount: number;
}

export interface NodeSourceLink {
  id: string;
  integrationId: string;
  integrationName: string;
  externalId: string;
  sourceSystem: string;
  nodeType: string;
  isStale: boolean;
  firstSeenAt: string;
  lastSeenAt: string;
}

export interface ReconciliationReview {
  id: string;
  status: string;
  integrationId?: string | null;
  integrationName: string;
  discoveryJobId?: string | null;
  createdNodeId?: string | null;
  createdNodeName: string;
  candidateNodeId?: string | null;
  candidateNodeName: string;
  sourceSystem: string;
  matchKey: string;
  confidence: string;
  matchScore: number;
  reason: string;
  candidateSummary: string;
  discoveredNode: unknown;
  candidateNodeIds: string[];
  createdAt: string;
  reviewedAt?: string | null;
  reviewedBy: string;
  resolutionNotes: string;
}

export interface InventoryNodeDetail {
  node: InventoryNode;
  sourceLinks: NodeSourceLink[];
  relationships: InfrastructureRelationship[];
  reviews: ReconciliationReview[];
}

export interface ResolveReconciliationReviewRequest {
  resolution: 'Merged' | 'Dismissed';
  targetNodeId?: string;
  notes?: string;
}

/* ===== Integrations ===== */

export interface AdapterCapabilities {
  supportsFullDiscovery: boolean;
  supportsIncremental: boolean;
  supportsCredentialsValidation: boolean;
  supportsHealthCheck: boolean;
  supportsMetrics: boolean;
  maxConcurrency: number;
  estimatedDiscoveryDuration: string;
  supportedEntityTypes: string[];
  supportedRelationshipTypes: string[];
}

export interface AdapterConfigurationSchema {
  jsonSchema: string;
  secretFields: string[];
  notes: string[];
}

export interface AdapterDescriptor {
  adapterType: string;
  displayName: string;
  version: string;
  capabilities: AdapterCapabilities;
  configurationSchema: AdapterConfigurationSchema;
}

export interface Integration {
  id: string;
  name: string;
  description: string;
  adapterType: string;
  adapterVersion: string;
  status: string;
  isEnabled: boolean;
  scheduleCron: string;
  /** UTC; in the past means due and about to be queued; null when disabled. */
  nextScheduledRunAt?: string | null;
  scheduleTimeZone?: string;
  timeoutSeconds: number;
  configurationVersion: number;
  configuration: Record<string, unknown>;
  endpoint: string;
  lastSyncAt?: string | null;
  lastTestAt?: string | null;
  lastTestSuccess?: boolean | null;
  lastError: string;
  lastErrorAt?: string | null;
  discoveredEntitiesCount: number;
  /** Data arrives from a collector; the server never tests or discovers it itself. */
  collectorManaged?: boolean;
  lastJobId?: string | null;
  createdAt: string;
  updatedAt?: string | null;
  adapter?: AdapterDescriptor | null;
}

export interface CreateIntegrationRequest {
  name: string;
  description: string;
  adapterType: string;
  configuration: Record<string, unknown>;
  secret?: string;
  scheduleCron: string;
  timeoutSeconds: number;
  isEnabled: boolean;
  collectorManaged?: boolean;
}

export interface UpdateIntegrationRequest {
  name?: string;
  description?: string;
  configuration?: Record<string, unknown>;
  secret?: string;
  clearSecret?: boolean;
  scheduleCron?: string;
  timeoutSeconds?: number;
  isEnabled?: boolean;
  collectorManaged?: boolean;
}

export interface ConnectionTestResult {
  success: boolean;
  message: string;
  latency: string;
  serverVersion?: string | null;
  errorCode?: string | null;
  errorDetail?: string | null;
  warnings: string[];
  details: Record<string, string>;
}

export interface DiscoveryJob {
  id: string;
  integrationId: string;
  integrationName: string;
  adapterType: string;
  jobType: string;
  status: string;
  startedAt: string;
  completedAt?: string | null;
  entitiesDiscovered: number;
  relationshipsDiscovered: number;
  progressPercent: number;
  currentPhase: string;
  currentMessage: string;
  requestedBy: string;
  durationMs?: number | null;
  errors: string[];
  warnings: string[];
  statistics: Record<string, number>;
}

/* ===== Collectors ===== */

export interface Collector {
  id: string;
  name: string;
  hostname: string;
  ipAddress: string;
  status: 'Online' | 'Offline' | 'Error' | string;
  version: string;
  lastHeartbeatAt: string;
  supportedAdapters: string[];
  /** Null values (or null metrics) mean the collector never reported them. */
  metrics: {
    cpuUsagePercent: number | null;
    memoryUsagePercent: number | null;
    queuedMessages: number | null;
  } | null;
}

/* ===== Dashboard ===== */

export interface RecentChange {
  id: string;
  entityName: string;
  entityType: string;
  /** "Discovered" (node first seen) or "StatusChanged". */
  changeType: string;
  source: string;
  status: string;
  timestamp: string;
}

export interface DashboardSummary {
  totalEntities: number;
  healthyEntities: number;
  warningEntities: number;
  criticalEntities: number;
  unknownEntities: number;
  staleEntities: number;
  activeIntegrations: number;
  totalIntegrations: number;
  pendingReconciliationReviews: number;
  criticalDependenciesCount: number;
  recentDiscoveries: number;
  lastDiscoveryAt?: string | null;
  recentChanges: RecentChange[];
}

export interface HealthDistribution {
  healthy: number;
  warning: number;
  critical: number;
  unknown: number;
}

/* ===== Snapshots ===== */

export interface CreateSnapshotRequest {
  name: string;
  description: string;
  createdBy?: string;
  sourceJobId?: string;
}

export interface SnapshotSummary {
  id: string;
  name: string;
  description: string;
  createdBy: string;
  nodeCount: number;
  relationshipCount: number;
  isImmutable: boolean;
  hash: string;
  snapshotVersion: number;
  sourceJobId?: string | null;
  graphCapturedAt: string;
  createdAt: string;
  warnings: string[];
}

export interface SnapshotNode {
  id: string;
  nodeId: string;
  nodeType: string;
  name: string;
  fqdn: string;
  ipAddress?: string | null;
  macAddress?: string | null;
  status: string;
  discoverySource: string;
  biosUuid: string;
  vmInstanceUuid: string;
  domainSid: string;
  isStale: boolean;
  firstSeen: string;
  lastSeen: string;
  properties: Record<string, unknown>;
}

export interface SnapshotRelationship {
  id: string;
  relationshipId: string;
  sourceNodeId: string;
  sourceNodeName: string;
  targetNodeId: string;
  targetNodeName: string;
  relationshipType: string;
  confidenceLevel: string;
  evidence: string;
  sourceSystem: string;
  isStale: boolean;
}

export interface SnapshotGraph {
  snapshot: SnapshotSummary;
  nodes: SnapshotNode[];
  relationships: SnapshotRelationship[];
}

export interface FieldChange {
  field: string;
  from?: string | null;
  to?: string | null;
}

export interface SnapshotNodeChange {
  nodeId: string;
  name: string;
  fqdn: string;
  nodeType: string;
  ipAddress?: string | null;
  status: string;
}

export interface SnapshotNodeFieldChange {
  nodeId: string;
  name: string;
  nodeType: string;
  changes: FieldChange[];
}

export interface SnapshotRelationshipChange {
  relationshipId: string;
  sourceNodeName: string;
  targetNodeName: string;
  relationshipType: string;
  changes: FieldChange[];
}

export interface SnapshotDiff {
  snapshotAId: string;
  snapshotAName: string;
  snapshotBId: string;
  snapshotBName: string;
  addedNodesCount: number;
  removedNodesCount: number;
  changedNodesCount: number;
  addedRelationshipsCount: number;
  removedRelationshipsCount: number;
  changedRelationshipsCount: number;
  addedNodes: SnapshotNodeChange[];
  removedNodes: SnapshotNodeChange[];
  changedNodes: SnapshotNodeFieldChange[];
  addedRelationships: SnapshotRelationshipChange[];
  removedRelationships: SnapshotRelationshipChange[];
  changedRelationships: SnapshotRelationshipChange[];
  highlights: string[];
}

export interface SnapshotVerification {
  snapshotId: string;
  snapshotName: string;
  isValid: boolean;
  storedHash: string;
  computedHash: string;
  storedNodeCount: number;
  actualNodeCount: number;
  storedRelationshipCount: number;
  actualRelationshipCount: number;
  differences: string[];
  verifiedAt: string;
}

/* ===== API ===== */

export interface ApiError {
  status: number;
  message: string;
  code?: string;
  details?: Record<string, string[]>;
}

export interface PaginatedResponse<T> {
  items: T[];
  totalCount: number;
  page: number;
  pageSize: number;
  totalPages: number;
}

/* ===== Simulations (BerSim engine) ===== */

export interface SimulationRunRequest {
  targetNodeId: string;
  /** host_failure | datastore_failure | network_partition | workload_outage */
  scenarioType: string;
}

export interface ImpactedEntity {
  id: string;
  name: string;
  type: string;
  impactLevel: string;
  dependencyPath: string;
}

export interface ClusterHeadroom {
  remainingCpuPercent: number;
  remainingRamPercent: number;
  hasCapacityForFailover: boolean;
  bottleneckResource: string;
  /** Machine-readable bottleneck: Cpu | Ram | None | Unknown | NotApplicable. */
  bottleneckCode?: 'Cpu' | 'Ram' | 'None' | 'Unknown' | 'NotApplicable' | string;
  clusterName?: string;
  /** True when the failed host's capacity was subtracted (N-1 headroom). */
  isPostFailure?: boolean;
}

export interface RecommendationItem {
  code: string;
  text: string;
  values: Record<string, string>;
}

export interface ImpactAnalysisResult {
  targetNodeId: string;
  targetNodeName: string;
  scenarioType: string;
  riskScore: number;
  confirmedImpacts: ImpactedEntity[];
  probableImpacts: ImpactedEntity[];
  possibleImpacts: ImpactedEntity[];
  clusterHeadroom: ClusterHeadroom;
  recommendations: string[];
  /** Coded recommendations, localized in the UI; `text` is the English fallback. */
  recommendationItems?: RecommendationItem[];
  /** True when the simulation graph hit the response cap; the result may be incomplete. */
  graphTruncated?: boolean;
}

/* ===== Audit ===== */

export interface AuditEntry {
  id: string;
  userId: string;
  username: string;
  action: string;
  entityType: string;
  entityId: string;
  detailsJson: string;
  ipAddress: string;
  success: boolean;
  createdAt: string;
}

export interface AuditPage {
  items: AuditEntry[];
  totalCount: number;
  page: number;
  pageSize: number;
}

/* ===== User management ===== */

export interface UserAccount {
  id: string;
  username: string;
  displayName: string;
  email: string;
  role: string;
  isActive: boolean;
  lastLogin?: string | null;
  createdAt: string;
}

export interface CreateUserRequest {
  username: string;
  displayName?: string;
  email?: string;
  role: string;
  password: string;
}

export interface UpdateUserRequest {
  displayName?: string;
  email?: string;
  role?: string;
  isActive?: boolean;
}
