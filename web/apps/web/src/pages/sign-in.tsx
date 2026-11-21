// Sign-in screens (slice S1.12a, Build item 7; FR-KRN-IAM-01, FR-KRN-IAM-02, FR-ADM-USR-24, ruling Q4). The server
// decides every step (sign-in → second factor → required password change → required enrolment → session); the
// screens only show the step it answers. The challenge token lives in memory only.
import { useState, type FormEvent } from 'react';
import { useNavigate, useSearchParams } from 'react-router';
import { Banner, Button, CheckboxField, CopyButton, Link, OneTimeCodeField, PasswordField, QrCode, RadioGroupField, TextField } from '@erp/ui';
import { formatDate, isolate, useLocaleSettings, useText } from '@erp/i18n';
import { ApiProblem } from '../api/client';
import { identity, type AuthAnswer, type Enrolment } from '../api/identity';
import { noteFreshSignIn, safeReturn, useSession } from '../app/session-context';
import { AuthLayout, ProblemBanner, usePasswordErrors, useSignInFlow } from './auth-layout';

/** Moves to the screen of the step the server answered. */
export function useNextStep() {
  const navigate = useNavigate();
  const session = useSession();
  const { flow, setFlow } = useSignInFlow();
  return (answer: AuthAnswer, returnTo = flow.returnTo) => {
    switch (answer.outcome) {
      case 'tokens':
        session.signedIn(answer);
        noteFreshSignIn();
        setFlow({ challengeToken: null, recoveryCodes: answer.recoveryCodes ?? null, enrolBy: answer.mfaEnrolBy ?? null, returnTo });
        if (answer.recoveryCodes && answer.recoveryCodes.length > 0) navigate('/sign-in/recovery-codes', { replace: true });
        else if (answer.mfaEnrolBy) navigate('/sign-in/reminder', { replace: true });
        else navigate(returnTo, { replace: true });
        return;
      case 'mfa_required':
        setFlow({ challengeToken: answer.challengeToken ?? null, methods: answer.mfaMethods ?? ['totp'], rememberDays: answer.rememberDeviceDays ?? 0, returnTo });
        navigate('/sign-in/verify');
        return;
      case 'password_change_required':
        setFlow({ challengeToken: answer.challengeToken ?? null, returnTo });
        navigate('/sign-in/new-password');
        return;
      case 'mfa_enrolment_required':
        setFlow({ challengeToken: answer.challengeToken ?? null, returnTo });
        navigate('/sign-in/set-up');
        return;
    }
  };
}

export function SignInPage() {
  const { t } = useText();
  const [params] = useSearchParams();
  const next = useNextStep();
  const [login, setLogin] = useState('');
  const [password, setPassword] = useState('');
  const [errors, setErrors] = useState<{ login?: string; password?: string }>({});
  const [problem, setProblem] = useState<unknown>(null);
  const [pending, setPending] = useState(false);
  const reason = params.get('reason');

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    const found = { ...(login.trim() ? {} : { login: t('auth.signIn.loginRequired') }), ...(password ? {} : { password: t('auth.signIn.passwordRequired') }) };
    setErrors(found);
    if (Object.keys(found).length > 0) return;
    setPending(true);
    setProblem(null);
    try {
      next(await identity.signIn(login.trim(), password), safeReturn(params.get('return')));
    } catch (error) {
      setProblem(error);
      setPassword('');
    } finally {
      setPending(false);
    }
  };

  return (
    <AuthLayout titleKey="auth.signIn.title">
      {reason === 'expired' && <Banner tone="info">{t('session.expired')}</Banner>}
      {reason === 'signedOut' && <Banner tone="info">{t('session.signedOut')}</Banner>}
      <ProblemBanner error={problem} />
      <form className="erp-stack" onSubmit={submit} noValidate>
        <TextField label={t('auth.signIn.login')} name="username" autoComplete="username" value={login} onChange={setLogin} isRequired error={errors.login ?? null} />
        <PasswordField label={t('auth.signIn.password')} name="password" autoComplete="current-password" value={password} onChange={setPassword} isRequired error={errors.password ?? null} />
        <Button variant="primary" type="submit" block isPending={pending}>{t('auth.signIn.submit')}</Button>
      </form>
      <Link href="/forgot-password">{t('auth.signIn.forgot')}</Link>
    </AuthLayout>
  );
}

type Method = 'totp' | 'email' | 'recovery';

