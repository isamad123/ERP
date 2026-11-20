// Session handling in the browser (slice S1.12a, Build item 6, ruling Q3; FR-KRN-IAM-04, spec 13 FR-UX-SEC-04).
// - The access token (5 minutes) is kept in memory only; the refresh token is the HttpOnly cookie (Identity API v3).
// - One tab refreshes at a time (a Web Lock) and shares the new access token with the other tabs (BroadcastChannel),
//   because a second refresh with an already-rotated token would end the session (S1.05 review, rotation reuse).
// - A refresh happens only after activity (pointer, keyboard, touch) in some tab since the last refresh, so the server's
//   idle timeout, counted from the last refresh, still ends forgotten sessions.
// - Two minutes before the idle end with no activity, the warning offers "Stay signed in"; at the idle end the person
//   is sent to sign-in and comes back to the same screen afterwards.
// - Sign-out ends the session on the server and in every tab.
import type { AuthAnswer } from '../api/identity';
import { systemClock, type Clock } from './clock';

export type SessionStatus = 'unknown' | 'signedIn' | 'signedOut' | 'expired';

export interface SessionSnapshot {
  status: SessionStatus;
  /** Milliseconds left before the idle end while the warning is shown, else null. */
  warningRemainingMs: number | null;
}

/** What the manager needs from the browser and the API; the tests give fakes. */
export interface SessionEnvironment {
  clock: Clock;
  refresh(): Promise<AuthAnswer>;
  signOut(accessToken: string): Promise<void>;
  channel: { post(message: SessionMessage): void; listen(handler: (message: SessionMessage) => void): () => void } | null;
  lock<T>(name: string, task: () => Promise<T>): Promise<T>;
  setTimer(handler: () => void, ms: number): number;
  clearTimer(id: number): void;
  activityTarget: Pick<EventTarget, 'addEventListener' | 'removeEventListener'> | null;
}

export type SessionMessage =
  // Times between tabs are ages (milliseconds ago or left), never one tab's clock reading; tokens are ordered by the
  // server's own expiry time, so a newer token always wins whatever each tab's clock says.
  | { type: 'token'; accessToken: string; serverExpiresAt: number; lifeLeftMs: number; idleMinutes: number; refreshedAgoMs: number }
  | { type: 'activity'; at: number }
  | { type: 'hello' }
  | { type: 'ended'; reason: 'signedOut' | 'expired' };

export const WARNING_BEFORE_MS = 2 * 60 * 1000;
/** Refresh this long before the access token expires (when there was activity). */
export const REFRESH_AHEAD_MS = 60 * 1000;
/** The access token's lifetime (S1.05 ruling Q3). */
export const ACCESS_LIFETIME_MS = 5 * 60 * 1000;
const TICK_MS = 1000;
const ACTIVITY_SHARE_MS = 15 * 1000;
const LOCK_NAME = 'erp-session-refresh';
const ACTIVITY_EVENTS = ['pointerdown', 'keydown', 'touchstart', 'wheel'] as const;

export class SessionManager {
  private access: string | null = null;
  private expiresAt = 0;
  private serverExpiresAt = 0;
  private idleMinutes = 30;
  private refreshedAt = 0;
  private lastActivity = 0;
  private lastShared = 0;
  private status: SessionStatus = 'unknown';
  private warningRemainingMs: number | null = null;
  private timer: number | null = null;
  private refreshing: Promise<boolean> | null = null;
  private readonly listeners = new Set<() => void>();
  private snapshot: SessionSnapshot = { status: 'unknown', warningRemainingMs: null };
  private unlisten: (() => void) | null = null;

  constructor(private readonly env: SessionEnvironment) {}

