import { describe, expect, it, vi, beforeEach } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter } from 'react-router';
import AcceptInvitePage from './AcceptInvitePage';
import { resetPassword } from '../api';

// The page redeems the invite token through the reset-password API and says what happened. These pin
// the states a visitor can land in; the real redeem against a server is the browser suite in e2e/.

vi.mock('../api', () => ({
  resetPassword: vi.fn(),
  getProviders: vi.fn().mockResolvedValue({}),
  // Same constructor shape as the real class: it takes the server's { error, message } body.
  ApiRequestError: class ApiRequestError extends Error {
    error: string;
    constructor(apiError: { error: string; message?: string }) {
      super(apiError.message || apiError.error);
      this.error = apiError.error;
    }
  },
}));

vi.mock('react-i18next', () => ({
  useTranslation: () => ({
    t: (key: string, vars?: Record<string, unknown>) => (vars?.organization ? `${key}:${vars.organization}` : key),
    i18n: { language: 'en' },
  }),
}));

async function renderAt(search: string) {
  render(
    <MemoryRouter initialEntries={[`/accept-invite${search}`]}>
      <AcceptInvitePage />
    </MemoryRouter>,
  );
  // Let the password-policy fetch settle so no state update lands after the test.
  await waitFor(() => expect(fetch).toHaveBeenCalled());
}

const password = () => document.getElementById('newPassword') as HTMLInputElement;
const confirmation = () => document.getElementById('confirmPassword') as HTMLInputElement;

beforeEach(() => {
  vi.stubGlobal('fetch', vi.fn().mockResolvedValue({ ok: false }));
  vi.mocked(resetPassword).mockReset();
});

describe('AcceptInvitePage', () => {
  it('shows the invalid-link state and no form when the link has no token', async () => {
    await renderAt('');
    expect(screen.getByText('invite.invalidTitle')).toBeTruthy();
    expect(document.querySelector('form')).toBeNull();
  });

  it('names the organisation the link names, by its slug when nothing supplies a display name', async () => {
    await renderAt('?p=tok&organization=acme');
    expect(screen.getByText('invite.titleOrganization:acme')).toBeTruthy();
  });

  it('is generic, and renders none of the text, when the organization value is not a slug', async () => {
    const text = 'PayPal Security. Your account is locked; call 1-800-555-0100';
    await renderAt(`?p=tok&organization=${encodeURIComponent(text)}`);
    expect(screen.getByText('invite.title')).toBeTruthy();
    expect(screen.getByText('invite.intro')).toBeTruthy();
    expect(document.body.textContent).not.toContain('PayPal');
  });

  it('is generic when the link names no organisation', async () => {
    await renderAt('?p=tok');
    expect(screen.getByText('invite.title')).toBeTruthy();
  });

  it('refuses mismatched passwords without calling the API', async () => {
    await renderAt('?p=tok');
    fireEvent.change(password(), { target: { value: 'Passw0rd!x1' } });
    fireEvent.change(confirmation(), { target: { value: 'Passw0rd!x2' } });
    fireEvent.submit(document.querySelector('form')!);
    expect(screen.getByText('passwordsDoNotMatch')).toBeTruthy();
    expect(resetPassword).not.toHaveBeenCalled();
  });

  it('redeems the token from the p parameter and shows the success state', async () => {
    vi.mocked(resetPassword).mockResolvedValue({ success: true });
    await renderAt('?p=tok');
    fireEvent.change(password(), { target: { value: 'Passw0rd!x1' } });
    fireEvent.change(confirmation(), { target: { value: 'Passw0rd!x1' } });
    fireEvent.submit(document.querySelector('form')!);
    await screen.findByText('invite.successTitle');
    expect(resetPassword).toHaveBeenCalledWith('tok', 'Passw0rd!x1', undefined);
  });

  it('shows the invalid-or-expired message when the server refuses the token', async () => {
    const { ApiRequestError } = await import('../api');
    vi.mocked(resetPassword).mockRejectedValue(new ApiRequestError({ error: 'token_expired' }));
    await renderAt('?p=tok');
    fireEvent.change(password(), { target: { value: 'Passw0rd!x1' } });
    fireEvent.change(confirmation(), { target: { value: 'Passw0rd!x1' } });
    fireEvent.submit(document.querySelector('form')!);
    await screen.findByText('invite.invalidMessage');
  });

  it("shows the server's own message for weak_password", async () => {
    const { ApiRequestError } = await import('../api');
    vi.mocked(resetPassword).mockRejectedValue(new ApiRequestError({ error: 'weak_password', message: 'Password is too common.' }));
    await renderAt('?p=tok');
    fireEvent.change(password(), { target: { value: 'Passw0rd!x1' } });
    fireEvent.change(confirmation(), { target: { value: 'Passw0rd!x1' } });
    fireEvent.submit(document.querySelector('form')!);
    await screen.findByText('Password is too common.');
  });
});
