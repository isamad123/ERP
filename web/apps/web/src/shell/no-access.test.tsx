// A signed-in person whom no role gives anything (slice S1.12b; spec 13 FR-UX-STA-08; the server answers 403 on
// /api/kernel/me, SessionContextTests): the shell says what is missing and what to do, instead of a broken screen.
import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { I18nRoot } from '@erp/i18n';
import { ToastProvider } from '@erp/ui';
import { SessionProvider } from '../app/session-context';
import { ShellDataProvider } from './context';

const problem = (status: number, title: string) =>
  new Response(JSON.stringify({ status, title }), { status, headers: { 'Content-Type': 'application/problem+json', 'X-Correlation-Id': 'ref-1' } });

afterEach(() => vi.unstubAllGlobals());

describe('FR-KRN-IAM-10 a person without any permission', () => {
  it('sees "no access yet" with a way to sign out, not the shell', async () => {
    vi.stubGlobal('fetch', vi.fn(async (url: string) => (url.startsWith('/api/kernel/') ? problem(403, 'errors.access.forbidden') : problem(401, 'errors.auth.unauthorized'))));
    render(
      <MemoryRouter>
        <I18nRoot locale="en">
          <ToastProvider>
            <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
              <SessionProvider>
                <ShellDataProvider><p>shell</p></ShellDataProvider>
              </SessionProvider>
            </QueryClientProvider>
          </ToastProvider>
        </I18nRoot>
      </MemoryRouter>,
    );
    expect(await screen.findByRole('heading', { name: 'Your account has no access yet', level: 1 })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Sign out' })).toBeTruthy();
    expect(screen.queryByText('shell')).toBeNull();
  });
});