  /** Joins the other tabs: takes their token if one answers, else tries the cookie once (a new tab or a reload). */
  async resume(waitForTabsMs = 250): Promise<SessionStatus> {
    this.unlisten = this.env.channel?.listen((m) => this.onMessage(m)) ?? null;
    const target = this.env.activityTarget;
    if (target) ACTIVITY_EVENTS.forEach((e) => target.addEventListener(e, this.onActivity, { passive: true, capture: true }));
    this.lastActivity = this.env.clock.now(); // opening or reloading the page is activity
    if (this.env.channel) {
      this.env.channel.post({ type: 'hello' });
      await new Promise((resolve) => this.env.setTimer(() => resolve(undefined), waitForTabsMs));
    }
    if (this.access === null) {
      const ok = await this.refreshNow();
      if (!ok) this.setStatus('signedOut');
    }
    return this.status;
  }

  dispose(): void {
    this.unlisten?.();
    const target = this.env.activityTarget;
    if (target) ACTIVITY_EVENTS.forEach((e) => target.removeEventListener(e, this.onActivity, { capture: true }));
    if (this.timer !== null) this.env.clearTimer(this.timer);
  }

  /** After a sign-in step answered `tokens`. */
  signedIn(answer: AuthAnswer): void {
    this.accept(answer);
    this.share();
  }

  accessToken(): string | null {
    return this.access;
  }

  getSnapshot = (): SessionSnapshot => this.snapshot;

  subscribe = (listener: () => void): (() => void) => {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  };

  /** "Stay signed in": counts as activity and refreshes at once. */
  async staySignedIn(): Promise<boolean> {
    this.lastActivity = this.env.clock.now();
    return this.refreshNow();
  }

  /** Ends the session on the server and in every tab; the local state ends even if the server cannot be reached. */
  async signOut(): Promise<void> {
    const token = this.access;
    try {
      if (token) await this.env.signOut(token);
    } finally {
      this.end('signedOut', true);
    }
  }

  /** For the API client after a 401: one refresh (shared with any refresh already running). */
  async refreshAfterUnauthorized(): Promise<boolean> {
    return this.refreshNow();
  }

  /** For the API client when a refresh did not help. */
  sessionEnded(): void {
    if (this.status === 'signedIn') this.end('expired', true);
  }

  private onActivity = (): void => {
    const now = this.env.clock.now();
    this.lastActivity = now;
    if (this.warningRemainingMs !== null) return; // the warning needs an explicit "Stay signed in"
    if (now - this.lastShared >= ACTIVITY_SHARE_MS) {
      this.lastShared = now;
      this.env.channel?.post({ type: 'activity', at: now });
    }
  };

  private onMessage(message: SessionMessage): void {
    switch (message.type) {
      case 'token':
        if (message.serverExpiresAt >= this.serverExpiresAt) {
          const now = this.env.clock.now();
          this.access = message.accessToken;
          this.serverExpiresAt = message.serverExpiresAt;
          this.expiresAt = now + message.lifeLeftMs;
          this.idleMinutes = message.idleMinutes;
          this.refreshedAt = Math.max(this.refreshedAt, now - message.refreshedAgoMs);
          this.lastActivity = Math.max(this.lastActivity, this.refreshedAt);
          this.warningRemainingMs = null;
          this.setStatus('signedIn');
          this.schedule();
        }
        break;
      case 'activity':
        // Activity in another tab just now (this tab's own clock; the sender's time is not used).
        if (this.warningRemainingMs === null) this.lastActivity = Math.max(this.lastActivity, this.env.clock.now());
        break;
      case 'hello':
        if (this.access !== null && this.status === 'signedIn') this.share();
        break;
      case 'ended':
        this.end(message.reason, false);
        break;
    }
  }

  private accept(answer: AuthAnswer): void {
    if (answer.outcome !== 'tokens' || !answer.accessToken || !answer.accessTokenExpiresAt) throw new Error('not a token answer');
    const now = this.env.clock.now();
    this.access = answer.accessToken;
    // The server's clock may differ from this computer's: the life left is taken from its answer but kept between 2 and
    // 5 minutes (the token's lifetime), so a skewed clock can neither cause a refresh loop nor a late refresh.
    this.serverExpiresAt = Date.parse(answer.accessTokenExpiresAt);
    const left = this.serverExpiresAt - now;
    this.expiresAt = now + Math.min(Math.max(Number.isFinite(left) ? left : ACCESS_LIFETIME_MS, 2 * 60 * 1000), ACCESS_LIFETIME_MS);
    this.idleMinutes = answer.idleTimeoutMinutes ?? this.idleMinutes;
    this.refreshedAt = now;
    this.lastActivity = Math.max(this.lastActivity, now);
    this.warningRemainingMs = null;
    this.setStatus('signedIn');
    this.schedule();
  }

