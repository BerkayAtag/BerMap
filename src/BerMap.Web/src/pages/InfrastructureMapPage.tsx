import React, { useMemo, useState, useCallback, useEffect } from 'react';
import { useQuery } from '@tanstack/react-query';
import { Link } from 'react-router-dom';
import {
  ReactFlow,
  Controls,
  Background,
  MiniMap,
  useNodesState,
  useEdgesState,
  type Node,
  type Edge,
} from '@xyflow/react';
import '@xyflow/react/dist/style.css';
import { Card } from '@/components/ui/Card';
import { StatusIndicator } from '@/components/ui/StatusIndicator';
import { Button } from '@/components/ui/Button';
import { ButtonLink } from '@/components/ui/ButtonLink';
import { useThemeTokens, type ThemeTokens } from '@/hooks/useThemeTokens';
import { useEscapeKey } from '@/hooks/useEscapeKey';
import { useLocale } from '@/lib/i18n';
import { api } from '@/lib/api';
import { formatDateTime, formatNumber, getErrorMessage, toNodeStatus } from '@/lib/format';
import type { GraphQueryResult, InfrastructureNode } from '@/types';
import {
  Server,
  Database,
  Cpu,
  Search,
  Network,
  RefreshCw,
  PlugZap,
  AlertTriangle,
  ExternalLink,
  Play,
  X,
} from 'lucide-react';

const TYPE_GROUP_ORDER: Record<string, number> = {
  Datacenter: 0,
  Site: 1,
  Forest: 1,
  Domain: 1,
  OrganizationalUnit: 2,
  Cluster: 2,
  ResourcePool: 3,
  Hypervisor: 3,
  Computer: 5,
  DomainController: 5,
  VirtualMachine: 6,
  PhysicalServer: 6,
  Endpoint: 7,
  Storage: 10,
  Datastore: 10,
  Network: 10,
  NetworkDevice: 10,
  Application: 8,
  Service: 8,
  Database: 8,
  Group: 9,
};

function nodeIcon(nodeType: string) {
  switch (nodeType) {
    case 'Database':
    case 'Datastore':
      return <Database className="w-4 h-4 text-blue-500" />;
    case 'VirtualMachine':
      return <Cpu className="w-4 h-4 text-purple-500" />;
    default:
      return <Server className="w-4 h-4 text-emerald-500" />;
  }
}

