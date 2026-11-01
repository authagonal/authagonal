// The accept-invite page, driven in a real browser against a live Authagonal.Server: one test per flow,
// titled with its flow id and a colon (F1: .. F10:). Each makes its own invite through the admin API and
// follows the link from the email the capture server holds. See playwright.config.ts for the environment.
import { test, expect, type Page } from '@playwright/test';
import { admin, cleanup, expireGrant, invite, PREFIX } from './support';

const GOOD = 'Passw0rd!x1';
const INVALID_OR_EXPIRED =
  'This invitation link is invalid or has expired. Ask the person who invited you to send a new one.';

test.afterEach(cleanup);

const content = (page: Page) => page.locator('[data-auth="content"]');
const title = (page: Page) => content(page).locator('h2');

async function submit(page: Page, password = GOOD, confirmation = password) {
  await page.fill('#newPassword', password);
  await page.fill('#confirmPassword', confirmation);
  await page.click('button[type=submit]');
}

test('F1: an organisation invite names the organisation', async ({ page }) => {
  const inv = await invite('f1', true);
  expect(inv.path).toContain(`organization=${inv.orgSlug}`);
  await page.goto(inv.path);
  await expect(title(page)).toContainText(inv.orgSlug!);
  await expect(content(page)).toContainText(`invited to join ${inv.orgSlug}`);
  await expect(content(page).locator('form')).toHaveCount(1);
});

test('F2: an invite with no organisation is generic', async ({ page }) => {
  const inv = await invite('f2', false);
  expect(inv.path).not.toContain('organization=');
  await page.goto(inv.path);
  await expect(title(page)).toHaveText('Set up your account');
  await expect(content(page)).not.toContainText('invited to join');
  await expect(content(page).locator('form')).toHaveCount(1);
});

test('F3: a link with no token shows the invalid-link state and no form', async ({ page }) => {
  await page.goto('/accept-invite');
  await expect(title(page)).toHaveText('Invalid invitation');
  await expect(content(page)).toContainText(INVALID_OR_EXPIRED);
  await expect(page.locator('form')).toHaveCount(0);
});

test('F4: an unknown token shows the invalid-or-expired message', async ({ page }) => {
  await page.goto(`/accept-invite?p=${PREFIX}unknown-token`);
  await submit(page);
  await expect(content(page)).toContainText(INVALID_OR_EXPIRED);
  await expect(page.locator('#newPassword')).toBeVisible();
});

test('F5: an expired token shows the same message', async ({ page }) => {
  const inv = await invite('f5', false);
  await expireGrant(inv.userId);
  await page.goto(inv.path);
  // The server must have found the grant and judged it expired (token_expired), not failed to find it
  // (invalid_token, F4's case): that is what makes the grant a really expired one.
  const redeem = page.waitForResponse((r) => r.url().includes('/api/auth/reset-password'));
  await submit(page);
  expect(((await (await redeem).json()) as { error?: string }).error).toBe('token_expired');
  await expect(content(page)).toContainText(INVALID_OR_EXPIRED);
  await expect(page.locator('#newPassword')).toBeVisible();
});

test('F6: a password the client-side policy refuses shows its rule', async ({ page }) => {
  const inv = await invite('f6', false);
  let posted = 0;
  page.on('request', (r) => { if (r.url().includes('/api/auth/reset-password')) posted++; });
  await page.goto(inv.path);
  await page.fill('#newPassword', 'short');
  const unmet = content(page).locator('li[data-met="false"]');
  await expect(unmet.first()).toBeVisible();
  await expect(content(page).locator('li', { hasText: 'At least 8 characters' })).toHaveAttribute('data-met', 'false');
  await page.fill('#confirmPassword', 'short');
  await expect(page.locator('button[type=submit]')).toBeDisabled();
  expect(posted).toBe(0);
});

test('F7: a password the server refuses (weak_password) shows the server message', async ({ page }) => {
  const inv = await invite('f7', false);
  await page.goto(inv.path);
  // Meets every rule the client checks (it counts é as a special character), but the server's
  // non-alphanumeric rule does not.
  await submit(page, 'Passw0rdé');
  await expect(content(page)).toContainText('Password must contain at least one non-alphanumeric character.');
  await expect(content(page).locator('li[data-met="false"]')).toHaveCount(0);
});

test('F8: mismatched passwords are refused', async ({ page }) => {
  const inv = await invite('f8', false);
  let posted = 0;
  page.on('request', (r) => { if (r.url().includes('/api/auth/reset-password')) posted++; });
  await page.goto(inv.path);
  await submit(page, GOOD, GOOD + '2');
  await expect(content(page)).toContainText('Passwords do not match');
  expect(posted).toBe(0);
});

test('F9: a good password redeems the token and the membership is active', async ({ page }) => {
  const inv = await invite('f9', true);
  const before = await admin('GET', `/api/v1/organizations/${inv.orgId}/members`);
  expect(before.json.items.map((m: { status: string }) => m.status)).toEqual(['invited']);

  await page.goto(inv.path);
  await submit(page);
  await expect(title(page)).toHaveText("You're all set");
  await expect(content(page).locator('a[href="/login"]')).toBeVisible();

  const after = await admin('GET', `/api/v1/organizations/${inv.orgId}/members`);
  expect(after.json.items.map((m: { userId: string; status: string }) => [m.userId, m.status]))
    .toEqual([[inv.userId, 'active']]);
});

test('F10: /accept-invite renders outside /login and /login still renders', async ({ page }) => {
  await page.goto('/accept-invite?p=x');
  expect(new URL(page.url()).pathname).toBe('/accept-invite');
  await expect(content(page).locator('form')).toHaveCount(1);

  await page.goto('/login');
  expect(new URL(page.url()).pathname).toBe('/login');
  await expect(page.locator('#email')).toBeVisible();
  await expect(content(page).locator('form')).toHaveCount(1);
  await expect(page.locator('#newPassword')).toHaveCount(0);
});
