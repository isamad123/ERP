// Session manager (slice S1.12a, Build item 6; FR-KRN-IAM-04, ruling Q3). Two tabs share one fake server whose refresh
// token rotates, and a reused token ends the session exactly as the API does (S1.05 ruling P6). Time is a fake clock.
import { describe, expect, it } from 'vitest';
import type { AuthAnswer } from '../api/identity';
import { SessionManager, WARNING_BEFORE_MS, type SessionEnvironment, type SessionMessage } from './session';

const MINUTE = 60_000;

class FakeWorld {
  now = Date.UTC(2026, 10, 20, 9, 0, 0);
  private timers: { id: number; at: number; run: () => void }[] = [];
  private nextId = 1;
  private tabs: ((m: SessionMessage) => void)[][] = [];
  private lockQueue: Promise<unknown> = Promise.resolve();
  /** The server's session: current cookie value and the time of the last refresh (last_seen_at). */
  cookie = 'rt-1';
  previous: string | null = null;
  lastSeen = this.now;
  ended = false;
  refreshes = 0;
  idleMinutes = 30;

  serverRefresh(cookie: string): AuthAnswer {
    if (this.ended || this.now - this.lastSeen >= this.idleMinutes * MINUTE) {
      this.ended = true;
      throw new Error('401');
    }
    if (cookie !== this.cookie) {
      if (cookie === this.previous) this.ended = true; // reused rotated token ends the session (P6)
      throw new Error('401');
    }
    this.refreshes += 1;
    this.previous = this.cookie;
    this.cookie = `rt-${this.refreshes + 1}`;
    this.lastSeen = this.now;
    return { outcome: 'tokens', accessToken: `at-${this.refreshes}`, accessTokenExpiresAt: new Date(this.now + 5 * MINUTE).toISOString(), idleTimeoutMinutes: this.idleMinutes };
  }

  /** A tab; `skewMs` makes its clock read ahead (or behind) of the others. */
  tab(skewMs = 0): { manager: SessionManager; activity: () => void } {
    const listeners: ((m: SessionMessage) => void)[] = [];
    this.tabs.push(listeners);
    const activityHandlers: (() => void)[] = [];
    const env: SessionEnvironment = {
      clock: { now: () => this.now + skewMs },
      // Every tab reads the browser's one cookie jar at the moment of the request.
      refresh: async () => this.serverRefresh(this.cookie),
      signOut: async () => { this.ended = true; },
      channel: {
        post: (m) => this.tabs.filter((t) => t !== listeners).forEach((t) => t.forEach((h) => queueMicrotask(() => h(structuredClone(m))))),
        listen: (h) => { listeners.push(h); return () => listeners.splice(listeners.indexOf(h), 1); },
      },
      lock: (_name, task) => {
        const run = this.lockQueue.then(task, task);
        this.lockQueue = run.catch(() => undefined);
        return run;
      },
      setTimer: (run, ms) => { const id = this.nextId++; this.timers.push({ id, at: this.now + ms, run }); return id; },
      clearTimer: (id) => { this.timers = this.timers.filter((t) => t.id !== id); },
      activityTarget: { addEventListener: (_e: string, h: EventListenerOrEventListenerObject) => activityHandlers.push(h as () => void), removeEventListener: () => undefined },
    };
    return { manager: new SessionManager(env), activity: () => activityHandlers.forEach((h) => h()) };
  }

  /** Moves time forward second by second, running timers and pending promises. */
  async advance(ms: number, onSecond?: (elapsed: number) => void): Promise<void> {
    const end = this.now + ms;
    while (this.now < end) {
      this.now = Math.min(end, this.now + 1000);
      onSecond?.(ms - (end - this.now));
      for (;;) {
        const due = this.timers.filter((t) => t.at <= this.now).sort((a, b) => a.at - b.at)[0];
        if (!due) break;
        this.timers = this.timers.filter((t) => t !== due);
        due.run();
        await flush();
      }
      await flush();
    }
  }
}

const flush = async () => { for (let i = 0; i < 20; i += 1) await Promise.resolve(); };

async function signedInTabs(world: FakeWorld, count: number, skews: number[] = []) {
  const tabs = Array.from({ length: count }, (_, i) => world.tab(skews[i] ?? 0));
  const first = tabs[0]!;
  const resumes = tabs.map((t) => t.manager.resume(0));
  await world.advance(1000);
  await Promise.all(resumes);
  first.manager.signedIn(world.serverRefresh(world.cookie));
  await flush();
  return tabs;
}