function buildFlowGraph(
  data: GraphQueryResult,
  tokens: ThemeTokens,
  isVisible: (node: InfrastructureNode) => boolean,
  selectedNodeId: string | null,
): { nodes: Node[]; edges: Edge[] } {
  // Layered layout: each TYPE_GROUP_ORDER value is a horizontal band (sites
  // and domains on top, workloads in the middle, storage/network below).
  // Wide bands wrap into centred rows so the graph keeps a readable aspect
  // ratio instead of one very wide strip.
  const layers = new Map<number, InfrastructureNode[]>();
  for (const node of data.nodes) {
    const layer = TYPE_GROUP_ORDER[node.nodeType] ?? 50;
    const list = layers.get(layer) ?? [];
    list.push(node);
    layers.set(layer, list);
  }

  const positions = new Map<string, { x: number; y: number }>();
  const COLUMN_GAP = 230;
  const ROW_GAP = 80;
  const LAYER_GAP = 70;
  const MAX_PER_ROW = 8;

  let y = 0;
  for (const [, nodes] of Array.from(layers.entries()).sort((a, b) => a[0] - b[0])) {
    const ordered = [...nodes].sort(
      (a, b) => a.nodeType.localeCompare(b.nodeType) || a.name.localeCompare(b.name),
    );
    for (let start = 0; start < ordered.length; start += MAX_PER_ROW) {
      const row = ordered.slice(start, start + MAX_PER_ROW);
      const offset = ((MAX_PER_ROW - row.length) * COLUMN_GAP) / 2;
      row.forEach((node, index) => {
        positions.set(node.id, { x: offset + index * COLUMN_GAP, y });
      });
      y += ROW_GAP;
    }
    y += LAYER_GAP;
  }

  const flowNodes: Node[] = data.nodes.map((node) => {
    const position = positions.get(node.id) ?? { x: 0, y: 0 };
    const status = toNodeStatus(node.status);
    const borderColor =
      node.nodeType === 'Datacenter' || node.nodeType === 'Application'
        ? tokens.brand
        : status === 'warning'
          ? tokens.warning
          : status === 'critical'
            ? tokens.critical
            : tokens.border;
    return {
      id: node.id,
      type: 'default',
      position,
      data: { label: node.name, status: node.status },
      style: {
        background: tokens.surface,
        color: tokens.text,
        borderRadius: '8px',
        // Stale nodes (no longer seen by any source) are dashed and faded.
        border: `1px ${node.isStale ? 'dashed' : 'solid'} ${node.isStale ? tokens.textMuted : borderColor}`,
        padding: '8px 10px',
        fontWeight: node.nodeType === 'Datacenter' ? 'bold' : 'normal',
        fontStyle: node.isStale ? 'italic' : 'normal',
        width: 200,
        fontSize: 11,
        opacity: isVisible(node) ? (node.isStale ? 0.55 : 1) : 0.12,
      },
    } satisfies Node;
  });

  const knownIds = new Set(data.nodes.map((node) => node.id));
  const flowEdges: Edge[] = data.relationships
    .filter((rel) => knownIds.has(rel.sourceNodeId) && knownIds.has(rel.targetNodeId))
    .map((rel) => {
      // Labelling every edge turns a real topology into an unreadable mesh;
      // relationship names are shown only for the selected node's edges.
      const isSelected = selectedNodeId !== null &&
        (rel.sourceNodeId === selectedNodeId || rel.targetNodeId === selectedNodeId);
      const dimmed = selectedNodeId !== null && !isSelected;
      return {
        id: rel.id,
        source: rel.sourceNodeId,
        target: rel.targetNodeId,
        label: isSelected ? rel.relationshipType : undefined,
        animated: isSelected && rel.confidenceLevel?.toLowerCase() === 'verified',
        zIndex: isSelected ? 1 : 0,
        style: {
          strokeWidth: isSelected ? 2 : 1,
          stroke: isSelected ? tokens.brand : tokens.textMuted,
          strokeDasharray: rel.isStale ? '4 4' : undefined,
          opacity: dimmed ? 0.15 : rel.isStale ? 0.3 : 0.55,
        },
        labelStyle: { fontSize: 10, fill: tokens.text },
        labelBgStyle: { fill: tokens.surface },
      };
    });

  return { nodes: flowNodes, edges: flowEdges };
}

