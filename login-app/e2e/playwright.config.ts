import { defineConfig } from '@playwright/test';

/**
 * Browser suite for the accept-invite flows (e2e/accept-invite.spec.ts). It runs against a live
 * Authagonal.Server rather than a dev server, because every flow redeems a real invite token:
 *
 *   QA_BASE_URL          the server (the SPA is served from the same origin), e.g. http://127.0.0.1:18471
 *   QA_CAPTURE_URL       the stand-in for Resend that Email:ResendApiUrl points at; the suite reads each
 *                        invite link from the email captured there
 *   QA_ADMIN_CRED_FILE   JSON file of { client_id, client_secret, scope } for a client_credentials client
 *                        allowed the admin scope; the suite makes its own organisations and invites with it
 *   QA_TABLE_ENDPOINT    optional, the Table storage endpoint the server persists grants in
 *                        (default: Azurite's http://127.0.0.1:18473/devstoreaccount1); F5 expires a grant there
 *
 * Run: npm run test:e2e   (the browser is Playwright's own Chromium; `npx playwright install chromium`
 * fetches it once). Vitest and the type-check CI runs never see this directory: vitest only collects
 * src/**, and tsc only compiles src/.
 */
export default defineConfig({
  testDir: '.',
  testMatch: '*.spec.ts',
  // One worker: the flows share one server and one capture inbox, and each cleans up what it made.
  workers: 1,
  fullyParallel: false,
  retries: 0,
  timeout: 30_000,
  outputDir: '../node_modules/.tmp/e2e-results',
  reporter: [['list']],
  use: {
    baseURL: process.env.QA_BASE_URL,
    headless: true,
  },
});