describe('FR-KRN-IAM-04 session in the browser (S1.12a)', () => {
  it('two tabs stay signed in for 30 minutes of activity without a reused-token sign-out', async () => {
    const world = new FakeWorld();
    const [a, b] = await signedInTabs(world, 2);
    await world.advance(30 * MINUTE + 5 * MINUTE, (elapsed) => {
      if (elapsed % 20_000 === 0) (elapsed % 40_000 === 0 ? a! : b!).activity();
    });
    expect(world.ended).toBe(false);
    expect(a!.manager.getSnapshot().status).toBe('signedIn');
    expect(b!.manager.getSnapshot().status).toBe('signedIn');
    expect(a!.manager.accessToken()).toBe(b!.manager.accessToken());
    expect(world.refreshes).toBeGreaterThan(7); // about every 4 minutes, one tab at a time
  });

  it('tabs whose clocks disagree still share the newest token and the idle time', async () => {
    const world = new FakeWorld();
    const [a, b] = await signedInTabs(world, 2, [20_000, -15_000]);
    await world.advance(30 * MINUTE + 5 * MINUTE, (elapsed) => {
      if (elapsed % 20_000 === 0) (elapsed % 40_000 === 0 ? a! : b!).activity();
    });
    expect(world.ended).toBe(false);
    expect(a!.manager.accessToken()).toBe(b!.manager.accessToken());
    const before = world.refreshes;
    await world.advance(26 * MINUTE); // 30 minutes after the last refresh have not passed yet
    expect(a!.manager.getSnapshot().status).toBe('signedIn');
    expect(b!.manager.getSnapshot().status).toBe('signedIn');
    await world.advance(10 * MINUTE); // the last activity's refresh can come up to one token life into the quiet time
    expect(a!.manager.getSnapshot().status).toBe('expired');
    expect(b!.manager.getSnapshot().status).toBe('expired');
    expect(world.refreshes - before).toBeLessThanOrEqual(1); // at most the one refresh earned by the last activity
  });

  it('two tabs refreshing at the same moment do not end the session', async () => {
    const world = new FakeWorld();
    const [a, b] = await signedInTabs(world, 2);
    const both = await Promise.all([a!.manager.staySignedIn(), b!.manager.staySignedIn()]);
    await flush();
    expect(both).toEqual([true, true]);
    expect(world.ended).toBe(false);
    expect(a!.manager.accessToken()).toBe(b!.manager.accessToken());
  });

  it('without activity the warning appears two minutes before the idle end and the session ends at the idle time', async () => {
    const world = new FakeWorld();
    const [a] = await signedInTabs(world, 1);
    const before = world.refreshes;
    await world.advance(28 * MINUTE - 2000);
    expect(a!.manager.getSnapshot().warningRemainingMs).toBeNull();
    await world.advance(3000);
    const warning = a!.manager.getSnapshot().warningRemainingMs;
    expect(warning).not.toBeNull();
    expect(warning!).toBeLessThanOrEqual(WARNING_BEFORE_MS);
    await world.advance(2 * MINUTE);
    expect(a!.manager.getSnapshot().status).toBe('expired');
    expect(world.refreshes).toBe(before); // no refresh without activity: the server's idle rule still ends forgotten sessions
  });

  it('"Stay signed in" refreshes and hides the warning', async () => {
    const world = new FakeWorld();
    const [a] = await signedInTabs(world, 1);
    await world.advance(29 * MINUTE);
    expect(a!.manager.getSnapshot().warningRemainingMs).not.toBeNull();
    expect(await a!.manager.staySignedIn()).toBe(true);
    expect(a!.manager.getSnapshot()).toEqual({ status: 'signedIn', warningRemainingMs: null });
    await world.advance(10 * MINUTE);
    expect(a!.manager.getSnapshot().status).toBe('signedIn');
  });

  it('a new tab takes the token from an open tab instead of refreshing', async () => {
    const world = new FakeWorld();
    await signedInTabs(world, 1);
    const before = world.refreshes;
    const late = world.tab();
    const resumed = late.manager.resume(250);
    await world.advance(1000);
    expect(await resumed).toBe('signedIn');
    expect(world.refreshes).toBe(before);
  });

  it('sign-out ends the session on the server and in every tab', async () => {
    const world = new FakeWorld();
    const [a, b] = await signedInTabs(world, 2);
    await a!.manager.signOut();
    await flush();
    expect(world.ended).toBe(true);
    expect(a!.manager.getSnapshot().status).toBe('signedOut');
    expect(b!.manager.getSnapshot().status).toBe('signedOut');
    expect(b!.manager.accessToken()).toBeNull();
  });

  it('a tab without a live session ends signed out', async () => {
    const world = new FakeWorld();
    world.ended = true;
    const tab = world.tab();
    const resumed = tab.manager.resume(0);
    await world.advance(1000);
    expect(await resumed).toBe('signedOut');
  });
});
