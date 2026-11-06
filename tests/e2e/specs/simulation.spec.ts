import type { Page } from '@playwright/test';
import { test, expect, captureDownload } from '../support/fixtures';
import { nodeByName } from '../support/graph';
import { PROD_HOST } from '../support/seed';
import { authFile } from '../support/state';

async function runSimulation(page: Page): Promise<void> {
  const response = page.waitForResponse((r) => r.url().endsWith('/api/simulations/run') && r.request().method() === 'POST');
  await page.getByRole('button', { name: 'Simülasyonu Çalıştır' }).click();
  expect((await response).status()).toBe(200);
  await expect(page.getByText('Simülasyon Çıktısı (Etki Yarıçapı Raporu)')).toBeVisible();
  await expect(page.getByText(/Risk Seviyesi: .+\(Skor:/)).toBeVisible();
}

test.describe('Simülasyon', () => {
  test('?target= ile host kesintisi: N-1 kapasite, Markdown risk raporu ve yazdırma görünümü', async ({ page, problems }) => {
    const host = await nodeByName(PROD_HOST, 'Hypervisor');
    await page.goto(`/simulation?target=${host.id}`);

    // The scenario follows from the node type; the review step names the target.
    await expect(page.getByRole('button', { name: /^Host Kesintisi/ })).toHaveAttribute('aria-pressed', 'true');
    await expect(page.getByRole('option', { name: new RegExp(PROD_HOST.replace(/\./g, '\\.')) })).toHaveAttribute(
      'aria-selected',
      'true',
    );
    await runSimulation(page);
    await expect(page.getByText('N-1 Sonrası Kalan Bellek')).toBeVisible();
    await expect(page.getByText('N-1 Sonrası Kalan CPU')).toBeVisible();
    // VMs on esx01 (app-core-01, mq-broker-01, dc01) are confirmed impacts.
    await expect(page.getByText('app-core-01').first()).toBeVisible();

    const report = await captureDownload(page, () => page.getByRole('button', { name: 'Raporu indir' }).click());
    expect(report.filename).toMatch(/^bermap-risk-report-esx01-corp-bermap-demo-host_failure-\d{4}-\d{2}-\d{2}-\d{2}-\d{2}\.md$/);
    expect(report.text).toContain('# Değişiklik Risk Raporu');
    expect(report.text).toContain(PROD_HOST);
    expect(report.text).toContain('Host Kesintisi (host\\_failure)');
    expect(report.text).toContain('## Risk skoru');
    expect(report.text).toContain('## Cluster kapasitesi');
    expect(report.text).toContain('## Doğrulanmış etkiler');
    expect(report.text).toMatch(/\| app-core-01 \| VirtualMachine \|/);
    expect(report.text).toContain('## Öneriler');

    // Count print() calls of the print view instead of opening the system dialog.
    await page.evaluate(() => {
      const open = window.open.bind(window);
      window.open = (...args: Parameters<typeof window.open>) => {
        const view = open(...args);
        if (view) {
          view.print = () => {
            (window as unknown as { __printCalls: number }).__printCalls =
              ((window as unknown as { __printCalls?: number }).__printCalls ?? 0) + 1;
          };
        }
        return view;
      };
    });
    const [printView] = await Promise.all([page.waitForEvent('popup'), page.getByRole('button', { name: 'Yazdır' }).click()]);
    await expect(printView.locator('body')).toContainText('Değişiklik Risk Raporu');
    await expect(printView.locator('body')).toContainText(PROD_HOST);
    await expect(printView.getByRole('button', { name: 'Yazdır' })).toBeVisible();
    expect(await printView.title()).toContain('Değişiklik Risk Raporu');
    await expect.poll(() => page.evaluate(() => (window as unknown as { __printCalls?: number }).__printCalls ?? 0)).toBe(1);
    await printView.close();

    await page.getByRole('button', { name: 'Sıfırla' }).click();
    await expect(page.getByText('Simülasyon Çıktısı (Etki Yarıçapı Raporu)')).toBeHidden();
    problems.expectNone();
  });

  test('iş yükü kesintisi listeden hedef seçilerek çalışır ve bağımlı uygulamaları bulur', async ({ page, problems }) => {
    const sql = await nodeByName('sql-core-01', 'VirtualMachine');
    await page.goto('/simulation');
    await expect(page.getByRole('button', { name: 'Simülasyonu Çalıştır' })).toBeDisabled();
    await page.getByRole('button', { name: /^İş Yükü Kesintisi/ }).click();
    await expect(page).toHaveURL(/scenario=workload_outage/);

    await page.getByRole('searchbox', { name: 'Simülasyon hedefi' }).fill('sql-core');
    await page.getByRole('option', { name: /sql-core-01/ }).first().click();
    await expect(page).toHaveURL(new RegExp(`target=${sql.id}`));

    await runSimulation(page);
    // app-core-01, app-core-02 and monitor-01 use sql-core-01.
    for (const dependent of ['app-core-01', 'app-core-02', 'monitor-01']) {
      await expect(page.getByText(dependent).first()).toBeVisible();
    }
    await expect(page.getByText('N-1 Sonrası Kalan Bellek')).toHaveCount(0);

    // Switching to a scenario the target cannot take drops the target instead of failing.
    await page.getByRole('button', { name: /^Host Kesintisi/ }).click();
    await expect(page).not.toHaveURL(/target=/);
    await expect(page.getByRole('button', { name: 'Simülasyonu Çalıştır' })).toBeDisabled();
    problems.expectNone();
  });

  test.describe('Salt-Okunur Görüntüleyici', () => {
    test.use({ storageState: authFile('viewer') });

    test('simülasyon salt okunur olduğu için çalıştırabilir', async ({ page, problems }) => {
      const sql = await nodeByName('sql-payments-01', 'VirtualMachine');
      await page.goto(`/simulation?target=${sql.id}&scenario=workload_outage`);
      await runSimulation(page);
      await expect(page.getByText('app-payments-01').first()).toBeVisible();
      problems.expectNone();
    });
  });
});