export function VerifyPage() {
  const { t } = useText();
  const navigate = useNavigate();
  const next = useNextStep();
  const { flow } = useSignInFlow();
  const [method, setMethod] = useState<Method>(flow.methods.includes('totp') ? 'totp' : 'email');
  const [code, setCode] = useState('');
  const [recovery, setRecovery] = useState('');
  const [remember, setRemember] = useState(false);
  const [sent, setSent] = useState(false);
  const [fieldError, setFieldError] = useState<string | null>(null);
  const [problem, setProblem] = useState<unknown>(null);
  const [pending, setPending] = useState(false);
  const challenge = flow.challengeToken;
  if (!challenge) return <StartAgain />;

  const sendCode = async () => {
    setProblem(null);
    try {
      await identity.sendEmailCode(challenge);
      setSent(true);
    } catch (error) {
      setProblem(error);
    }
  };

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    const value = method === 'recovery' ? recovery.trim() : code;
    if (!value) return setFieldError(t('auth.mfa.codeRequired'));
    if (method !== 'recovery' && value.length !== 6) return setFieldError(t('auth.mfa.codeFormat'));
    setFieldError(null);
    setPending(true);
    setProblem(null);
    try {
      next(await identity.mfa(challenge, method === 'recovery' ? { recoveryCode: value, rememberDevice: remember } : { code: value, rememberDevice: remember }));
    } catch (error) {
      // A used or expired challenge cannot be tried again: the person starts over (the same answer for every failure).
      if (error instanceof ApiProblem && error.status === 401) {
        setCode('');
        setRecovery('');
      }
      setProblem(error);
    } finally {
      setPending(false);
    }
  };

  const others: Method[] = (['totp', 'email', 'recovery'] as const).filter((m) => m !== method && (m === 'recovery' || flow.methods.includes(m)));
  const switchLabel = { totp: t('auth.mfa.useTotp'), email: t('auth.mfa.useEmail'), recovery: t('auth.mfa.useRecovery') } as const;

  return (
    <AuthLayout titleKey="auth.mfa.title">
      <ProblemBanner error={problem} />
      <form className="erp-stack" onSubmit={submit} noValidate>
        {method === 'totp' && (
          <>
            <p>{t('auth.mfa.totp.intro')}</p>
            <OneTimeCodeField label={t('auth.mfa.totp.code')} value={code} onChange={setCode} isRequired error={fieldError} />
          </>
        )}
        {method === 'email' && (
          <>
            <p>{t('auth.mfa.email.intro')}</p>
            {sent && <Banner tone="success">{t('auth.mfa.email.sent')}</Banner>}
            <Button variant="secondary" onPress={sendCode}>{sent ? t('auth.mfa.email.resend') : t('auth.mfa.email.send')}</Button>
            <OneTimeCodeField label={t('auth.mfa.email.code')} value={code} onChange={setCode} isRequired error={fieldError} />
          </>
        )}
        {method === 'recovery' && (
          <>
            <p>{t('auth.mfa.recovery.intro')}</p>
            <TextField label={t('auth.mfa.recovery.code')} value={recovery} onChange={setRecovery} autoComplete="off" isCode isRequired error={fieldError} />
          </>
        )}
        {flow.rememberDays > 0 && (
          <CheckboxField label={t('auth.mfa.remember', { days: flow.rememberDays })} help={t('auth.mfa.remember.help')} isSelected={remember} onChange={setRemember} />
        )}
        <Button variant="primary" type="submit" block isPending={pending}>{t('auth.mfa.submit')}</Button>
      </form>
      <nav className="erp-stack" aria-label={t('auth.mfa.other')}>
        {others.map((m) => (
          <Button key={m} variant="quiet" onPress={() => { setMethod(m); setCode(''); setFieldError(null); setProblem(null); }}>{switchLabel[m]}</Button>
        ))}
        <Link href="/sign-in" onPress={() => navigate('/sign-in')}>{t('auth.backToSignIn')}</Link>
      </nav>
    </AuthLayout>
  );
}

/** New password fields with the confirmation and the server's rule messages (shared by four screens). */
export function NewPasswordFields({ password, confirm, onPassword, onConfirm, error, confirmError }: {
  password: string; confirm: string; onPassword: (v: string) => void; onConfirm: (v: string) => void; error: string | null; confirmError: string | null;
}) {
  const { t } = useText();
  return (
    <>
      <PasswordField label={t('auth.newPassword')} autoComplete="new-password" value={password} onChange={onPassword} help={t('auth.passwordHelp')} isRequired error={error} />
      <PasswordField label={t('auth.confirmPassword')} autoComplete="new-password" value={confirm} onChange={onConfirm} isRequired error={confirmError} />
    </>
  );
}

