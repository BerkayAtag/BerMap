import { test, expect } from '../support/fixtures';
import { nodeByName, type Topology } from '../support/graph';

test.describe('Altyapı Haritası', () => {
  test('filtre sayacı eşleşen düğüm sayısını gösterir ve temizlenir', async ({ page, problems }) => {
    // Other specs add nodes in parallel (collector batches), so a count read from
    // the API before or after the page loads can differ from what the page shows.
    // The expected numbers come from the very topology response the page renders.
    const loaded = page.waitForResponse(
      (r) => new URL(r.url()).pathname === '/api/graph/topology' && r.request().method() === 'GET' && r.ok(),
    );
    await page.goto('/map');
    const graph = (await (await loaded).json()) as Topology;
    const total = graph.nodes.length;
    const sqlMatches = graph.nodes.filter(
      (n) => n.name.toLowerCase().includes('sql') || n.fqdn.toLowerCase().includes('sql') || n.nodeType.toLowerCase().includes('sql'),
    ).length;
    expect(sqlMatches).toBeGreaterThanOrEqual(3);

    const counter = page.getByText('Gösterilen:');
    await expect(counter).toContainText(`${total} düğüm`);
    await expect(page.locator('.react-flow__node')).toHaveCount(total);

    const filter = page.getByRole('searchbox', { name: 'Haritada filtrele' });
    await filter.fill('sql');
    await expect(counter).toContainText(`${sqlMatches} / ${total} düğüm`);

    await filter.fill('bu-isimde-dugum-yok');
    await expect(counter).toContainText(`0 / ${total} düğüm`);
    const empty = page.getByRole('status').filter({ hasText: 'ile eşleşen düğüm yok' });
    await expect(empty).toBeVisible();
    await empty.getByRole('button', { name: 'Temizle' }).click();
    await expect(filter).toHaveValue('');
    await expect(counter).toContainText(`${total} düğüm`);
    problems.expectNone();
  });

  test('düğüm paneli envantere ve simülasyona bağlantı verir', async ({ page, problems }) => {
    const host = await nodeByName('esx01.corp.bermap.demo', 'Hypervisor');
    await page.goto('/map');
    const flowNode = page.locator(`.react-flow__node[data-id="${host.id}"]`);
    await flowNode.click();

    const panel = page.getByText('Bileşen Detayı').locator('xpath=ancestor::*[.//a[normalize-space()="Envanterde aç"]][1]');
    await expect(panel.getByRole('heading', { level: 4, name: host.name })).toBeVisible();
    await expect(panel).toContainText('Hypervisor');
    await expect(panel).toContainText(/İlişkiler \(\d+\)/);

    await panel.getByRole('link', { name: 'Envanterde aç' }).click();
    await expect(page).toHaveURL(new RegExp(`/inventory\\?node=${host.id}$`));
    await expect(page.getByRole('dialog', { name: 'Varlık detayı' }).getByRole('heading', { level: 2, name: host.name })).toBeVisible();

    await page.goBack();
    await expect(page).toHaveURL(/\/map$/);
    await page.locator(`.react-flow__node[data-id="${host.id}"]`).click();
    await panel.getByRole('link', { name: 'Simülasyon' }).click();
    await expect(page).toHaveURL(new RegExp(`/simulation\\?target=${host.id}$`));
    await expect(page.getByRole('button', { name: /^Host Kesintisi/ })).toHaveAttribute('aria-pressed', 'true');

    await page.goto('/map');
    await page.locator(`.react-flow__node[data-id="${host.id}"]`).click();
    await expect(panel).toBeVisible();
    await page.keyboard.press('Escape');
    await expect(page.getByText('Bileşen Detayı')).toBeHidden();
    problems.expectNone();
  });
});
