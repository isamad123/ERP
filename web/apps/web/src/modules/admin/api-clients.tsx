// API clients (slice S1.27c Build item 10; FR-ADM-SEC-02, FR-KRN-INT-07): the keys of connected systems, each with its permissions, allowed addresses and
// limit; create (the key is shown once with a copy button), make a new secret, revoke, and the use of each key. The server decides everything; the
// screen shows its answers. A key made while a second person must approve says it does not work yet.
import { useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { parseDate, type CalendarDate } from '@internationalized/date';
import { Copy, Plus } from 'lucide-react';
import { Banner, Button, CheckboxField, DateField, Dialog, EmptyState, ErrorPanel, Link, NumberField, PageHeader, Skeleton, StatusChip, TextAreaField, TextField, useToast, type Status } from '@erp/ui';
import { useDocumentTitle } from '@erp/engine';
import { formatDateTime, formatInteger, useLocaleSettings, useText, type MessageKey } from '@erp/i18n';
import { ApiProblem } from '../../api/client';
import { apiClients, type ApiClientSecret, type ApiClientView } from '../../api/api-clients';
import { systemClock } from '../../session/clock';
import { useShell } from '../../shell/context';
import { ReasonDialog } from './users-parts';
import './users.css';
import './api-clients-messages';

const KEY = ['iam', 'api-clients'] as const;
const CHIP: Record<ApiClientView['status'], Status> = { active: 'active', revoked: 'blocked', expired: 'inactive' };

export default function ApiClientsScreen() {
  const { t, server } = useText();
  const { can, writable } = useShell();
  const { locale, timeZone, numbers } = useLocaleSettings();
  useDocumentTitle(t('api.title'));
  const [creating, setCreating] = useState(false);
  const [rotating, setRotating] = useState<ApiClientView | null>(null);
  const [revoking, setRevoking] = useState<ApiClientView | null>(null);
  const [usageOf, setUsageOf] = useState<ApiClientView | null>(null);
  const [secret, setSecret] = useState<ApiClientSecret | null>(null);
  const list = useQuery({ queryKey: KEY, queryFn: ({ signal }) => apiClients.list(signal), refetchOnMount: 'always' });
  const problem = list.error instanceof ApiProblem ? list.error : null;
  const rows = list.data ?? [];
  const canManage = writable && can('iam.api_clients.manage');
  const lang = locale === 'ur' ? 'ur' : 'en';
  const when = (value: string | null, none: string) => (value ? formatDateTime(value, lang, timeZone, numbers) : none);
  return (
    <div className="erp-page">
      <PageHeader title={t('api.title')} description={t('api.description')}
        actions={canManage ? <Button variant="primary" icon={<Plus size={18} aria-hidden="true" />} onPress={() => setCreating(true)}>{t('api.create')}</Button> : undefined} />
      {list.isPending ? <Skeleton lines={5} /> : list.isError && rows.length === 0 ? (
        <ErrorPanel message={problem?.status === 403 ? t('api.denied') : t('api.loadError')} reference={problem?.reference ?? null}
          action={<Button variant="primary" onPress={() => { void list.refetch(); }}>{t('common.retry')}</Button>} />
      ) : rows.length === 0 ? (
        <EmptyState title={t('api.empty')} action={canManage ? <Button variant="primary" onPress={() => setCreating(true)}>{t('api.create')}</Button> : undefined}>{t('api.empty.body')}</EmptyState>
      ) : (
        <table className="erp-table">
          <caption className="erp-visually-hidden">{t('api.title')}</caption>
          <thead><tr>
            <th scope="col">{t('api.col.name')}</th><th scope="col">{t('api.col.id')}</th><th scope="col">{t('api.col.scopes')}</th><th scope="col">{t('api.col.limit')}</th>
            <th scope="col">{t('api.col.expires')}</th><th scope="col">{t('api.col.last')}</th><th scope="col">{t('api.col.status')}</th><th scope="col">{t('api.col.actions')}</th>
          </tr></thead>
          <tbody>
            {rows.map((c) => (
              <tr key={c.id}>
                <th scope="row"><bdi>{c.name}</bdi></th>
                <td><bdi className="erp-code">{c.clientId}</bdi></td>
                <td><div className="erp-stack">{c.scopes.map((s) => <bdi key={s}>{server(`permissions.${s}`) ?? s}</bdi>)}</div></td>
                <td>{formatInteger(c.rateLimitPerMinute, numbers)}</td>
                <td>{when(c.expiresAt, t('api.never'))}</td>
                <td>{when(c.lastUsedAt, t('api.unused'))}</td>
                <td>
                  <StatusChip status={CHIP[c.status]} label={t(`api.status.${c.status}` as MessageKey)} />
                  {c.status === 'active' && c.previousSecretValidUntil && new Date(c.previousSecretValidUntil).getTime() > systemClock.now() && (
                    <div className="erp-small erp-muted">{t('api.grace', { when: when(c.previousSecretValidUntil, '') })}</div>
                  )}
                </td>
                <td>
                  <span className="erp-row erp-wrap">
                    <Button variant="quiet" aria-label={`${t('api.usage')}: ${c.name}`} onPress={() => setUsageOf(c)}>{t('api.usage')}</Button>
                    {canManage && c.status === 'active' && <Button variant="quiet" aria-label={`${t('api.rotate')}: ${c.name}`} onPress={() => setRotating(c)}>{t('api.rotate')}</Button>}
                    {canManage && c.status === 'active' && <Button variant="quiet" aria-label={`${t('api.revoke')}: ${c.name}`} onPress={() => setRevoking(c)}>{t('api.revoke')}</Button>}
                    {can('aud.changes.view') && <Link href={`/admin/audit?entityType=iam.api_clients&entityId=${c.id}`}>{t('api.history')}</Link>}
                  </span>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {creating && <CreateDialog onClose={() => setCreating(false)} onMade={setSecret} />}
      {rotating && <RotateDialog client={rotating} onClose={() => setRotating(null)} onMade={setSecret} />}
      {revoking && <RevokeDialog client={revoking} onClose={() => setRevoking(null)} />}
      {usageOf && <UsageDialog client={usageOf} onClose={() => setUsageOf(null)} />}
      {secret && <SecretDialog made={secret} onClose={() => setSecret(null)} />}
    </div>
  );
}

function CreateDialog({ onClose, onMade }: { onClose: () => void; onMade: (made: ApiClientSecret) => void }) {
  const { t, server } = useText();
  const toast = useToast();
  const queryClient = useQueryClient();
  const options = useQuery({ queryKey: [...KEY, 'scope-options'], queryFn: ({ signal }) => apiClients.scopeOptions(signal) });
  const [name, setName] = useState('');
  const [scopes, setScopes] = useState<Set<string>>(new Set());
  const [ip, setIp] = useState('');
  const [rate, setRate] = useState(60);
  const [expires, setExpires] = useState<CalendarDate | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const toggle = (code: string, on: boolean) => setScopes((s) => { const n = new Set(s); if (on) n.add(code); else n.delete(code); return n; });
  const lines = ip.split(/\r?\n/).map((l) => l.trim()).filter((l) => l.length > 0);
  const ready = name.trim().length > 0 && scopes.size > 0 && rate >= 1;
  const submit = async () => {
    setBusy(true);
    setError(null);
    try {
      const made = await apiClients.create({ name: name.trim(), scopes: [...scopes], ipAllow: lines, rateLimitPerMinute: rate, expiresAt: expires ? `${expires.toString()}T23:59:59Z` : null });
      void queryClient.invalidateQueries({ queryKey: KEY });
      onMade(made);
      onClose();
    } catch (e) {
      const key = e instanceof ApiProblem ? e.key : null;
      setError((key && server(key)) ?? t('api.loadError'));
      if (!(e instanceof ApiProblem)) toast.error(t('api.loadError'));
    } finally {
      setBusy(false);
    }
  };
  return (
    <Dialog role="alertdialog" title={t('api.create')} isOpen onOpenChange={(open) => { if (!open) onClose(); }}
      actions={() => (
        <>
          <Button variant="secondary" onPress={onClose}>{t('api.cancel')}</Button>
          <Button variant="primary" isPending={busy} isDisabled={!ready} onPress={() => { void submit(); }}>{t('api.create.submit')}</Button>
        </>
      )}>
      <TextField label={t('api.field.name')} help={t('api.field.name.help')} isRequired value={name} onChange={setName} maxLength={100} />
      <fieldset className="erp-stack">
        <legend>{t('api.field.scopes')}</legend>
        <p className="erp-small erp-muted">{t('api.field.scopes.help')}</p>
        {options.isPending ? <Skeleton lines={2} /> : (options.data ?? []).map((o) => (
          <CheckboxField key={o.code} label={server(o.descriptionKey) ?? o.code} isSelected={scopes.has(o.code)} onChange={(on) => toggle(o.code, on)} />
        ))}
      </fieldset>
      <TextAreaField label={t('api.field.ip')} help={t('api.field.ip.help')} value={ip} onChange={setIp} maxLength={4000} />
      <NumberField label={t('api.field.rate')} help={t('api.field.rate.help')} isRequired value={rate} onChange={setRate} minValue={1} />
      <DateField label={t('api.field.expires')} help={t('api.field.expires.help')} value={expires} onChange={(v) => setExpires(v ? parseDate(v.toString()) : null)} />
      {error && <Banner tone="danger">{error}</Banner>}
    </Dialog>
  );
}

function RotateDialog({ client, onClose, onMade }: { client: ApiClientView; onClose: () => void; onMade: (made: ApiClientSecret) => void }) {
  const { t } = useText();
  const queryClient = useQueryClient();
  return (
    <ReasonDialog isOpen reasonOptional title={t('api.rotate.title')} confirmLabel={t('api.rotate')} effect={t('api.rotate.effect', { name: client.name })} onClose={onClose}
      onConfirm={async () => {
        const made = await apiClients.rotate(client.id);
        void queryClient.invalidateQueries({ queryKey: KEY });
        onMade(made);
      }} />
  );
}

function RevokeDialog({ client, onClose }: { client: ApiClientView; onClose: () => void }) {
  const { t } = useText();
  const toast = useToast();
  const queryClient = useQueryClient();
  return (
    <ReasonDialog isOpen danger reasonOptional title={t('api.revoke.title')} confirmLabel={t('api.revoke')} effect={t('api.revoke.effect', { name: client.name })} onClose={onClose}
      onConfirm={async () => {
        await apiClients.revoke(client.id);
        void queryClient.invalidateQueries({ queryKey: KEY });
        toast.success(t('api.status.revoked'));
      }} />
  );
}

function UsageDialog({ client, onClose }: { client: ApiClientView; onClose: () => void }) {
  const { t } = useText();
  const { numbers } = useLocaleSettings();
  const usage = useQuery({ queryKey: [...KEY, client.id, 'usage'], queryFn: ({ signal }) => apiClients.usage(client.id, 30, signal), refetchOnMount: 'always' });
  const days = usage.data?.days ?? [];
  return (
    <Dialog title={t('api.usage.title', { name: client.name })} isOpen onOpenChange={(open) => { if (!open) onClose(); }}
      actions={() => <Button variant="secondary" onPress={onClose}>{t('api.cancel')}</Button>}>
      {usage.isPending ? <Skeleton lines={3} /> : usage.isError ? <ErrorPanel message={t('api.loadError')} /> : days.length === 0 ? <p>{t('api.usage.empty')}</p> : (
        <table className="erp-table">
          <caption className="erp-visually-hidden">{t('api.usage.title', { name: client.name })}</caption>
          <thead><tr>
            <th scope="col">{t('api.usage.col.date')}</th><th scope="col">{t('api.usage.col.calls')}</th><th scope="col">{t('api.usage.col.errors4xx')}</th>
            <th scope="col">{t('api.usage.col.errors5xx')}</th><th scope="col">{t('api.usage.col.limited')}</th><th scope="col">{t('api.usage.col.p95')}</th>
          </tr></thead>
          <tbody>
            {days.map((d) => (
              <tr key={d.date}>
                <th scope="row"><bdi>{d.date}</bdi></th>
                <td>{formatInteger(d.calls, numbers)}</td><td>{formatInteger(d.errors4xx, numbers)}</td><td>{formatInteger(d.errors5xx, numbers)}</td>
                <td>{formatInteger(d.rateLimited, numbers)}</td><td>{formatInteger(d.p95Ms, numbers)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </Dialog>
  );
}

/** The key, shown once: it is held only in this component's state and gone when the person confirms they stored it. */
function SecretDialog({ made, onClose }: { made: ApiClientSecret; onClose: () => void }) {
  const { t } = useText();
  const toast = useToast();
  const copy = async () => {
    try { await navigator.clipboard.writeText(made.apiKey); toast.success(t('api.secret.copied')); } catch { toast.error(t('api.secret.copyFailed')); }
  };
  return (
    <Dialog role="alertdialog" title={t('api.secret.title')} isOpen onOpenChange={() => undefined}
      actions={() => <Button variant="primary" onPress={onClose}>{t('api.secret.done')}</Button>}>
      <p>{t('api.secret.body')}</p>
      {made.heldRequestId && <Banner tone="warning">{t('api.secret.waiting')} <Link href="/approvals">{t('api.secret.waiting.link')}</Link></Banner>}
      <TextField label={t('api.secret.label')} value={made.apiKey} isReadOnly isCode />
      <Button variant="secondary" icon={<Copy size={16} aria-hidden="true" />} onPress={() => { void copy(); }}>{t('api.secret.copy')}</Button>
    </Dialog>
  );
}
