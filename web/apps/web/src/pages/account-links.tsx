// Screens opened from e-mailed links and the forgot-password request (slice S1.12a, Build item 7; FR-KRN-IAM-01,
// FR-ADM-USR-02): forgot password (always the same answer, whatever was entered), /reset-password?token= and
// /activate?token=. The link's token is read from the address and sent once; it is never stored.
import { useState, type FormEvent } from 'react';
import { useSearchParams } from 'react-router';
import { Banner, Button, Link, TextField } from '@erp/ui';
import { useText } from '@erp/i18n';
import { ApiProblem } from '../api/client';
import { identity } from '../api/identity';
import { AuthLayout, ProblemBanner, usePasswordErrors } from './auth-layout';
import { NewPasswordFields, useNewPassword, useNextStep } from './sign-in';

export function ForgotPasswordPage() {
  const { t } = useText();
  const [login, setLogin] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [done, setDone] = useState(false);
  const [problem, setProblem] = useState<unknown>(null);
  const [pending, setPending] = useState(false);

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    if (!login.trim()) return setError(t('auth.signIn.loginRequired'));
    setError(null);
    setPending(true);
    setProblem(null);
    try {
      await identity.forgotPassword(login.trim());
      setDone(true); // the same message for a known and an unknown login (no account enumeration)
    } catch (e) {
      setProblem(e);
    } finally {
      setPending(false);
    }
  };

  return (
    <AuthLayout titleKey="auth.forgot.title">
      {done ? (
        <Banner tone="success">{t('auth.forgot.done')}</Banner>
      ) : (
        <>
          <p>{t('auth.forgot.intro')}</p>
          <ProblemBanner error={problem} />
          <form className="erp-stack" onSubmit={submit} noValidate>
            <TextField label={t('auth.signIn.login')} autoComplete="username" value={login} onChange={setLogin} isRequired error={error} />
            <Button variant="primary" type="submit" block isPending={pending}>{t('auth.forgot.submit')}</Button>
          </form>
        </>
      )}
      <Link href="/sign-in">{t('auth.backToSignIn')}</Link>
    </AuthLayout>
  );
}

function LinkPasswordPage({ kind }: { kind: 'reset' | 'activate' }) {
  const { t } = useText();
  const [params] = useSearchParams();
  const token = params.get('token');
  const next = useNextStep();
  const form = useNewPassword();
  const passwordErrors = usePasswordErrors();
  const [invalid, setInvalid] = useState(false);
  const [done, setDone] = useState(false);
  const [problem, setProblem] = useState<unknown>(null);
  const [pending, setPending] = useState(false);
  const title = kind === 'reset' ? 'auth.reset.title' : 'auth.activate.title';

  if (!token) {
    return (
      <AuthLayout titleKey={title}>
        <Banner tone="warning">{t('auth.link.incomplete')}</Banner>
        <Link href="/sign-in">{t('auth.backToSignIn')}</Link>
      </AuthLayout>
    );
  }

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    if (!form.validate()) return;
    setPending(true);
    setProblem(null);
    try {
      if (kind === 'reset') {
        await identity.resetPassword(token, form.password);
        setDone(true);
      } else {
        next(await identity.activate(token, form.password), '/');
      }
    } catch (error) {
      const rules = passwordErrors(error);
      if (rules) form.setServerError(rules);
      else if (error instanceof ApiProblem && error.status === 401) setInvalid(true);
      else setProblem(error);
    } finally {
      setPending(false);
    }
  };

  return (
    <AuthLayout titleKey={title}>
      {done && <Banner tone="success">{t('auth.reset.done')}</Banner>}
      {invalid && <Banner tone="warning">{t(kind === 'reset' ? 'auth.reset.invalid' : 'auth.activate.invalid')}</Banner>}
      {!done && !invalid && (
        <>
          {kind === 'activate' && <p>{t('auth.activate.intro')}</p>}
          <ProblemBanner error={problem} />
          <form className="erp-stack" onSubmit={submit} noValidate>
            <NewPasswordFields {...form.fields} />
            <Button variant="primary" type="submit" block isPending={pending}>{t(kind === 'reset' ? 'auth.reset.submit' : 'auth.activate.submit')}</Button>
          </form>
        </>
      )}
      <Link href={kind === 'reset' && invalid ? '/forgot-password' : '/sign-in'}>{kind === 'reset' && invalid ? t('auth.forgot.title') : t('auth.backToSignIn')}</Link>
    </AuthLayout>
  );
}

export const ResetPasswordPage = () => <LinkPasswordPage kind="reset" />;
export const ActivatePage = () => <LinkPasswordPage kind="activate" />;
