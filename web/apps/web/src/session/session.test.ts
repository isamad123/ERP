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
  /** What the sign-out request does: answer (204), or fail with an error carrying this status (0 = network). */
  signOutFails: { status: number } | null = null;
  refreshes = 0;
  idleMinutes = 30;
  /** While set, a refresh waits on its way to the server (it carries the cookie of the moment it left). */
  gate: Promise<void> | null = null;

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
      refresh: async () => {
        const sent = this.cookie;
        if (this.gate) await this.gate;
        return this.serverRefresh(sent);
      },
      signOut: async () => {
        if (this.signOutFails) throw Object.assign(new Error('sign-out'), { status: this.signOutFails.status });
        this.ended = true;
      },
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
    expect(a!.manager.getSnapshot()).toEqual({ status: 'signedIn', warningRemainingMs: null, signOutFailed: false });
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

  describe('FR-KRN-IAM-04 R-071 sign-out that the server did not confirm', () => {
    for (const [name, status] of [['a network failure', 0], ['503 busy after the retry', 503], ['a 500 answer', 500]] as const) {
      it(`FR-KRN-IAM-04 R-071 ${name} keeps the person signed in, in every tab`, async () => {
        const world = new FakeWorld();
        const [a, b] = await signedInTabs(world, 2);
        world.signOutFails = { status };
        expect(await a!.manager.signOut()).toBe('failed');
        await flush();
        expect(world.ended).toBe(false);
        expect(a!.manager.getSnapshot()).toMatchObject({ status: 'signedIn', signOutFailed: true });
        expect(a!.manager.accessToken()).not.toBeNull();
        expect(b!.manager.getSnapshot()).toMatchObject({ status: 'signedIn', signOutFailed: false });
        expect(b!.manager.accessToken()).not.toBeNull();
      });
    }

    it('FR-KRN-IAM-04 R-071 a 204 answer ends the session in every tab', async () => {
      const world = new FakeWorld();
      const [a, b] = await signedInTabs(world, 2);
      expect(await a!.manager.signOut()).toBe('ended');
      await flush();
      expect(world.ended).toBe(true);
      expect(a!.manager.getSnapshot().status).toBe('signedOut');
      expect(b!.manager.getSnapshot().status).toBe('signedOut');
    });

    it('FR-KRN-IAM-04 R-071 a 401 answer (the session had already ended at the server) ends the session in every tab', async () => {
      const world = new FakeWorld();
      const [a, b] = await signedInTabs(world, 2);
      world.signOutFails = { status: 401 };
      expect(await a!.manager.signOut()).toBe('ended');
      await flush();
      expect(a!.manager.getSnapshot().status).toBe('signedOut');
      expect(b!.manager.getSnapshot().status).toBe('signedOut');
      expect(b!.manager.accessToken()).toBeNull();
    });

    it('FR-KRN-IAM-04 R-071 trying again after a failure signs out and clears the message; closing the message keeps the session', async () => {
      const world = new FakeWorld();
      const [a, b] = await signedInTabs(world, 2);
      world.signOutFails = { status: 0 };
      await a!.manager.signOut();
      a!.manager.dismissSignOutFailure();
      expect(a!.manager.getSnapshot()).toMatchObject({ status: 'signedIn', signOutFailed: false });
      await a!.manager.signOut();
      expect(a!.manager.getSnapshot().signOutFailed).toBe(true);
      world.signOutFails = null;
      expect(await a!.manager.signOut()).toBe('ended');
      await flush();
      expect(world.ended).toBe(true);
      expect(a!.manager.getSnapshot()).toMatchObject({ status: 'signedOut', signOutFailed: false });
      expect(b!.manager.getSnapshot().status).toBe('signedOut');
    });
  });

  it('a background request that meets a 401 does not keep an idle session alive (review S1.12a finding 2)', async () => {
    // A poll (S1.12b: the notification bell every 60 seconds while the tab is visible) runs without the person; when its
    // access token has expired the API client asks for one refresh. Without activity that refresh must not happen,
    // or the server's idle rule (counted from the last refresh) would never end a forgotten session.
    const world = new FakeWorld();
    const [a] = await signedInTabs(world, 1);
    const before = world.refreshes;
    const answers: boolean[] = [];
    await world.advance(31 * MINUTE, (elapsed) => {
      if (elapsed % MINUTE === 0 && elapsed > 5 * MINUTE) {
        void a!.manager.refreshAfterUnauthorized().then((ok) => {
          answers.push(ok);
          if (!ok) a!.manager.sessionEnded(); // what the client does when the refresh did not help
        });
      }
    });
    expect(world.refreshes).toBe(before);
    expect(answers.length).toBeGreaterThan(20);
    expect(answers.every((ok) => !ok)).toBe(true);
    expect(a!.manager.getSnapshot().status).toBe('expired'); // at the idle time, not at the first background 401
  });

  it('a request after the person\'s activity still gets its one refresh after a 401', async () => {
    const world = new FakeWorld();
    const [a] = await signedInTabs(world, 1);
    await world.advance(6 * MINUTE);
    const before = world.refreshes;
    a!.activity();
    expect(await a!.manager.refreshAfterUnauthorized()).toBe(true);
    expect(world.refreshes).toBe(before + 1);
    expect(a!.manager.getSnapshot().status).toBe('signedIn');
  });

  it('an idle tab is not signed out early by a background 401', async () => {
    const world = new FakeWorld();
    const [a] = await signedInTabs(world, 1);
    await world.advance(10 * MINUTE);
    expect(await a!.manager.refreshAfterUnauthorized()).toBe(false);
    a!.manager.sessionEnded();
    expect(a!.manager.getSnapshot().status).toBe('signedIn'); // the warning and the idle end still come at their time
    await world.advance(18 * MINUTE + 1000);
    expect(a!.manager.getSnapshot().warningRemainingMs).not.toBeNull();
    expect(await a!.manager.staySignedIn()).toBe(true); // the server session is still alive
  });

  it('a sign-in that finishes while the page\'s first check is still on its way stays signed in (S1.12b finding)', async () => {
    const world = new FakeWorld();
    world.cookie = 'none'; // no session yet: the first check will be refused
    let open: () => void = () => undefined;
    world.gate = new Promise((resolve) => { open = resolve; });
    const tab = world.tab();
    const resumed = tab.manager.resume(0);
    await world.advance(1000); // the check is on its way
    world.cookie = 'rt-1'; // the activation (or sign-in) answers with a new session
    world.gate = null;
    tab.manager.signedIn(world.serverRefresh(world.cookie));
    open();
    await world.advance(1000);
    await resumed;
    expect(tab.manager.getSnapshot().status).toBe('signedIn');
    expect(world.ended).toBe(false);
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
