import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { BrowserRouter } from 'react-router';
import './styles.css';
import App from './App';
import AuthLayout from './components/AuthLayout';
import AcceptInvitePage from './pages/AcceptInvitePage';
import { loadBranding, BrandingContext } from './branding';
import './i18n';
import i18n from './i18n';

// The invite email links to the host root, not to the login app's /login/* — so the accept page is
// picked by pathname and gets a router of its own with no basename. Exactly one router mounts: App
// brings its own BrowserRouter (basename /login), and a second one would fight it over history.
const isAcceptInvite = location.pathname.replace(/\/+$/, '') === '/accept-invite';

loadBranding().then((config) => {
  document.title = isAcceptInvite
    ? i18n.t('invite.title')
    : i18n.t('signInTitle', { appName: config.appName });

  createRoot(document.getElementById('root')!).render(
    <StrictMode>
      <BrandingContext.Provider value={config}>
        {isAcceptInvite ? (
          <BrowserRouter>
            <AuthLayout>
              <AcceptInvitePage />
            </AuthLayout>
          </BrowserRouter>
        ) : (
          <App />
        )}
      </BrandingContext.Provider>
    </StrictMode>,
  );
});