export const InfrastructureMapPage: React.FC = () => {
  const tokens = useThemeTokens();
  const isDark = tokens.isDark;
  const locale = useLocale();
  const isTr = locale === 'tr';

  const topologyQuery = useQuery<GraphQueryResult>({
    queryKey: ['graph', 'topology'],
    queryFn: () => api.get<GraphQueryResult>('/graph/topology?depth=3'),
    refetchInterval: 60_000,
  });

  const [nodes, setNodes, onNodesChange] = useNodesState<Node>([]);
  const [edges, setEdges, onEdgesChange] = useEdgesState<Edge>([]);
  const [selectedNodeId, setSelectedNodeId] = useState<string | null>(null);
  const [queryInput, setQueryInput] = useState('');

  const searchText = queryInput.trim().toLowerCase();

  const matchNode = useCallback(
    (node: InfrastructureNode): boolean => {
      if (!searchText) return true;
      return (
        node.name.toLowerCase().includes(searchText) ||
        node.fqdn.toLowerCase().includes(searchText) ||
        (node.ipAddress ?? '').includes(searchText) ||
        node.nodeType.toLowerCase().includes(searchText)
      );
    },
    [searchText],
  );

  // Project real API data into the flow graph whenever data, theme or filter change.
  // Positions already on the canvas (including user-dragged ones) are kept by node id.
  useEffect(() => {
    const data = topologyQuery.data;
    if (!data) return;
    const flow = buildFlowGraph(data, tokens, matchNode, selectedNodeId);
    // Existing nodes are updated in place (style/data only) so React Flow keeps
    // their measured size, selection and dragged position; replacing them would
    // hide and re-measure the whole canvas on every keystroke or click.
    setNodes((current) => {
      const previous = new Map(current.map((node) => [node.id, node]));
      return flow.nodes.map((node) => {
        const existing = previous.get(node.id);
        return existing ? { ...existing, style: node.style, data: node.data } : node;
      });
    });
    setEdges(flow.edges);
  }, [topologyQuery.data, tokens, matchNode, selectedNodeId, setNodes, setEdges]);

  const nodeById = useMemo(() => {
    const map = new Map<string, InfrastructureNode>();
    topologyQuery.data?.nodes.forEach((node) => map.set(node.id, node));
    return map;
  }, [topologyQuery.data]);

  const selectedNode = selectedNodeId ? nodeById.get(selectedNodeId) ?? null : null;
  const relevantRelations = useMemo(() => {
    if (!selectedNodeId) return [];
    return (
      topologyQuery.data?.relationships.filter(
        (rel) => rel.sourceNodeId === selectedNodeId || rel.targetNodeId === selectedNodeId,
      ) ?? []
    );
  }, [topologyQuery.data, selectedNodeId]);

  const error = topologyQuery.isError ? getErrorMessage(topologyQuery.error) : null;
  const totalNodes = topologyQuery.data?.nodes.length ?? 0;
  const matchedCount = useMemo(
    () => (searchText ? (topologyQuery.data?.nodes.filter(matchNode).length ?? 0) : totalNodes),
    [searchText, topologyQuery.data, matchNode, totalNodes],
  );

  const onNodeClick = useCallback((_: React.MouseEvent, node: Node) => {
    setSelectedNodeId(node.id);
  }, []);
  const closeDetail = useCallback(() => setSelectedNodeId(null), []);

  useEscapeKey(selectedNodeId !== null, closeDetail);

  return (
    <div className="h-full min-h-[480px] flex flex-col gap-4">
      {/* Data source note: map reflects the real reconciled graph */}
      <div className="flex flex-wrap items-center justify-between gap-2 bg-white dark:bg-dark-card p-2.5 rounded-lg border border-neutral-200 dark:border-dark-border text-[11px] text-neutral-500 dark:text-neutral-400">
        <span>
          {isTr
            ? 'Bu harita envanter grafiğinin canlı görünümüdür: keşif ve uzlaştırma tamamlandığında düğümler ve bağlantılar otomatik güncellenir.'
            : 'This map is a live view of the inventory graph: nodes and links update automatically when discovery and reconciliation complete.'}
        </span>
        <Link to="/integrations" className="text-brand-primary dark:text-brand-primary-dark hover:underline shrink-0">
          {isTr ? 'Keşfi yönet →' : 'Manage discovery →'}
        </Link>
      </div>

      {topologyQuery.data?.isTruncated && (
        <div
          role="status"
          className="flex items-start gap-2 p-2.5 rounded-lg border border-amber-200 dark:border-amber-900 bg-amber-50 dark:bg-amber-950/30 text-[11px] text-amber-800 dark:text-amber-300"
        >
          <AlertTriangle className="w-3.5 h-3.5 mt-0.5 shrink-0" aria-hidden="true" />
          <span>
            {isTr
              ? `Graf çok büyük; harita yalnızca ilk ${formatNumber(topologyQuery.data.nodes.length)} düğümü gösteriyor. Tam liste için Envanter sayfasını kullanın.`
              : `The graph is too large; the map shows only the first ${formatNumber(topologyQuery.data.nodes.length)} nodes. Use the Inventory page for the full list.`}
          </span>
        </div>
      )}

      {/* Map Control Bar */}
      <div className="flex flex-wrap items-center justify-between gap-3 bg-white dark:bg-dark-card p-3 rounded-lg border border-neutral-200 dark:border-dark-border">
        <div className="flex items-center gap-3 min-w-0">
          <div className="flex items-center gap-2 px-3 py-1.5 rounded-md bg-neutral-100 dark:bg-dark-border text-sm min-w-0">
            <Search className="w-4 h-4 text-neutral-400 shrink-0" />
            <input
              type="search"
              value={queryInput}
              onChange={(event) => setQueryInput(event.target.value)}
              placeholder={isTr ? 'Haritada filtrele...' : 'Filter on map...'}
              aria-label={isTr ? 'Haritada filtrele' : 'Filter on map'}
              className="bg-transparent text-sm focus:outline-none w-full sm:w-48 min-w-0 text-neutral-800 dark:text-neutral-200"
            />
          </div>
        </div>

        <div className="flex flex-wrap items-center gap-2">
          <Button
            variant="outline"
            size="sm"
            className="gap-1.5"
            onClick={() => void topologyQuery.refetch()}
            disabled={topologyQuery.isFetching}
          >
            <RefreshCw className={`w-3.5 h-3.5 ${topologyQuery.isFetching ? 'animate-spin' : ''}`} />
            {isTr ? 'Yenile' : 'Refresh'}
          </Button>
          <span className="text-xs text-neutral-500 dark:text-neutral-400 mr-2" aria-live="polite">
            {isTr ? 'Gösterilen:' : 'Showing:'}{' '}
            {searchText ? (
              <>
                <strong>{formatNumber(matchedCount)}</strong> / {formatNumber(totalNodes)}
              </>
            ) : (
              <strong>{formatNumber(totalNodes)}</strong>
            )}{' '}
            {isTr ? 'düğüm,' : 'nodes,'} <strong>{formatNumber(edges.length)}</strong> {isTr ? 'bağlantı' : 'links'}
          </span>
        </div>
      </div>

      {/* Main Graph Canvas Area + Detail Side Panel */}
      <div className="flex-1 flex gap-4 min-h-0 relative">
        <div className="flex-1 rounded-xl border border-neutral-200 dark:border-dark-border overflow-hidden bg-neutral-50 dark:bg-dark-bg relative">
          {topologyQuery.isLoading ? (
            <div className="absolute inset-0 flex flex-col items-center justify-center gap-2 text-sm text-neutral-500">
              <Network className="w-8 h-8 text-neutral-300 dark:text-neutral-600 animate-pulse" />
              {isTr ? 'Topoloji yükleniyor...' : 'Loading topology...'}
            </div>
          ) : error ? (
            <div className="absolute inset-0 flex flex-col items-center justify-center gap-3 text-sm text-rose-600 dark:text-rose-400 p-6 text-center">
              <Network className="w-8 h-8" />
              <span>{error}</span>
              <Button variant="outline" size="sm" onClick={() => void topologyQuery.refetch()}>
                {isTr ? 'Tekrar Dene' : 'Retry'}
              </Button>
            </div>
          ) : nodes.length === 0 ? (
            <div className="absolute inset-0 flex flex-col items-center justify-center gap-3 text-sm text-neutral-500 dark:text-neutral-400 p-8 text-center">
              <PlugZap className="w-10 h-10 mx-auto text-neutral-300 dark:text-neutral-600" />
              <h3 className="font-bold text-neutral-900 dark:text-white">
                {isTr ? 'Haritalanacak topoloji yok' : 'No topology to map yet'}
              </h3>
              <p className="max-w-md">
                {isTr
                  ? 'Altyapı grafiği, entegrasyonların gerçek keşif çalışmaları tamamlandığında oluşturulur. Yeni bir sunucu veya uygulama keşfedildiğinde buraya otomatik eklenir; manuel düğüm eklemesi gerekmez.'
                  : 'The infrastructure graph is built when real discovery runs from your integrations complete. Newly discovered servers or applications appear here automatically; no manual node creation is needed.'}
              </p>
              <ButtonLink to="/integrations" variant="primary" size="sm" className="mt-2 gap-2 bg-brand-primary">
                <PlugZap className="w-4 h-4" />
                {isTr ? 'Keşif başlat' : 'Start discovery'}
              </ButtonLink>
            </div>
          ) : (
            <ReactFlow
              nodes={nodes}
              edges={edges}
              onNodesChange={onNodesChange}
              onEdgesChange={onEdgesChange}
              onNodeClick={onNodeClick}
              onPaneClick={closeDetail}
              colorMode={isDark ? 'dark' : 'light'}
              fitView
            >
              <Background color={tokens.border} gap={16} />
              <Controls className="!bg-white dark:!bg-dark-card !border-neutral-200 dark:!border-dark-border" />
              <MiniMap
                className="!bg-white/80 dark:!bg-dark-card/80 !border-neutral-200 dark:!border-dark-border"
                nodeColor={() => tokens.brand}
                maskColor={isDark ? 'rgb(17 19 24 / 0.6)' : 'rgb(245 246 248 / 0.6)'}
              />
            </ReactFlow>
          )}

          {searchText && matchedCount === 0 && nodes.length > 0 && (
            <div
              role="status"
              className="absolute top-3 left-1/2 -translate-x-1/2 z-10 max-w-[90%] px-4 py-2 rounded-lg border border-amber-200 dark:border-amber-900 bg-amber-50 dark:bg-amber-950/80 text-xs text-amber-800 dark:text-amber-300 shadow-sm flex items-center gap-2"
            >
              <AlertTriangle className="w-3.5 h-3.5 shrink-0" aria-hidden="true" />
              <span>
                {isTr
                  ? `"${queryInput.trim()}" ile eşleşen düğüm yok. Ad, FQDN, IP veya tür ile arayın.`
                  : `No nodes match "${queryInput.trim()}". Search by name, FQDN, IP or type.`}
              </span>
              <button
                type="button"
                onClick={() => setQueryInput('')}
                className="font-semibold hover:underline shrink-0"
              >
                {isTr ? 'Temizle' : 'Clear'}
              </button>
            </div>
          )}
        </div>

        {/* Selected Node Details Side Panel (real data); an overlay on small screens */}
        {selectedNode && (
          <Card className="absolute inset-y-0 right-0 z-20 w-full max-w-80 lg:static lg:w-80 lg:max-w-none p-5 flex flex-col justify-between border-neutral-200 dark:border-dark-border bm-slide-in-right overflow-y-auto shadow-xl lg:shadow-none">
            <div className="min-w-0">
              <div className="flex items-center justify-between border-b border-neutral-200 dark:border-dark-border pb-3">
                <div className="flex items-center gap-2">
                  {nodeIcon(selectedNode.nodeType)}
                  <span className="text-xs font-bold uppercase tracking-wider text-neutral-500">
                    {isTr ? 'Bileşen Detayı' : 'Component Detail'}
                  </span>
                </div>
                <div className="flex items-center gap-2">
                  <StatusIndicator status={toNodeStatus(selectedNode.status)} size="sm" />
                  <button
                    type="button"
                    onClick={closeDetail}
                    aria-label={isTr ? 'Detayı kapat' : 'Close detail'}
                    className="p-1 rounded text-neutral-400 hover:text-neutral-600 dark:hover:text-neutral-200"
                  >
                    <X className="w-4 h-4" />
                  </button>
                </div>
              </div>

              <div className="mt-4 space-y-3">
                <div className="min-w-0">
                  <h4 className="font-bold text-base text-neutral-900 dark:text-white break-words">
                    {selectedNode.name}
                  </h4>
                  <span className="text-xs text-neutral-500 font-mono break-all">
                    {selectedNode.fqdn || selectedNode.id}
                  </span>
                  {selectedNode.isStale && (
                    <span className="mt-1 inline-flex text-[10px] px-1.5 py-0.5 rounded bg-amber-50 dark:bg-amber-950/30 text-amber-700 dark:text-amber-400 font-semibold">
                      {isTr ? 'Bayat — son keşifte görülmedi' : 'Stale — not seen in the latest discovery'}
                    </span>
                  )}
                </div>

                <div className="flex flex-wrap gap-2">
                  <ButtonLink
                    to={`/inventory?node=${encodeURIComponent(selectedNode.id)}`}
                    variant="outline"
                    size="sm"
                    className="gap-1.5"
                  >
                    <ExternalLink className="w-3.5 h-3.5" />
                    {isTr ? 'Envanterde aç' : 'Open in inventory'}
                  </ButtonLink>
                  <ButtonLink
                    to={`/simulation?target=${encodeURIComponent(selectedNode.id)}`}
                    variant="outline"
                    size="sm"
                    className="gap-1.5"
                  >
                    <Play className="w-3.5 h-3.5" />
                    {isTr ? 'Simülasyon' : 'Simulation'}
                  </ButtonLink>
                </div>

                <div className="space-y-2 text-xs pt-2">
                  <div className="flex justify-between py-1 border-b border-neutral-100 dark:border-dark-border">
                    <span className="text-neutral-500">{isTr ? 'Tür:' : 'Type:'}</span>
                    <span lang="en" className="font-medium uppercase text-neutral-800 dark:text-neutral-200">
                      {selectedNode.nodeType}
                    </span>
                  </div>
                  {Boolean(selectedNode.ipAddress) && (
                    <div className="flex justify-between py-1 border-b border-neutral-100 dark:border-dark-border">
                      <span className="text-neutral-500">{isTr ? 'IP Adresi:' : 'IP Address:'}</span>
                      <span className="font-mono text-neutral-800 dark:text-neutral-200">
                        {String(selectedNode.ipAddress)}
                      </span>
                    </div>
                  )}
                  <div className="flex justify-between py-1 border-b border-neutral-100 dark:border-dark-border">
                    <span className="text-neutral-500">{isTr ? 'Keşif Kaynağı:' : 'Discovery Source:'}</span>
                    <span className="font-medium text-brand-primary">
                      {selectedNode.sourceSystems.join(' + ') || selectedNode.discoverySource}
                    </span>
                  </div>
                  <div className="flex justify-between py-1 border-b border-neutral-100 dark:border-dark-border">
                    <span className="text-neutral-500">{isTr ? 'Son Görülme:' : 'Last Seen:'}</span>
                    <span className="text-neutral-800 dark:text-neutral-200">
                      {formatDateTime(selectedNode.lastSeen)}
                    </span>
                  </div>
                  <div className="flex justify-between py-1 border-b border-neutral-100 dark:border-dark-border">
                    <span className="text-neutral-500">{isTr ? 'Kaynak Sayısı:' : 'Source Count:'}</span>
                    <span className="font-medium text-neutral-800 dark:text-neutral-200">
                      {selectedNode.sourceSystems.length}
                    </span>
                  </div>
                </div>

                <div className="pt-2">
                  <div className="text-[10px] uppercase tracking-wide text-neutral-500 mb-1.5">
                    {isTr ? `İlişkiler (${relevantRelations.length})` : `Relationships (${relevantRelations.length})`}
                  </div>
                  {relevantRelations.length === 0 ? (
                    <p className="text-[11px] text-neutral-500">
                      {isTr ? 'Bu düğüm için kayıtlı ilişki yok.' : 'No recorded relationships for this node.'}
                    </p>
                  ) : (
                    <div className="space-y-1">
                      {relevantRelations.slice(0, 12).map((rel) => {
                        const outgoing = rel.sourceNodeId === selectedNode.id;
                        const otherId = outgoing ? rel.targetNodeId : rel.sourceNodeId;
                        const otherName = outgoing ? rel.targetNodeName : rel.sourceNodeName;
                        const onMap = nodeById.has(otherId);
                        return (
                          <button
                            key={rel.id}
                            type="button"
                            disabled={!onMap}
                            onClick={() => setSelectedNodeId(otherId)}
                            title={onMap ? undefined : isTr ? 'Bu düğüm haritada gösterilmiyor' : 'This node is not shown on the map'}
                            className={`w-full text-left text-[11px] text-neutral-600 dark:text-neutral-300 flex items-center gap-1 rounded px-1 py-0.5 enabled:hover:bg-neutral-100 dark:enabled:hover:bg-neutral-800 disabled:cursor-default ${
                              rel.isStale ? 'opacity-60 line-through decoration-dotted' : ''
                            }`}
                          >
                            <span lang="en" className="font-mono text-[10px] text-neutral-400 shrink-0">{rel.relationshipType}</span>
                            <span className={`truncate ${onMap ? 'text-brand-primary dark:text-brand-primary-dark' : ''}`}>
                              {outgoing ? '→' : '←'} {otherName}
                            </span>
                          </button>
                        );
                      })}
                      {relevantRelations.length > 12 && (
                        <p className="text-[10px] text-neutral-500">
                          +{relevantRelations.length - 12} {isTr ? 'ilişki daha' : 'more'}
                        </p>
                      )}
                    </div>
                  )}
                </div>
              </div>
            </div>

            <div className="pt-4 border-t border-neutral-200 dark:border-dark-border">
              <Button
                variant="outline"
                size="sm"
                className="w-full"
                onClick={closeDetail}
              >
                {isTr ? 'Kapat' : 'Close'}
              </Button>
            </div>
          </Card>
        )}
      </div>
    </div>
  );
};
