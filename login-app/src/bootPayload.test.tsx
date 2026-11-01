import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, waitFor } from '@testing-library/react';
import { MemoryRouter } from 'react-router';
import AuthLayout from './components/AuthLayout';
import { brandingDefaults, getOrganization, loadBranding, type AuthagonalBoot } from './branding';
import { getProviders } from './api';

// -------------------------------------------------------------------------------------------------
// The fixtures under ./fixtures/boot are boot payloads the library server (LoginBootPayloadBuilder
// inlined by MapAuthagonalLoginFallback) really produced for pinned, localized and tenant-only login
// pages, captured unchanged. This reads each one the way the browser does: as the
// <script type="application/json" id="authagonal-boot"> element in the document head.
// -------------------------------------------------------------------------------------------------

const fixtures = import.meta.glob('./fixtures/boot/boot-*.json', {
  query: '?raw',
  import: 'default',
  eager: true,
}) as Record<string, string>;

function install(raw: string) {
  const el = document.createElement('script');
  el.type = 'application/json';
  el.id = 'authagonal-boot';
  el.textContent = raw;
  document.head.appendChild(el);
}

afterEach(() => {
  document.head.innerHTML = '';
  document.body.innerHTML = '';
  vi.unstubAllGlobals();
});

describe('login-app on boot payloads produced by the library server', () => {
  it('has the four captured payloads', () => {
    expect(Object.keys(fixtures)).toHaveLength(4);
  });

  for (const [file, raw] of Object.entries(fixtures)) {
    const name = file.split('/').pop()!;
    const boot = JSON.parse(raw) as AuthagonalBoot;

    describe(name, () => {
      it('loadBranding returns the payload branding over the defaults without a fetch', async () => {
        install(raw);
        const fetchSpy = vi.fn(() => Promise.reject(new Error('offline')));
        vi.stubGlobal('fetch', fetchSpy);

        expect(await loadBranding()).toEqual({ ...brandingDefaults, ...boot.branding });
        expect(fetchSpy).not.toHaveBeenCalled();
      });

      it('getOrganization returns the payload organization, or null when it has none', () => {
        install(raw);
        expect(getOrganization()).toEqual(boot.organization ?? null);
      });

      it('getProviders returns the payload providers and never calls fetch', async () => {
        install(raw);
        const fetchSpy = vi.fn(() => Promise.reject(new Error('offline')));
        vi.stubGlobal('fetch', fetchSpy);

        expect(boot.providers).toBeTruthy();
        expect(await getProviders()).toEqual(boot.providers);
        expect(fetchSpy).not.toHaveBeenCalled();
      });

      it('AuthLayout shows the organisation line only when the payload carries an organisation', async () => {
        install(raw);
        vi.stubGlobal('fetch', vi.fn(() => Promise.reject(new Error('offline'))));
        const merged = { ...brandingDefaults, ...boot.branding };

        render(<MemoryRouter><AuthLayout><p>content</p></AuthLayout></MemoryRouter>);

        // the app name is text, or the logo's alt text when the branding carries a logo
        await waitFor(() => {
          const shown = (document.body.textContent ?? '').includes(merged.appName)
            || Array.from(document.querySelectorAll('img')).some((i) => i.getAttribute('alt') === merged.appName);
          expect(shown).toBe(true);
        });

        const org = boot.organization;
        if (org) {
          expect(document.body.textContent).toContain(`Signing in to ${org.name}`);
        } else {
          expect(document.body.textContent).not.toMatch(/signing in to/i);
        }
      });
    });
  }
});