export function useNewPassword() {
  const { t } = useText();
  const [password, setPassword] = useState('');
  const [confirm, setConfirm] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [confirmError, setConfirmError] = useState<string | null>(null);
  return {
    fields: { password, confirm, onPassword: setPassword, onConfirm: setConfirm, error, confirmError },
    password,
    /** Checks locally what can be checked; the server checks its password rules. */
    validate: () => {
      setError(password ? null : t('auth.signIn.passwordRequired'));
      setConfirmError(password && confirm !== password ? t('auth.passwordMismatch') : null);
      return !!password && confirm === password;
    },
    setServerError: setError,
  };
}

export function NewPasswordPage() {
  const { t } = useText();
  const next = useNextStep();
  const { flow } = useSignInFlow();
  const form = useNewPassword();
  const passwordErrors = usePasswordErrors();
  const [problem, setProblem] = useState<unknown>(null);
  const [pending, setPending] = useState(false);
  const challenge = flow.challengeToken;
  if (!challenge) return <StartAgain />;

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    if (!form.validate()) return;
    setPending(true);
    setProblem(null);
    try {
      next(await identity.changeRequiredPassword(challenge, form.password));
    } catch (error) {
      const rules = passwordErrors(error);
      if (rules) form.setServerError(rules);
      else setProblem(error);
    } finally {
      setPending(false);
    }
  };

  return (
    <AuthLayout titleKey="auth.passwordChange.title">
      <p>{t('auth.passwordChange.intro')}</p>
      <ProblemBanner error={problem} />
      <form className="erp-stack" onSubmit={submit} noValidate>
        <NewPasswordFields {...form.fields} />
        <Button variant="primary" type="submit" block isPending={pending}>{t('auth.passwordChange.submit')}</Button>
      </form>
    </AuthLayout>
  );
}

/**
 * Setting up the second step: required at sign-in after the grace period (with the challenge token), or during the
 * grace period from the reminder (signed in; the current password confirms it, S1.05 build item 5). An authenticator
 * shows its QR code and key; at sign-in it also needs the code e-mailed to the person (R-003).
 */
export function SetUpPage({ signedIn = false }: { signedIn?: boolean }) {
  const { t } = useText();
  const navigate = useNavigate();
  const next = useNextStep();
  const { flow, setFlow } = useSignInFlow();
  const [kind, setKind] = useState<'totp' | 'email'>('totp');
  const [enrolment, setEnrolment] = useState<Enrolment | null>(null);
  const [currentPassword, setCurrentPassword] = useState('');
  const [code, setCode] = useState('');
  const [emailCode, setEmailCode] = useState('');
  const [problem, setProblem] = useState<unknown>(null);
  const [pending, setPending] = useState(false);
  const challenge = flow.challengeToken;
  if (!signedIn && !challenge) return <StartAgain />;

  const start = async (event: FormEvent) => {
    event.preventDefault();
    setPending(true);
    setProblem(null);
    try {
      setEnrolment(signedIn || !challenge ? await identity.startMfa(kind, currentPassword) : await identity.startRequiredEnrolment(challenge, kind));
    } catch (error) {
      setProblem(error);
    } finally {
      setPending(false);
    }
  };

  const confirm = async (event: FormEvent) => {
    event.preventDefault();
    if (!enrolment) return;
    setPending(true);
    setProblem(null);
    try {
      if (signedIn || !challenge) {
        const { recoveryCodes } = await identity.confirmMfa(enrolment.deviceId, code);
        setFlow({ recoveryCodes, enrolBy: null });
        navigate(recoveryCodes.length > 0 ? '/sign-in/recovery-codes' : safeReturn(flow.returnTo), { replace: true });
      } else {
        next(await identity.confirmRequiredEnrolment(challenge, enrolment.deviceId, code, enrolment.kind === 'totp' ? emailCode : undefined));
      }
    } catch (error) {
      setProblem(error);
    } finally {
      setPending(false);
    }
  };

  if (!enrolment) {
    return (
      <AuthLayout titleKey="auth.enrol.title">
        <p>{t('auth.enrol.intro')}</p>
        <ProblemBanner error={problem} />
        <form className="erp-stack" onSubmit={start} noValidate>
          <RadioGroupField label={t('auth.enrol.method')} value={kind} onChange={(id) => setKind(id === 'email' ? 'email' : 'totp')} options={[
            { id: 'totp', label: t('auth.enrol.method.totp'), detail: t('auth.enrol.method.totp.help') },
            { id: 'email', label: t('auth.enrol.method.email'), detail: t('auth.enrol.method.email.help') },
          ]} />
          {signedIn && <PasswordField label={t('auth.enrol.currentPassword')} autoComplete="current-password" value={currentPassword} onChange={setCurrentPassword} isRequired />}
          <Button variant="primary" type="submit" block isPending={pending}>{t('auth.enrol.start')}</Button>
        </form>
      </AuthLayout>
    );
  }

  return (
    <AuthLayout titleKey="auth.enrol.title">
      <ProblemBanner error={problem} />
      <form className="erp-stack" onSubmit={confirm} noValidate>
        {enrolment.kind === 'totp' && enrolment.otpAuthUri && enrolment.secret ? (
          <>
            <p>{t('auth.enrol.scan')}</p>
            <QrCode value={enrolment.otpAuthUri} label={t('auth.enrol.qr')} />
            <p>{t('auth.enrol.secret')}</p>
            <div className="erp-row">
              <code className="erp-code erp-auth-secret">{enrolment.secret.replace(/(.{4})/g, '$1 ').trim()}</code>
              <CopyButton text={enrolment.secret} />
            </div>
            <OneTimeCodeField label={t('auth.enrol.code')} value={code} onChange={setCode} isRequired />
            {!signedIn && (
              <OneTimeCodeField label={t('auth.enrol.emailCode')} help={t('auth.enrol.emailCode.help')} value={emailCode} onChange={setEmailCode} isRequired />
            )}
          </>
        ) : (
          <>
            <p>{t('auth.enrol.emailOnly')}</p>
            <OneTimeCodeField label={t('auth.mfa.email.code')} value={code} onChange={setCode} isRequired />
          </>
        )}
        <Button variant="primary" type="submit" block isPending={pending}>{t('auth.enrol.submit')}</Button>
      </form>
    </AuthLayout>
  );
}

