import { useState, useEffect } from 'react';
import { useSearchParams } from 'react-router';
import { useTranslation } from 'react-i18next';
import { resetPassword, getProviders, ApiRequestError } from '../api';
import { getOrganization } from '../branding';
import type { PasswordPolicyRule } from '../types';
import { localizePasswordRules, evaluatePasswordRules } from '../lib/passwordRules';
import { Turnstile } from '../components/Turnstile';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Alert } from '@/components/ui/alert';
import { CardTitle, CardFooter } from '@/components/ui/card';
import { Check, X } from 'lucide-react';

const API_URL = import.meta.env.VITE_API_URL || '';

const defaultRules: PasswordPolicyRule[] = [
  { rule: 'minLength', value: 8, label: 'At least 8 characters' },
  { rule: 'uppercase', value: null, label: 'Uppercase letter' },
  { rule: 'lowercase', value: null, label: 'Lowercase letter' },
  { rule: 'digit', value: null, label: 'Number' },
  { rule: 'specialChar', value: null, label: 'Special character' },
];

/** The shape of an organisation identifier (Organization.cs `OrganizationIdentifier`). */
const ORGANIZATION_IDENTIFIER = /^[A-Za-z0-9._~-]{1,200}$/;

/**
 * What to call the organisation an invite link names, or null for an invite that names none.
 *
 * The link carries the organisation's slug (`?organization=<slug>`) and nothing else, and the library
 * has no anonymous endpoint that turns a slug into a display name — the admin API that does is
 * `IdentityAdmin`-only, and a visitor holding an invite link has no credential. So the name comes from
 * the host's boot payload when the host inlined this very organisation there (see `getOrganization`),
 * and otherwise the page names the organisation by its slug.
 */
function organizationLabel(slug: string): string | null {
  // Only a value of the organisation identifier's shape is shown; anything else on the query string is
  // arbitrary text on the auth host's own origin, so the page falls back to the generic invite copy.
  if (!ORGANIZATION_IDENTIFIER.test(slug)) return null;
  const inlined = getOrganization();
  if (inlined?.name && (inlined.slug === slug || inlined.id === slug)) return inlined.name;
  return slug;
}

/**
 * The page an invite email links to. Accepting an invite is, mechanically, redeeming the same
 * single-use grant a password reset uses (`POST /api/auth/reset-password`) — but the user is setting a
 * password for the first time, so the copy is first-time setup, never "reset what was never set".
 *
 * It lives at the host root (`/accept-invite`), not under the login app's `/login` basename, and reads
 * the token from the same `p` parameter the reset link uses. A host that serves the page elsewhere
 * mounts this component inside its own router.
 */
