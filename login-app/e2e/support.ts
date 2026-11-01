import { createHmac } from 'node:crypto';
import { readFileSync } from 'node:fs';

/** The environment the loop (or a developer) points the suite at; see playwright.config.ts. */
export const BASE = required('QA_BASE_URL');
export const CAPTURE = required('QA_CAPTURE_URL');
const CRED = JSON.parse(readFileSync(required('QA_ADMIN_CRED_FILE'), 'utf8')) as {
  client_id: string; client_secret: string; scope: string;
};

function required(name: string): string {
  const v = process.env[name];
  if (!v) throw new Error(`${name} is not set (see e2e/playwright.config.ts)`);
  return v;
}

/** Prefix on everything the suite creates, so a leftover is recognisable. */
export const PREFIX = 'authagonal-org-to-library-01-';

// ---- admin API ---------------------------------------------------------------------------------

async function adminToken(): Promise<string> {
  const r = await fetch(`${BASE}/connect/token`, {
    method: 'POST',
    headers: {
      Authorization: 'Basic ' + Buffer.from(`${CRED.client_id}:${CRED.client_secret}`).toString('base64'),
      'Content-Type': 'application/x-www-form-urlencoded',
    },
    body: new URLSearchParams({ grant_type: 'client_credentials', scope: CRED.scope }),
  });
  const j = (await r.json()) as { access_token?: string };
  if (!j.access_token) throw new Error(`admin token: ${r.status} ${JSON.stringify(j)}`);
  return j.access_token;
}

// The admin API's bodies are read ad hoc by the flows (ids, member lists), so they stay loosely typed.
// eslint-disable-next-line @typescript-eslint/no-explicit-any
export type AdminResponse = { status: number; json: any };

export async function admin(method: string, path: string, body?: unknown): Promise<AdminResponse> {
  const r = await fetch(BASE + path, {
    method,
    headers: { Authorization: 'Bearer ' + (await adminToken()), 'Content-Type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  return { status: r.status, json: await r.json().catch(() => null) };
}

// ---- invites, as the invitee receives them -----------------------------------------------------

export interface Invite {
  userId: string;
  orgId?: string;
  orgSlug?: string;
  /** The invite link's path and query, read from the captured email. */
  path: string;
  /** The `p` parameter of that link. */
  token: string;
}

/** What a test made on the server, removed again by `cleanup`. */
const made: { orgs: string[]; users: string[] } = { orgs: [], users: [] };

/**
 * Invites a new user through the admin API and returns the link the invitation email carries. The link
 * is read from the email the capture server holds (the stand-in for Resend), never composed here.
 */
export async function invite(tag: string, withOrganization: boolean, orgName?: string): Promise<Invite> {
  let orgId: string | undefined;
  let orgSlug: string | undefined;
  if (withOrganization) {
    orgSlug = `${PREFIX}e2e-${tag}`;
    const o = await admin('POST', '/api/v1/organizations', { slug: orgSlug, name: orgName ?? orgSlug });
    if (o.status !== 201) throw new Error(`create organisation: ${o.status} ${JSON.stringify(o.json)}`);
    orgId = o.json.id as string;
    made.orgs.push(orgId);
  }
  const email = `${PREFIX}e2e-${tag}@authagonal-org-to-library-01.test`;
  const i = await admin('POST', '/api/v1/profile/invite', { email, organizationId: orgId });
  if (i.status !== 200) throw new Error(`invite: ${i.status} ${JSON.stringify(i.json)}`);
  made.users.push(i.json.userId as string);

  let href = '';
  for (let n = 0; n < 80 && !href; n++) {
    const got = (await (await fetch(`${CAPTURE}/captured?to=${encodeURIComponent(email)}`)).json()) as { html?: string }[];
    const m = got.length ? /href="([^"]*accept-invite[^"]*)"/.exec(got[got.length - 1].html ?? '') : null;
    if (m) href = m[1].replace(/&amp;/g, '&');
    else await new Promise((r) => setTimeout(r, 250));
  }
  if (!href) throw new Error(`no invite email captured for ${email}`);
  const u = new URL(href);
  return { userId: i.json.userId as string, orgId, orgSlug, path: u.pathname + u.search, token: u.searchParams.get('p')! };
}

/** Removes the organisations and users the test made. */
export async function cleanup(): Promise<void> {
  for (const o of made.orgs.splice(0)) await admin('DELETE', `/api/v1/organizations/${o}`);
  for (const u of made.users.splice(0)) await admin('DELETE', `/api/v1/profile/${u}`);
}

// ---- expiring a real invite's grant ------------------------------------------------------------

// Azurite's published development account and key (docs: "Use the Azurite emulator for local Azure
// Storage development"), the storage the loop's server persists grants in.
const ACCOUNT = 'devstoreaccount1';
const KEY = 'Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==';
const TABLE_ENDPOINT = (process.env.QA_TABLE_ENDPOINT ?? `http://127.0.0.1:18473/${ACCOUNT}`).replace(/\/+$/, '');

async function table(method: string, resource: string, body?: unknown): Promise<Response> {
  const url = new URL(`${TABLE_ENDPOINT}/${resource}`);
  const date = new Date().toUTCString();
  // Shared Key Lite for the Table service: sign the date and the canonicalised resource, which for the
  // emulator's path-style endpoint is the account followed by the whole request path.
  const signature = createHmac('sha256', Buffer.from(KEY, 'base64'))
    .update(`${date}\n/${ACCOUNT}${url.pathname}`)
    .digest('base64');
  return fetch(url, {
    method,
    headers: {
      'x-ms-date': date,
      'x-ms-version': '2019-02-02',
      Authorization: `SharedKeyLite ${ACCOUNT}:${signature}`,
      Accept: 'application/json;odata=nometadata',
      'Content-Type': 'application/json',
      ...(method === 'MERGE' ? { 'If-Match': '*' } : {}),
    },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
}

/**
 * Makes the invite's persisted grant genuinely expired, by rewriting its `ExpiresAt` in the server's
 * `Grants` table to a day ago. The grant is the password_reset row the invite minted for its user; the
 * server reads `ExpiresAt` from that row on redeem, so nothing in the product is told the grant is
 * expired — it is.
 */
export async function expireGrant(userId: string): Promise<void> {
  const filter = `SubjectId eq '${userId}' and Type eq 'password_reset'`;
  const found = await table('GET', `Grants()?$filter=${encodeURIComponent(filter)}`);
  if (!found.ok) throw new Error(`Grants query: ${found.status} ${await found.text()}`);
  const rows = ((await found.json()) as { value: { PartitionKey: string; RowKey: string }[] }).value;
  if (rows.length !== 1) throw new Error(`expected one password_reset grant for ${userId}, found ${rows.length}`);
  const { PartitionKey, RowKey } = rows[0];
  const expired = new Date(Date.now() - 24 * 3600 * 1000).toISOString();
  const key = `Grants(PartitionKey='${encodeURIComponent(PartitionKey)}',RowKey='${encodeURIComponent(RowKey)}')`;
  const merged = await table('MERGE', key, { ExpiresAt: expired, 'ExpiresAt@odata.type': 'Edm.DateTime' });
  if (merged.status !== 204) throw new Error(`Grants merge: ${merged.status} ${await merged.text()}`);
}
