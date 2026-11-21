// The notification bell (slice S1.12b Build item 13; FR-HRM-NTF-09; S1.12 ruling Q16): the unread count is shown on the
// bell and in its name, and refreshed every 60 seconds while the tab is visible (no live connection in 1.0).
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, render, screen } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { I18nRoot } from '@erp/i18n';
import { NotificationBell } from './TopBar';

let unread = 3;
const fetchMock = vi.fn(async () => new Response(JSON.stringify({ unread }), { status: 200, headers: { 'Content-Type': 'application/json' } }));

function setVisibility(state: 'visible' | 'hidden') {
  Object.defineProperty(document, 'visibilityState', { configurable: true, get: () => state });
  document.dispatchEvent(new Event('visibilitychange'));
}

beforeEach(() => {
  vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout', 'setInterval', 'clearInterval'] });
  vi.stubGlobal('fetch', fetchMock);
  fetchMock.mockClear();
  unread = 3;
  setVisibility('visible');
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.useRealTimers();
  setVisibility('visible');
});

function renderBell() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={client}>
      <I18nRoot locale="en"><NotificationBell /></I18nRoot>
    </QueryClientProvider>,
  );
}

describe('FR-HRM-NTF-09 notification bell', () => {
  it('names the unread count and refreshes it every 60 seconds while the tab is visible', async () => {
    renderBell();
    await act(async () => { await vi.advanceTimersByTimeAsync(10); });
    expect(screen.getByRole('button', { name: 'Notifications, 3 unread' })).toBeTruthy();
    expect(fetchMock).toHaveBeenCalledTimes(1);

    unread = 5;
    await act(async () => { await vi.advanceTimersByTimeAsync(59_000); });
    expect(fetchMock).toHaveBeenCalledTimes(1);
    await act(async () => { await vi.advanceTimersByTimeAsync(1_100); });
    expect(fetchMock).toHaveBeenCalledTimes(2);
    expect(screen.getByRole('button', { name: 'Notifications, 5 unread' })).toBeTruthy();

    setVisibility('hidden');
    await act(async () => { await vi.advanceTimersByTimeAsync(180_000); });
    expect(fetchMock).toHaveBeenCalledTimes(2); // nothing while the tab is hidden
  });

  it('says only "Notifications" when nothing is unread', async () => {
    unread = 0;
    renderBell();
    await act(async () => { await vi.advanceTimersByTimeAsync(10); });
    expect(screen.getByRole('button', { name: 'Notifications' })).toBeTruthy();
  });
});
