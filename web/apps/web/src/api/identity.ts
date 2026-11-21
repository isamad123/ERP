// Hand-written client of the identity endpoints (Identity API version 3, slice S1.12a; contracts register rows
// "Identity API" and "Identity e-mail API"). The anonymous sign-in steps never send the access token.
import { requestJson } from './client';

export type Outcome = 'tokens' | 'mfa_required' | 'password_change_required' | 'mfa_enrolment_required';

export interface AuthAnswer {
  outcome: Outcome;
  accessToken?: string | null;
  accessTokenExpiresAt?: string | null;
  sessionId?: string | null;
  challengeToken?: string | null;
  mfaEnrolBy?: string | null;
  recoveryCodes?: string[] | null;
  mfaMethods?: string[] | null;
  idleTimeoutMinutes?: number | null;
  rememberDeviceDays?: number | null;
}

export interface Enrolment {
  deviceId: string;
  otpAuthUri?: string | null;
  secret?: string | null;
  kind: 'totp' | 'email';
  emailCodeSent?: boolean;
}

const anonymous = { auth: false } as const;

export const identity = {
  signIn: (login: string, password: string) => requestJson<AuthAnswer>('POST', '/api/iam/auth/sign-in', { ...anonymous, body: { login, password } }),
  mfa: (challengeToken: string, input: { code?: string; recoveryCode?: string; rememberDevice?: boolean }) =>
    requestJson<AuthAnswer>('POST', '/api/iam/auth/mfa', { ...anonymous, body: { challengeToken, ...input } }),
  sendEmailCode: (challengeToken: string) => requestJson<undefined>('POST', '/api/iam/auth/mfa/email-code', { ...anonymous, body: { challengeToken } }),
  changeRequiredPassword: (challengeToken: string, newPassword: string) =>
    requestJson<AuthAnswer>('POST', '/api/iam/auth/password-change', { ...anonymous, body: { challengeToken, newPassword } }),
  startRequiredEnrolment: (challengeToken: string, kind: 'totp' | 'email') =>
    requestJson<Enrolment>('POST', '/api/iam/auth/mfa-enrolment', { ...anonymous, body: { challengeToken, kind } }),
  confirmRequiredEnrolment: (challengeToken: string, deviceId: string, code: string, emailCode?: string) =>
    requestJson<AuthAnswer>('POST', '/api/iam/auth/mfa-enrolment/confirm', { ...anonymous, body: { challengeToken, deviceId, code, emailCode } }),
  forgotPassword: (login: string) => requestJson<undefined>('POST', '/api/iam/auth/password/forgot', { ...anonymous, body: { login } }),
  resetPassword: (token: string, newPassword: string) => requestJson<undefined>('POST', '/api/iam/auth/password/reset', { ...anonymous, body: { token, newPassword } }),
  activate: (token: string, newPassword: string) => requestJson<AuthAnswer>('POST', '/api/iam/auth/activate', { ...anonymous, body: { token, newPassword } }),
  /** The refresh token travels in the HttpOnly cookie; the body stays empty (version 3). */
  refresh: () => requestJson<AuthAnswer>('POST', '/api/iam/auth/refresh', { ...anonymous, body: {} }),
  signOut: () => requestJson<undefined>('POST', '/api/iam/auth/sign-out', { body: {} }),

  // Self-service enrolment during the grace period (the person is signed in; S1.05 build item 5).
  startMfa: (kind: 'totp' | 'email', currentPassword: string) =>
    requestJson<Enrolment>('POST', '/api/iam/me/mfa-devices', { body: { kind, currentPassword, label: kind === 'totp' ? 'Authenticator' : null } }),
  confirmMfa: (deviceId: string, code: string) =>
    requestJson<{ recoveryCodes: string[] }>('POST', `/api/iam/me/mfa-devices/${encodeURIComponent(deviceId)}/confirm`, { body: { code } }),
};
