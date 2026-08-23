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
  const systemDevice = page.getByRole('button', { name: '打开 SYSTEM-DEV-PC' });
  await expect(systemDevice).toBeVisible({ timeout: 20_000 });
  await systemDevice.click();

  await expect(page.getByRole('main').getByText('D:\\Projects\\SystemTest', { exact: true })).toBeVisible();
  await expect(page.getByRole('main').getByText('等待远程控制', { exact: true }))
    .toBeVisible({ timeout: 20_000 });
  const approval = page.getByRole('region', { name: '等待审批' });
  await expect(approval).toBeVisible();
  await page.getByRole('button', { name: '打开会话栏' }).click();
  await expect(page.getByRole('button', { name: /系统历史会话/u })).toBeVisible();
  await page.getByRole('button', { name: '关闭会话栏' }).first().click();

  await page.getByPlaceholder('不要修改数据库结构，只调整业务层。').fill('真实 Relay Steer');
  await page.getByRole('button', { name: '发送 Steer' }).click();
  await expect(page.getByText('干预已送入当前 Turn')).toBeVisible();

  await approval.getByRole('button', { name: '允许一次' }).click();
  await expect(page.getByText('审批结果已提交')).toBeVisible();

  await page.getByRole('button', { name: '停止当前任务' }).click();
  await expect(page.getByText('停止请求已接受，等待 Interrupted 终态')).toBeVisible();
  await expect(page.getByText('任务已停止')).toBeVisible();

  await page.getByRole('button', { name: '打开会话栏' }).click();
  await page.getByRole('button', { name: /系统历史会话/u }).click();
  await expect(page.getByText('系统历史用户消息', { exact: true })).toBeVisible();
  await expect(page.getByText('系统历史助手回复', { exact: true })).toBeVisible();
  await page.getByLabel('恢复 Desktop 外部会话').fill('真实 Relay 恢复历史会话');
  await page.getByRole('button', { name: '恢复并由 Agent 托管' }).click();
  await expect(page.getByText('历史会话已恢复，真实 Turn 已启动')).toBeVisible();
  await expect(page.getByText('远程会话运行中')).toBeVisible();

  await page.reload();
  await expect(page.getByRole('button', { name: '停止当前任务' })).toBeVisible({ timeout: 20_000 });
  await expect(page.getByText('SYSTEM-DEV-PC')).toBeVisible();
});