  private share(): void {
    const now = this.env.clock.now();
    if (this.access) {
      this.env.channel?.post({ type: 'token', accessToken: this.access, serverExpiresAt: this.serverExpiresAt, lifeLeftMs: Math.max(0, this.expiresAt - now),
        idleMinutes: this.idleMinutes, refreshedAgoMs: Math.max(0, now - this.refreshedAt) });
    }
  }

  /** One refresh at a time across tabs; a tab that waited for the lock uses the token another tab just shared. */
  private refreshNow(): Promise<boolean> {
    if (this.refreshing) return this.refreshing;
    const tokenBefore = this.access;
    this.refreshing = this.env.lock(LOCK_NAME, async () => {
      if (this.access !== null && this.access !== tokenBefore) return true; // another tab refreshed while we waited
      try {
        this.accept(await this.env.refresh());
        this.share();
        return true;
      } catch {
        if (this.status === 'signedIn') this.end('expired', true);
        return false;
      }
    }).finally(() => { this.refreshing = null; });
    return this.refreshing;
  }

  private idleEnd(): number {
    return this.refreshedAt + this.idleMinutes * 60 * 1000;
  }

  private schedule(): void {
    if (this.timer !== null) this.env.clearTimer(this.timer);
    this.timer = this.env.setTimer(() => this.tick(), TICK_MS);
  }

  private tick(): void {
    this.timer = null;
    if (this.status !== 'signedIn') return;
    const now = this.env.clock.now();
    const active = this.lastActivity > this.refreshedAt;
    const idleEnd = this.idleEnd();
    if (now >= idleEnd) {
      this.end('expired', true);
      return;
    }

    if (active && this.warningRemainingMs === null && (now >= this.expiresAt - REFRESH_AHEAD_MS || now >= idleEnd - WARNING_BEFORE_MS)) {
      void this.refreshNow().then(() => this.schedule());
      return;
    }

    if (!active && now >= idleEnd - WARNING_BEFORE_MS) {
      this.warningRemainingMs = idleEnd - now;
      this.publish();
    }
    this.schedule();
  }

  private end(reason: 'signedOut' | 'expired', tellOthers: boolean): void {
    this.access = null;
    this.expiresAt = 0;
    this.serverExpiresAt = 0;
    this.warningRemainingMs = null;
    if (this.timer !== null) this.env.clearTimer(this.timer);
    this.timer = null;
    if (tellOthers) this.env.channel?.post({ type: 'ended', reason });
    this.setStatus(reason);
  }

  private setStatus(status: SessionStatus): void {
    this.status = status;
    this.publish();
  }

  private publish(): void {
    if (this.snapshot.status === this.status && this.snapshot.warningRemainingMs === this.warningRemainingMs) return;
    this.snapshot = { status: this.status, warningRemainingMs: this.warningRemainingMs };
    this.listeners.forEach((listener) => listener());
  }
}

/** The real browser environment. */
export function browserEnvironment(refresh: () => Promise<AuthAnswer>, signOut: (token: string) => Promise<void>, clock: Clock = systemClock): SessionEnvironment {
  const bc = typeof BroadcastChannel === 'undefined' ? null : new BroadcastChannel('erp-session');
  return {
    clock,
    refresh,
    signOut,
    channel: bc && {
      post: (message) => bc.postMessage(message),
      listen: (handler) => {
        const listener = (event: MessageEvent<SessionMessage>) => handler(event.data);
        bc.addEventListener('message', listener);
        return () => bc.removeEventListener('message', listener);
      },
    },
    lock: (name, task) => (navigator.locks ? navigator.locks.request(name, task) : task()),
    setTimer: (handler, ms) => window.setTimeout(handler, ms),
    clearTimer: (id) => window.clearTimeout(id),
    activityTarget: window,
  };
}
