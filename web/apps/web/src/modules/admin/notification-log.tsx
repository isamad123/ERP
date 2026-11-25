// The notification log (slice S1.16c Build item 10; FR-ADM-OPS-04, FR-KRN-NTF-05): every e-mail with its status, retries and failure
// reason, a summary now and per day, and resend of a finished message. The text of identity e-mails (links, codes) and salary
// messages is never shown: the server withholds it and the screen says so (GHR-06).
import { useState } from 'react';
import { parseDate, type CalendarDate } from '@internationalized/date';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Send } from 'lucide-react';
import { Banner, Button, DateField, Dialog, EmptyState, SelectField, StatusChip, TextField, useToast, type Status } from '@erp/ui';
import { useText, type MessageKey } from '@erp/i18n';
import { ApiProblem } from '../../api/client';
import { opsApi, PAGE_SIZE, type Delivery } from '../../api/ops';
import { useShell } from '../../shell/context';
import { dayAfter, dayStart, Loaded, useFormats, useProblemText } from './ops-parts';

const STATUSES = ['queued', 'sent', 'failed', 'dead'] as const;
const CHIP: Record<Delivery['status'], Status> = { queued: 'pending', sent: 'approved', failed: 'blocked', dead: 'inactive' };
const isBounce = (d: Delivery) => d.status === 'failed' && (d.error ?? '').startsWith('bounced:');

function StatusLabel({ d }: { d: Delivery }) {
  const { t } = useText();
  return <StatusChip status={CHIP[d.status]} label={isBounce(d) ? t('ntl.bounced') : t(`ntl.status.${d.status}` as MessageKey)} />;
}

/** The log without its page frame: the "Messages" tab of the job centre. */
export function NotificationLogBody() {
  const { t } = useText();
  const { can } = useShell();
  if (!can('ntf.outbound_messages.read')) return <Banner tone="danger">{t('ops.denied')}</Banner>;
  return (
    <div className="erp-stack">
      <p className="erp-muted">{t('ntl.description')}</p>
      <Summary />
      <Messages />
    </div>
  );
}

/** The numbers and the per-day table are for people who see every company; a company-scoped reader gets the list only. */
function Summary() {
  const { t } = useText();
  const f = useFormats();
  const summary = useQuery({ queryKey: ['ntl', 'summary'], queryFn: opsApi.deliverySummary, retry: false, refetchOnMount: 'always' });
  const daily = useQuery({ queryKey: ['ntl', 'daily'], queryFn: () => opsApi.deliveryDaily(14), retry: false, refetchOnMount: 'always' });
  if (summary.isError && summary.error instanceof ApiProblem && summary.error.status === 403) return null;
  return (
    <>
      <section className="erp-stack" aria-labelledby="ntl-summary">
        <h2 id="ntl-summary" className="erp-h3">{t('ntl.summary')}</h2>
        <Loaded query={summary}>
          {(s) => (
            <>
              <dl className="erp-row erp-wrap">
                {([['ntl.summary.queued', s.queued], ['ntl.summary.retrying', s.retrying], ['ntl.summary.sent', s.sent24h], ['ntl.summary.failed', s.failed24h], ['ntl.summary.dead', s.dead24h]] as const).map(([key, n]) => (
                  <div key={key}><dt className="erp-small erp-muted">{t(key)}</dt><dd><strong>{f.int(n)}</strong></dd></div>
                ))}
              </dl>
              {s.oldestQueuedAt && <p className="erp-small erp-muted">{t('ntl.summary.oldest', { when: f.when(s.oldestQueuedAt) })}</p>}
            </>
          )}
        </Loaded>
      </section>
      <section className="erp-stack" aria-labelledby="ntl-daily">
        <h2 id="ntl-daily" className="erp-h3">{t('ntl.daily')}</h2>
        <Loaded query={daily}>
          {(days) => days.length === 0 ? <p className="erp-muted">{t('ntl.daily.empty')}</p> : (
            <table className="erp-table">
              <caption className="erp-visually-hidden">{t('ntl.daily')}</caption>
              <thead><tr>
                <th scope="col">{t('ntl.daily.col.day')}</th><th scope="col">{t('ntl.daily.col.sent')}</th><th scope="col">{t('ntl.daily.col.failed')}</th>
                <th scope="col">{t('ntl.daily.col.dead')}</th><th scope="col">{t('ntl.daily.col.queued')}</th>
              </tr></thead>
              <tbody>
                {days.map((d) => (
                  <tr key={d.day}><th scope="row">{f.day(d.day)}</th><td>{f.int(d.sent)}</td><td>{f.int(d.failed)}</td><td>{f.int(d.dead)}</td><td>{f.int(d.queued)}</td></tr>
                ))}
              </tbody>
            </table>
          )}
        </Loaded>
      </section>
    </>
  );
}

