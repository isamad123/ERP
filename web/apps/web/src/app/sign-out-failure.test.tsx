// Sign-out that the server did not confirm (R-071; FR-KRN-IAM-04): the person stays signed in, is told so, and "Try again"
// then signs out and goes to sign-in. Real SessionProvider and dialog; only the network (fetch) is faked.
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router';
import { I18nRoot } from '@erp/i18n';
import { Button, ToastProvider } from '@erp/ui';
import { SessionProvider, useSessionState, useSignOut } from './session-context';

const tokens = () => new Response(JSON.stringify({ outcome: 'tokens', accessToken: 'at-1', accessTokenExpiresAt: new Date(Date.now() + 300_000).toISOString(), idleTimeoutMinutes: 30 }),
  { status: 200, headers: { 'Content-Type': 'application/json' } });

function Home() {
  const signOut = useSignOut();
  const { status } = useSessionState();
  return <main><p>status {status}</p><Button onPress={signOut}>Leave</Button></main>;
}

function setup(signOutAnswer: () => Promise<Response>) {
  vi.stubGlobal('fetch', vi.fn(async (url: string) => (url.includes('/auth/refresh') ? tokens() : signOutAnswer())));
  render(
    <MemoryRouter initialEntries={['/']}>
      <I18nRoot locale="en">
        <ToastProvider>
          <SessionProvider>
            <Routes><Route path="/" element={<Home />} /><Route path="/sign-in" element={<p>sign-in screen</p>} /></Routes>
          </SessionProvider>
        </ToastProvider>
      </I18nRoot>
    </MemoryRouter>,
  );
}

afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

describe('FR-KRN-IAM-04 R-071 sign-out failure', () => {
  it('FR-KRN-IAM-04 R-071 a network failure shows the message with "Try again" and the person stays signed in; "Try again" then signs out', async () => {
    let failing = true;
    setup(async () => { if (failing) throw new TypeError('network'); return new Response(null, { status: 204 }); });
    await screen.findByText('status signedIn');
    fireEvent.click(screen.getByRole('button', { name: 'Leave' }));
    const dialog = await screen.findByRole('alertdialog', { name: 'Sign-out did not work' });
    expect(dialog.textContent).toContain('If it keeps failing, close the browser.');
    expect(screen.queryByText('sign-in screen')).toBeNull();
    expect(screen.getByText('status signedIn')).toBeTruthy();
    failing = false;
    fireEvent.click(screen.getByRole('button', { name: 'Try again' }));
    await screen.findByText('sign-in screen');
    expect(screen.queryByRole('alertdialog')).toBeNull();
  });

  it('FR-KRN-IAM-04 R-071 a 503 busy answer is a failure too, and "Close" keeps the person signed in', async () => {
    setup(async () => new Response(JSON.stringify({ title: 'errors.auth.busy', retryAfterSeconds: 1 }), { status: 503, headers: { 'Content-Type': 'application/problem+json' } }));
    await screen.findByText('status signedIn');
    fireEvent.click(screen.getByRole('button', { name: 'Leave' }));
    await screen.findByRole('alertdialog', { name: 'Sign-out did not work' }, { timeout: 5000 });
    fireEvent.click(screen.getByRole('button', { name: 'Close' }));
    await waitFor(() => expect(screen.queryByRole('alertdialog')).toBeNull());
    expect(screen.getByText('status signedIn')).toBeTruthy();
  }, 15000);

  it('FR-KRN-IAM-04 R-071 a 204 answer goes to sign-in without any failure message', async () => {
    setup(async () => new Response(null, { status: 204 }));
    await screen.findByText('status signedIn');
    fireEvent.click(screen.getByRole('button', { name: 'Leave' }));
    await screen.findByText('sign-in screen');
    expect(screen.queryByRole('alertdialog')).toBeNull();
  });
});
