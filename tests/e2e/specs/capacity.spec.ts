import { apiRequest, type IntegrationDto } from '../support/api';
import { enrollmentToken } from '../support/env';
import { test, expect } from '../support/fixtures';
import { DOMAIN } from '../support/seed';
import { adminToken } from '../support/state';

const THRESHOLD_INTEGRATION = 'E2E · kapasite eşiği';

/**
 * The demo clusters all stay below 80 %. A private collector-managed
 * integration adds one cluster with a host above each threshold.
 */
async function ensureThresholdCluster(): Promise<void> {
  const token = adminToken();
  const list = await apiRequest<IntegrationDto[]>('GET', '/api/integrations', { token });
  let integration = list.find((i) => i.name === THRESHOLD_INTEGRATION);
  integration ??= await apiRequest<IntegrationDto>('POST', '/api/integrations', {
    token,
    body: {
      name: THRESHOLD_INTEGRATION,
      description: 'Kapasite eşiklerini doğrulayan sentetik cluster',
      adapterType: 'VMware',
      configuration: { server: `vcenter-threshold.${DOMAIN}`, port: 443, username: 'threshold' },
      isEnabled: true,
      collectorManaged: true,
    },
  });
  const host = (ext: string, name: string, cpu: number, mem: number) => ({
    externalId: ext,
    nodeType: 'Hypervisor',
    name,
    fqdn: name,
    ipAddress: null,
    status: 'healthy',
    sourceSystem: 'VMware',
    properties: { CpuUsagePercent: String(cpu), MemoryUsagePercent: String(mem), Cluster: 'CL-E2E-THRESHOLD' },
  });
  await apiRequest('POST', '/api/collectors/submit-batch?final=true', {
    headers: { 'X-BerMap-Enrollment-Token': enrollmentToken() },
    body: {
      success: true,
      status: 'Completed',
      integrationId: integration.id,
      sourceVersion: 'e2e',
      nodes: [
        {
          externalId: 'cluster-e2e-threshold',
          nodeType: 'Cluster',
          name: 'CL-E2E-THRESHOLD',
          fqdn: '',
          ipAddress: null,
          status: 'healthy',
          sourceSystem: 'VMware',
          properties: { HostCount: '2', VmCount: '0', CpuUsagePercent: '84', MemoryUsagePercent: '91', HaEnabled: 'True', DrsEnabled: 'False' },
        },
        host('host-e2e-warn', `esx-e2e-warn.${DOMAIN}`, 81, 60),
        host('host-e2e-crit', `esx-e2e-crit.${DOMAIN}`, 40, 95),
      ],
      relationships: [
        { sourceExternalId: 'host-e2e-warn', targetExternalId: 'cluster-e2e-threshold', relationshipType: 'MemberOf', confidenceLevel: 'Verified', evidence: '', sourceSystem: 'VMware' },
        { sourceExternalId: 'host-e2e-crit', targetExternalId: 'cluster-e2e-threshold', relationshipType: 'MemberOf', confidenceLevel: 'Verified', evidence: '', sourceSystem: 'VMware' },
      ],
    },
  });
}

test.describe('Kapasite İstihbaratı', () => {
  test('eşikler: %80 uyarı, %90 kritik; demo cluster ve hostlar doğru değerlerle listelenir', async ({ page, problems }) => {
    await ensureThresholdCluster();
    await page.goto('/capacity');
    await expect(page.getByText('CPU veya RAM ≥ %80 uyarı, ≥ %90 kritik')).toBeVisible();

    const row = (name: string) => page.getByRole('row').filter({ has: page.getByRole('link', { name, exact: true }) });

    await expect(row('CL-PROD-01')).toContainText('68%');
    await expect(row('CL-PROD-01')).toContainText('74%');
    await expect(row('CL-PROD-01')).toContainText('Sağlıklı');
    await expect(row('CL-DMZ-01')).toContainText('Sağlıklı');
    await expect(row('CL-E2E-THRESHOLD')).toContainText('91%');
    await expect(row('CL-E2E-THRESHOLD')).toContainText('Kritik');

    await expect(row(`esx03.${DOMAIN}`)).toContainText('79%');
    await expect(row(`esx03.${DOMAIN}`)).toContainText('Sağlıklı');
    await expect(row(`esx-e2e-warn.${DOMAIN}`)).toContainText('Uyarı');
    await expect(row(`esx-e2e-crit.${DOMAIN}`)).toContainText('Kritik');

    // The busiest entries are listed first.
    const hostLinks = page.getByRole('table').nth(1).getByRole('link');
    await expect(hostLinks.first()).toHaveText(`esx-e2e-crit.${DOMAIN}`);

    await row('CL-PROD-01').getByRole('link', { name: 'CL-PROD-01' }).click();
    await expect(page).toHaveURL(/\/inventory\?node=[0-9a-f-]{36}$/);
    await expect(page.getByRole('dialog', { name: 'Varlık detayı' }).getByRole('heading', { level: 2, name: 'CL-PROD-01' })).toBeVisible();
    problems.expectNone();
  });
});
