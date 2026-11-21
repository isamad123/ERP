// The web application's clock (CLAUDE.md: injected clock). Tests replace it with a fake one.
export interface Clock {
  now(): number;
}

export const systemClock: Clock = {
  // eslint-disable-next-line no-restricted-syntax -- the one place the real time is read
  now: () => Date.now(),
};