function Messages() {
  const { t } = useText();
  const f = useFormats();
  const [status, setStatus] = useState('any');
  const [recipient, setRecipient] = useState('');
  const [from, setFrom] = useState<CalendarDate | null>(null);
  const [to, setTo] = useState<CalendarDate | null>(null);
  const [limit, setLimit] = useState(PAGE_SIZE);
  const [open, setOpen] = useState<string | null>(null);
  const params = { status: status === 'any' ? undefined : status, recipient: recipient.trim() || undefined, from: dayStart(from), to: dayAfter(to), limit };
  const list = useQuery({ queryKey: ['ntl', 'list', params], queryFn: ({ signal }) => opsApi.deliveries(params, signal), refetchOnMount: 'always' });
  return (
    <section className="erp-stack" aria-labelledby="ntl-messages">
      <h2 id="ntl-messages" className="erp-h3">{t('ntl.messages')}</h2>
      <div className="erp-filters" role="search" aria-label={t('ntl.filters')}>
        <SelectField label={t('ntl.filter.status')} value={status} onChange={setStatus}
          options={[{ id: 'any', label: t('ops.any') }, ...STATUSES.map((s) => ({ id: s, label: t(`ntl.status.${s}` as MessageKey) }))]} />
        <TextField label={t('ntl.filter.recipient')} type="email" value={recipient} onChange={setRecipient} />
        <DateField label={t('ntl.filter.from')} value={from} onChange={(v) => setFrom(v ? parseDate(v.toString()) : null)} />
        <DateField label={t('ntl.filter.to')} value={to} onChange={(v) => setTo(v ? parseDate(v.toString()) : null)} />
      </div>
      <Loaded query={list}>
        {(rows) => rows.length === 0 ? <EmptyState title={t('ntl.empty')}>{t('ntl.empty.body')}</EmptyState> : (
          <>
            <table className="erp-table">
              <caption className="erp-visually-hidden">{t('ntl.messages')}</caption>
              <thead><tr>
                <th scope="col">{t('ntl.col.when')}</th><th scope="col">{t('ntl.col.recipient')}</th><th scope="col">{t('ntl.col.subject')}</th>
                <th scope="col">{t('ntl.col.status')}</th><th scope="col">{t('ntl.col.attempts')}</th><th scope="col">{t('ntl.col.reason')}</th><th scope="col">{t('ntl.col.actions')}</th>
              </tr></thead>
              <tbody>
                {rows.map((d) => (
                  <tr key={d.id}>
                    <td>{f.when(d.createdAt)}</td>
                    <th scope="row"><bdi dir="ltr">{d.recipient}</bdi></th>
                    <td><bdi>{d.subject}</bdi></td>
                    <td><StatusLabel d={d} /></td>
                    <td>{f.int(d.attempts)}</td>
                    <td><bdi dir="ltr">{d.error ?? '–'}</bdi></td>
                    <td><Button variant="secondary" onPress={() => setOpen(d.id)}>{t('ntl.open')}</Button></td>
                  </tr>
                ))}
              </tbody>
            </table>
            {rows.length >= limit && <div><Button variant="secondary" onPress={() => setLimit(limit + PAGE_SIZE)}>{t('ntl.more')}</Button></div>}
          </>
        )}
      </Loaded>
      {open && <MessageDetail id={open} onClose={() => setOpen(null)} />}
    </section>
  );
}

function MessageDetail({ id, onClose }: { id: string; onClose: () => void }) {
  const { t } = useText();
  const { can, writable } = useShell();
  const f = useFormats();
  const toast = useToast();
  const queryClient = useQueryClient();
  const problemText = useProblemText();
  const query = useQuery({ queryKey: ['ntl', 'detail', id], queryFn: () => opsApi.delivery(id), refetchOnMount: 'always' });
  const resend = useMutation({
    mutationFn: () => opsApi.resend(id),
    onSuccess: () => { toast.success(t('ntl.resend.done')); void queryClient.invalidateQueries({ queryKey: ['ntl'] }); onClose(); },
    onError: (e) => toast.error(problemText(e, { 'errors.notification.delivery_waiting': t('ntl.resend.disabled') })),
  });
  return (
    <Dialog drawer isOpen onOpenChange={(o) => { if (!o) onClose(); }} title={t('ntl.detail.title')}
      actions={(close) => <Button variant="secondary" onPress={close}>{t('ntl.detail.close')}</Button>}>
      <Loaded query={query}>
        {({ message: m, body, link, bodyWithheld, resendOf }) => (
          <>
            <div className="erp-row erp-wrap"><StatusLabel d={m} /><span className="erp-muted">{f.when(m.createdAt)}</span></div>
            <dl className="erp-stack">
              <div><dt className="erp-small erp-muted">{t('ntl.col.recipient')}</dt><dd><bdi dir="ltr">{m.recipient}</bdi></dd></div>
              <div><dt className="erp-small erp-muted">{t('ntl.col.subject')}</dt><dd><bdi>{m.subject}</bdi></dd></div>
              {m.eventCode && <div><dt className="erp-small erp-muted">{t('ntl.detail.event')}</dt><dd><bdi dir="ltr">{m.eventCode}</bdi></dd></div>}
              <div><dt className="erp-small erp-muted">{t('ntl.col.attempts')}</dt><dd>{f.int(m.attempts)}</dd></div>
              {m.error && <div><dt className="erp-small erp-muted">{t('ntl.col.reason')}</dt><dd><bdi dir="ltr">{m.error}</bdi></dd></div>}
              {m.nextAttemptAt && m.status === 'queued' && <div><dt className="erp-small erp-muted">{t('jobs.col.next')}</dt><dd>{f.when(m.nextAttemptAt)}</dd></div>}
            </dl>
            {resendOf && <p className="erp-muted">{t('ntl.detail.resendOf')}</p>}
            {bodyWithheld ? <Banner tone="info" title={t('ntl.detail.body')}>{t('ntl.detail.withheld')}</Banner> : (
              <>
                <div><strong>{t('ntl.detail.body')}</strong><p><bdi>{body}</bdi></p></div>
                {link && <div><strong>{t('ntl.detail.link')}</strong><p><bdi dir="ltr">{link}</bdi></p></div>}
              </>
            )}
            {!m.resendable && <p className="erp-muted">{t('ntl.detail.notResendable')}</p>}
            {m.resendable && can('ntf.outbound_messages.resend') && writable && (
              <div><Button variant="primary" icon={<Send size={16} aria-hidden="true" />} isDisabled={m.status === 'queued'} isPending={resend.isPending} onPress={() => resend.mutate()}>{t('ntl.resend')}</Button></div>
            )}
          </>
        )}
      </Loaded>
    </Dialog>
  );
}
