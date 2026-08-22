import { expect, test } from '@playwright/test';

test('real Relay validates browser crypto pairing and routes all controls', async ({ page, request }, testInfo) => {
  await page.addInitScript(() => {
    localStorage.setItem('codex-control-relay-url', 'ws://127.0.0.1:5080/ws/controller');
  });

  let pairingCode = '';
  await expect.poll(async () => {
    const response = await request.get(
      `http://127.0.0.1:5080/test/pairing-code?token=${encodeURIComponent(testInfo.project.name)}`,
    );
    if (!response.ok()) return false;
    pairingCode = (await response.json()).code as string;
    return pairingCode.length === 6;
  }).toBe(true);

  await page.goto('/');
  await page.getByLabel('六位配对码').fill(pairingCode);
  await page.getByRole('button', { name: '配对', exact: true }).click();
  await expect(page.getByText('SYSTEM-DEV-PC')).toBeVisible();
  await page.getByRole('button', { name: '打开 SYSTEM-DEV-PC' }).click();

  await expect(page.getByText('D:\\Projects\\SystemTest', { exact: true })).toBeVisible();
  await expect(page.getByText('等待远程控制')).toBeVisible();
  const approval = page.getByRole('region', { name: '等待审批' });
  await expect(approval).toBeVisible();

  await page.getByPlaceholder('不要修改数据库结构，只调整业务层。').fill('真实 Relay Steer');
  await page.getByRole('button', { name: '发送 Steer' }).click();
  await expect(page.getByText('干预已送入当前 Turn')).toBeVisible();

  await page.getByRole('button', { name: '停止当前任务' }).click();
  await expect(page.getByText('停止请求已接受，等待 Interrupted 终态')).toBeVisible();

  await approval.getByRole('button', { name: '允许一次' }).click();
  await expect(page.getByText('审批结果已提交')).toBeVisible();

  await page.reload();
  await expect(page.getByRole('heading', { name: '开发电脑' })).toBeVisible();
  await expect(page.getByText('SYSTEM-DEV-PC')).toBeVisible();
});
