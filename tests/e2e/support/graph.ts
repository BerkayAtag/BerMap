import { apiRequest, type NodeDto } from './api';
import { adminToken } from './state';

/** The seeded node with this exact name (and type, when names repeat across sources). */
export async function nodeByName(name: string, nodeType?: string): Promise<NodeDto> {
  const results = await apiRequest<NodeDto[]>('GET', `/api/graph/search?q=${encodeURIComponent(name)}`, {
    token: adminToken(),
  });
  const node = results.find((n) => n.name === name && (!nodeType || n.nodeType === nodeType));
  if (!node) throw new Error(`Seeded node ${name} (${nodeType ?? 'any type'}) not found`);
  return node;
}

export interface Topology {
  nodes: Array<NodeDto & { ipAddress?: string | null; fqdn: string }>;
  relationships: Array<{ sourceNodeId: string; targetNodeId: string }>;
}

export async function topology(): Promise<Topology> {
  return apiRequest<Topology>('GET', '/api/graph/topology?depth=3', { token: adminToken() });
}