/** Recovery codes, shown once after the first factor is set up. */
export function RecoveryCodesPage() {
  const { t } = useText();
  const navigate = useNavigate();
  const { flow, setFlow } = useSignInFlow();
  const codes = flow.recoveryCodes;
  if (!codes || codes.length === 0) return <StartAgain />;
  return (
    <AuthLayout titleKey="auth.recovery.title">
      <p>{t('auth.recovery.intro')}</p>
      <ul className="erp-auth-codes" aria-label={t('auth.recovery.list')}>
        {codes.map((code) => <li key={code} className="erp-code">{code}</li>)}
      </ul>
      <div className="erp-row">
        <CopyButton text={codes.join('\n')} label={t('auth.recovery.copy')} />
        <Button variant="primary" onPress={() => {
          setFlow({ recoveryCodes: null });
          navigate(flow.enrolBy ? '/sign-in/reminder' : safeReturn(flow.returnTo), { replace: true });
        }}>{t('auth.recovery.saved')}</Button>
      </div>
    </AuthLayout>
  );
}

/** During the grace period: the deadline, "Set up now" or "Remind me later". */
export function ReminderPage() {
  const { t } = useText();
  const navigate = useNavigate();
  const { locale, timeZone } = useLocaleSettings();
  const { flow } = useSignInFlow();
  const date = flow.enrolBy ? formatDate(flow.enrolBy, locale === 'ur' ? 'ur' : 'en', timeZone) : null;
  return (
    <AuthLayout titleKey="auth.enrol.reminder.title">
      {date && <p>{t('auth.enrol.reminder.body', { date: isolate(date) })}</p>}
      <div className="erp-row">
        <Button variant="primary" onPress={() => navigate('/sign-in/set-up-now')}>{t('auth.enrol.reminder.now')}</Button>
        <Button variant="secondary" onPress={() => navigate(safeReturn(flow.returnTo), { replace: true })}>{t('auth.enrol.reminder.later')}</Button>
      </div>
    </AuthLayout>
  );
}

/** A sign-in step opened without its earlier steps (reload, typed address): start at the beginning. */
function StartAgain() {
  const { t } = useText();
  return (
    <AuthLayout titleKey="auth.signIn.title">
      <Banner tone="info">{t('session.expired')}</Banner>
      <Link href="/sign-in">{t('auth.backToSignIn')}</Link>
    </AuthLayout>
  );
}