export default function AcceptInvitePage() {
  const { t } = useTranslation();
  const [searchParams] = useSearchParams();
  const token = searchParams.get('p') || '';
  const organization = organizationLabel(searchParams.get('organization') || '');

  const [newPassword, setNewPassword] = useState('');
  const [confirmPassword, setConfirmPassword] = useState('');
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState('');
  const [validationError, setValidationError] = useState('');
  const [success, setSuccess] = useState(false);
  // "Continue to {app}" target from the redeem response; null keeps the plain sign-in link.
  const [appLink, setAppLink] = useState<{ clientName: string; homeUri: string } | null>(null);
  const [rules, setRules] = useState<PasswordPolicyRule[]>(defaultRules);
  const [turnstileSiteKey, setTurnstileSiteKey] = useState<string | undefined>(undefined);
  const [turnstileToken, setTurnstileToken] = useState<string | null>(null);
  const [turnstileKey, setTurnstileKey] = useState(0); // bump to re-mount the widget for a fresh challenge

  useEffect(() => {
    fetch(`${API_URL}/api/auth/password-policy`)
      .then((r) => (r.ok ? r.json() : null))
      .then((data) => { if (data?.rules) setRules(data.rules); })
      .catch(() => { /* use defaults */ });
  }, []);

  useEffect(() => {
    getProviders()
      .then((res) => setTurnstileSiteKey(res.turnstileSiteKey))
      .catch(() => {});
  }, []);

  const requirements = evaluatePasswordRules(newPassword, localizePasswordRules(t, rules));
  const allRequirementsMet = requirements.every((r) => r.met);

  async function handleSubmit(e: React.FormEvent) {
    e.preventDefault();
    setError('');
    setValidationError('');

    if (!allRequirementsMet) {
      setValidationError(t('passwordNotMeetRequirements'));
      return;
    }
    if (newPassword !== confirmPassword) {
      setValidationError(t('passwordsDoNotMatch'));
      return;
    }

    setLoading(true);
    try {
      const result = await resetPassword(token, newPassword, turnstileToken || undefined);
      if (result.appLink?.homeUri) setAppLink({ clientName: result.appLink.clientName, homeUri: result.appLink.homeUri });
      setSuccess(true);
    } catch (err) {
      if (err instanceof ApiRequestError) {
        switch (err.error) {
          case 'weak_password':
            setError(err.message || t('passwordWeakError'));
            break;
          case 'invalid_token':
          case 'token_expired':
            setError(t('invite.invalidMessage'));
            break;
          case 'password_required':
            setError(t('errorPasswordRequired'));
            break;
          case 'captcha_failed':
            setError(t('captchaFailed'));
            break;
          default:
            setError(err.message || t('errorUnexpected'));
        }
      } else {
        setError(t('errorUnexpected'));
      }
      // Turnstile tokens are single-use — reset so a retry gets a fresh challenge.
      if (turnstileSiteKey) {
        setTurnstileToken(null);
        setTurnstileKey((k) => k + 1);
      }
    } finally {
      setLoading(false);
    }
  }

  if (success) {
    return (
      <div>
        <CardTitle>{t('invite.successTitle')}</CardTitle>
        <Alert variant="success">{t('invite.successMessage')}</Alert>
        {appLink && (
          <a href={appLink.homeUri} data-testid="continue-to-app" className="mt-4 block no-underline">
            <Button className="w-full">{t('continueToApp', { app: appLink.clientName })}</Button>
          </a>
        )}
        <CardFooter>
          {/* A full navigation, not a router Link: this page renders at the host root, outside the
              login app's /login basename, so a client-side hop to "/" would not reach the sign-in page. */}
          <a href="/login" className="text-sm font-medium text-primary hover:underline no-underline">
            {t('signIn')}
          </a>
        </CardFooter>
      </div>
    );
  }

  if (!token) {
    return (
      <div>
        <CardTitle>{t('invite.invalidTitle')}</CardTitle>
        <Alert variant="error">{t('invite.invalidMessage')}</Alert>
      </div>
    );
  }

  return (
    <div>
      <CardTitle>
        {organization ? t('invite.titleOrganization', { organization }) : t('invite.title')}
      </CardTitle>
      <p className="mb-4 text-sm text-gray-500 dark:text-gray-400">
        {organization ? t('invite.introOrganization', { organization }) : t('invite.intro')}
      </p>

      {error && <Alert variant="error">{error}</Alert>}
      {validationError && <Alert variant="error">{validationError}</Alert>}

      <form onSubmit={handleSubmit}>
        <div className="mb-4">
          <Label htmlFor="newPassword">{t('newPassword')}</Label>
          <Input
            id="newPassword"
            type="password"
            value={newPassword}
            onChange={(e) => setNewPassword(e.target.value)}
            placeholder={t('newPasswordPlaceholder')}
            autoComplete="new-password"
            autoFocus
            maxLength={256}
            required
          />
        </div>

        {newPassword.length > 0 && (
          <ul className="list-none mb-4 p-3 bg-gray-50 dark:bg-gray-800/60 rounded-md">
            {requirements.map((req) => (
              <li key={req.label} data-met={req.met} className={`text-[13px] py-0.5 flex items-center gap-1.5 ${req.met ? 'text-green-800 dark:text-green-400' : 'text-red-800 dark:text-red-400'}`}>
                {req.met ? <Check className="h-3.5 w-3.5 shrink-0" /> : <X className="h-3.5 w-3.5 shrink-0" />}
                {req.label}
              </li>
            ))}
          </ul>
        )}

        <div className="mb-4">
          <Label htmlFor="confirmPassword">{t('confirmPassword')}</Label>
          <Input
            id="confirmPassword"
            type="password"
            value={confirmPassword}
            onChange={(e) => setConfirmPassword(e.target.value)}
            placeholder={t('confirmPasswordPlaceholder')}
            autoComplete="new-password"
            maxLength={256}
            required
          />
        </div>

        {turnstileSiteKey && (
          <div className="mb-4">
            <Turnstile key={turnstileKey} siteKey={turnstileSiteKey} onToken={setTurnstileToken} />
          </div>
        )}

        <Button type="submit" loading={loading} disabled={!allRequirementsMet || (!!turnstileSiteKey && !turnstileToken)}>
          {loading ? t('invite.creating') : t('invite.button')}
        </Button>
      </form>
    </div>
  );
}
