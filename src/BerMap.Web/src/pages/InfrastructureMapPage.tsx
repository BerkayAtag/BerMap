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
import { useThemeStore } from '@/stores/themeStore';
import { useLocale } from '@/lib/i18n';
import { api } from '@/lib/api';
import { formatDateTime, getErrorMessage, toNodeStatus } from '@/lib/format';
import type { GraphQueryResult, InfrastructureNode } from '@/types';
import { Server, Database, Cpu, Layers, Search, Network, RefreshCw, PlugZap } from 'lucide-react';

const TYPE_GROUP_ORDER: Record<string, number> = {
  Datacenter: 0,
  Site: 1,
  Forest: 1,
  Domain: 1,
  OrganizationalUnit: 2,
  Cluster: 2,
  ResourcePool: 3,
  Hypervisor: 3,
  Storage: 4,
  Datastore: 4,
  Network: 4,
  NetworkDevice: 4,
  Computer: 5,
  DomainController: 5,
  VirtualMachine: 6,
  PhysicalServer: 6,
  Endpoint: 7,
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
  isDark: boolean,
): { nodes: Node[]; edges: Edge[] } {
  const nodesByType = new Map<string, InfrastructureNode[]>();
  for (const node of data.nodes) {
    const list = nodesByType.get(node.nodeType) ?? [];
    list.push(node);
    nodesByType.set(node.nodeType, list);
  }

  const groups = Array.from(nodesByType.entries()).sort(
    (a, b) => (TYPE_GROUP_ORDER[a[0]] ?? 50) - (TYPE_GROUP_ORDER[b[0]] ?? 50) || a[0].localeCompare(b[0]),
  );

  const positions = new Map<string, { x: number; y: number }>();
  const COLUMN_GAP = 230;
  const ROW_GAP = 110;
  const PER_GROUP_MAX_ROW = 12;

  let column = 0;
  for (const [, nodes] of groups) {
    nodes.slice(0, PER_GROUP_MAX_ROW).forEach((node, index) => {
      positions.set(node.id, {
        x: column * COLUMN_GAP,
        y: index * ROW_GAP,
      });
    });
    // keep the remaining nodes of the same group in a second sub-column
    if (nodes.length > PER_GROUP_MAX_ROW) {
      nodes.slice(PER_GROUP_MAX_ROW, PER_GROUP_MAX_ROW * 2).forEach((node, index) => {
        positions.set(node.id, {
          x: column * COLUMN_GAP + 115,
          y: index * ROW_GAP,
        });
      });
    }
    column += 1;
  }

  const flowNodes: Node[] = data.nodes
    .filter((node) => positions.has(node.id))
    .map((node) => {
      const position = positions.get(node.id)!;
      const borderColor =
        node.nodeType === 'Datacenter' || node.nodeType === 'Application'
          ? '#E30613'
          : node.status === 'warning'
            ? '#F59E0B'
            : node.status === 'critical'
              ? '#DC2626'
              : isDark
                ? '#4B5563'
                : '#D1D5DB';
      return {
        id: node.id,
        type: 'default',
        position,
        data: { label: node.name, status: node.status },
        style: {
          background: isDark ? '#1F2937' : '#FFF',
          color: isDark ? '#FFF' : '#111827',
          borderRadius: '8px',
          border: `1px solid ${borderColor}`,
          padding: '8px 10px',
          fontWeight: node.nodeType === 'Datacenter' ? 'bold' : 'normal',
          width: 200,
          fontSize: 11,
        },
      } satisfies Node;
    });

  const knownIds = new Set(data.nodes.map((node) => node.id));
  const flowEdges: Edge[] = data.relationships
    .filter((rel) => knownIds.has(rel.sourceNodeId) && knownIds.has(rel.targetNodeId))
    .map((rel) => ({
      id: rel.id,
      source: rel.sourceNodeId,
      target: rel.targetNodeId,
      label: rel.relationshipType,
      animated: rel.confidenceLevel?.toLowerCase() === 'verified',
      style: { strokeWidth: 1 },
      labelStyle: { fontSize: 9, fill: isDark ? '#9CA3AF' : '#6B7280' },
    }));

  return { nodes: flowNodes, edges: flowEdges };
}

export const InfrastructureMapPage: React.FC = () => {
  const { theme } = useThemeStore();
  const isDark = theme === 'dark';
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

  // Project real API data into the flow graph whenever it changes.
  useEffect(() => {
    const data = topologyQuery.data;
    if (!data) return;
    const flow = buildFlowGraph(data, isDark);
    setNodes(flow.nodes);
    setEdges(flow.edges);
  }, [topologyQuery.data, isDark, setNodes, setEdges]);

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

  useEffect(() => {
    if (!searchText) {
      setNodes((current) => current.map((node) => ({ ...node, opacity: 1 })));
      return;
    }
    const data = topologyQuery.data;
    if (!data) return;
    const matching = new Set(data.nodes.filter(matchNode).map((node) => node.id));
    setNodes((current) =>
      current.map((node) => ({
        ...node,
        opacity: matching.has(node.id) ? 1 : 0.12,
      })),
    );
  }, [searchText, topologyQuery.data, matchNode, setNodes]);

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

  const onNodeClick = useCallback((_: React.MouseEvent, node: Node) => {
    setSelectedNodeId(node.id);
  }, []);

  return (
    <div className="h-[calc(100vh-6rem)] flex flex-col space-y-4">
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

      {/* Map Control Bar */}
      <div className="flex flex-wrap items-center justify-between gap-3 bg-white dark:bg-dark-card p-3 rounded-lg border border-neutral-200 dark:border-dark-border">
        <div className="flex items-center gap-3">
          <div className="flex items-center gap-2 px-3 py-1.5 rounded-md bg-neutral-100 dark:bg-dark-border text-sm">
            <Search className="w-4 h-4 text-neutral-400" />
            <input
              type="text"
              value={queryInput}
              onChange={(event) => setQueryInput(event.target.value)}
              placeholder={isTr ? 'Haritada filtrele...' : 'Filter on map...'}
              className="bg-transparent text-sm focus:outline-none w-48 text-neutral-800 dark:text-neutral-200"
            />
          </div>

          <Button variant="outline" size="sm" className="gap-1.5" disabled>
            <Layers className="w-3.5 h-3.5" />
            {isTr ? 'Görünüm: Tümü' : 'View: All'}
          </Button>
        </div>

        <div className="flex items-center gap-2">
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
          <span className="text-xs text-neutral-500 dark:text-neutral-400 mr-2">
            {isTr ? 'Gösterilen:' : 'Showing:'}{' '}
            <strong>{nodes.length}</strong> {isTr ? 'düğüm,' : 'nodes,'}{' '}
            <strong>{edges.length}</strong> {isTr ? 'bağlantı' : 'links'}
          </span>
        </div>
      </div>

      {/* Main Graph Canvas Area + Detail Side Panel */}
      <div className="flex-1 flex gap-4 min-h-0 relative">
        <div className="flex-1 rounded-xl border border-neutral-200 dark:border-dark-border overflow-hidden bg-neutral-50 dark:bg-[#0E1015] relative">
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
              <Link to="/integrations" className="mt-2">
                <Button variant="primary" size="sm" className="gap-2 bg-brand-primary">
                  <PlugZap className="w-4 h-4" />
                  {isTr ? 'Keşif başlat' : 'Start discovery'}
                </Button>
              </Link>
            </div>
          ) : (
            <ReactFlow
              nodes={nodes}
              edges={edges}
              onNodesChange={onNodesChange}
              onEdgesChange={onEdgesChange}
              onNodeClick={onNodeClick}
              fitView
            >
              <Background color={isDark ? '#343945' : '#D1D5DB'} gap={16} />
              <Controls className="!bg-white dark:!bg-dark-card !border-neutral-200 dark:!border-dark-border" />
              <MiniMap
                className="!bg-white/80 dark:!bg-dark-card/80 !border-neutral-200 dark:!border-dark-border"
                nodeColor={() => '#E30613'}
              />
            </ReactFlow>
          )}
        </div>

        {/* Selected Node Details Side Panel (real data) */}
        {selectedNode && (
          <Card className="w-80 p-5 flex flex-col justify-between border-neutral-200 dark:border-dark-border animate-in slide-in-from-right-4 duration-200">
            <div className="min-w-0">
              <div className="flex items-center justify-between border-b border-neutral-200 dark:border-dark-border pb-3">
                <div className="flex items-center gap-2">
                  {nodeIcon(selectedNode.nodeType)}
                  <span className="text-xs font-bold uppercase tracking-wider text-neutral-500">
                    {isTr ? 'Bileşen Detayı' : 'Component Detail'}
                  </span>
                </div>
                <StatusIndicator status={toNodeStatus(selectedNode.status)} size="sm" />
              </div>

              <div className="mt-4 space-y-3">
                <div className="min-w-0">
                  <h4 className="font-bold text-base text-neutral-900 dark:text-white break-words">
                    {selectedNode.name}
                  </h4>
                  <span className="text-xs text-neutral-500 font-mono break-all">
                    {selectedNode.fqdn || selectedNode.id}
                  </span>
                </div>

                <div className="space-y-2 text-xs pt-2">
                  <div className="flex justify-between py-1 border-b border-neutral-100 dark:border-dark-border">
                    <span className="text-neutral-500">{isTr ? 'Tür:' : 'Type:'}</span>
                    <span className="font-medium uppercase text-neutral-800 dark:text-neutral-200">
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
                      {relevantRelations.slice(0, 12).map((rel) => (
                        <div
                          key={rel.id}
                          className="text-[11px] text-neutral-600 dark:text-neutral-300 flex items-center gap-1"
                        >
                          <span className="font-mono text-[10px] text-neutral-400">{rel.relationshipType}</span>
                          <span>
                            {rel.sourceNodeId === selectedNode.id
                              ? `${isTr ? '→' : '→'} ${rel.targetNodeName}`
                              : `${isTr ? '←' : '←'} ${rel.sourceNodeName}`}
                          </span>
                        </div>
                      ))}
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
                onClick={() => setSelectedNodeId(null)}
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
