// The expense card of the shared approvals inbox (slice S1.42; FR-HRM-EXP-03, GHR-06, FR-XM-32): under the generic fields of an expense claim the approver sees every line with its
// amount, the rate it was converted at, the policy flags and the receipt, and, when allowed to decide, types the approved amount of each line (0 rejects it; a reason is needed when it
// is below what was claimed) and saves the decisions. The Approve button of the task, which stays the inbox's own, then approves the saved amounts.
import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Banner, Button, Link, Skeleton, TextField, useToast } from '@erp/ui';
import { formatDate, formatDecimal, useLocaleSettings, useText, type MessageKey } from '@erp/i18n';
import { expensesApi, type ClaimLine, type ClaimView } from '../../api/expenses';
import { nameOf } from '../../api/workflow';
import type { ApprovalCardProps } from '../kernel/approval-cards';
import { useShell } from '../../shell/context';
import { decisionErrors, trimZeros, type DecisionDraft } from './expense-model';
import { Flags, Money, useExpenseRefusal } from './expense-parts';
import { ReceiptLink } from './expense-receipts';
import './expenses.css';
import './expenses-messages';

export default function ExpenseApprovalCard({ documentId }: ApprovalCardProps) {
  const { t } = useText();
  const refusal = useExpenseRefusal();
  const view = useQuery({ queryKey: ['hrm', 'expenses', 'claim', documentId], queryFn: ({ signal }) => expensesApi.detail(documentId, signal), retry: false });
  if (view.isPending) return <Skeleton lines={5} />;
  if (view.isError) return <section aria-label={t('exp.card.title')}><p className="erp-muted" role="status">{refusal(view.error)}</p></section>;
  return <Card view={view.data} />;
}

const startOf = (line: ClaimLine): DecisionDraft => ({ approved: trimZeros(line.approvedAmount ?? line.amount ?? '0'), reason: line.decisionReason ?? '' });

function Card({ view }: { view: ClaimView }) {
  const { t } = useText();
  const { writable } = useShell();
  const { locale, timeZone, numbers } = useLocaleSettings();
  const lang = locale === 'ur' ? 'ur' : 'en';
  const toast = useToast();
  const refusal = useExpenseRefusal();
  const queryClient = useQueryClient();
  const [drafts, setDrafts] = useState<Record<string, DecisionDraft>>(() => Object.fromEntries(view.lines.map((l) => [l.id, startOf(l)])));
  const [tried, setTried] = useState(false);
  const [message, setMessage] = useState<string | null>(null);
  const day = (d: string) => formatDate(`${d}T12:00:00Z`, lang, timeZone, numbers);
  const decide = view.canDecide && !view.amountsHidden;
  const problems = (l: ClaimLine) => decisionErrors(l, drafts[l.id] ?? startOf(l));
  const valid = view.lines.every((l) => Object.keys(problems(l)).length === 0);
  const save = useMutation({
    mutationFn: () => expensesApi.decide(view.id, view.lines.map((l) => {
      const d = drafts[l.id] ?? startOf(l);
      return { lineId: l.id, approvedAmount: d.approved.trim(), ...(d.reason.trim() ? { reason: d.reason.trim() } : {}) };
    })),
    onSuccess: async () => { setMessage(null); toast.success(t('exp.decide.saved')); await queryClient.invalidateQueries({ queryKey: ['hrm', 'expenses'] }); },
    onError: (e) => setMessage(refusal(e)),
  });
  const set = (id: string, change: Partial<DecisionDraft>) => setDrafts((d) => ({ ...d, [id]: { ...(d[id] ?? { approved: '', reason: '' }), ...change } }));
  return (
    <section aria-labelledby="erp-exp-card" className="erp-stack">
      <h3 id="erp-exp-card">{t('exp.card.title')}</h3>
      <dl className="erp-facts">
        <dt>{t('exp.facts.employee')}</dt><dd><bdi>{view.employeeNo ?? ''}</bdi></dd>
        {view.title && <><dt>{t('exp.field.title')}</dt><dd><bdi>{view.title}</bdi></dd></>}
        <dt>{t('exp.field.via')}</dt><dd>{t(`exp.via.${view.reimburseVia}` as MessageKey)}</dd>
        <dt>{t('exp.facts.total')}</dt><dd><Money value={view.totalAmount} currency={view.currencyCode} /></dd>
      </dl>
      {view.amountsHidden && <p className="erp-muted" role="note">{t('exp.hidden.note')}</p>}
      {view.lines.some((l) => l.flags.length > 0) && <Banner tone="warning" title={t('exp.card.flagged')}>{t('exp.card.flagged.body')}</Banner>}
      <ul className="erp-stack" aria-label={t('exp.lines')}>
        {view.lines.map((l, i) => {
          const draft = drafts[l.id] ?? startOf(l);
          const e = tried ? problems(l) : {};
          return (
            <li key={l.id} className="erp-exp-card-line">
              <strong>{t('exp.line.n', { n: i + 1 })}: <bdi>{nameOf(l.categoryName, lang) || l.categoryCode}</bdi></strong>
              <span><bdi>{day(l.expenseDate)}</bdi> · <Money value={l.amount} currency={view.currencyCode} />
                {l.originalCurrencyCode !== view.currencyCode && <> ({t('exp.card.claimedIn')} <Money value={l.originalAmount} currency={l.originalCurrencyCode} />{l.exchangeRate ? `, ${t('exp.line.rateShort', { rate: formatDecimal(l.exchangeRate, numbers) })}` : ''})</>}</span>
              {l.quantity && l.quantity !== '0' && <span>{t('exp.line.quantity', { value: formatDecimal(trimZeros(l.quantity), numbers) })}</span>}
              {l.description && <span><bdi>{l.description}</bdi></span>}
              <Flags flags={l.flags} blocksSubmit={l.blocksSubmit} />
              <span><ReceiptLink claimId={view.id} fileId={l.receiptFileId} /></span>
              {decide && (
                <div className="erp-exp-decide" role="group" aria-label={t('exp.decide.line', { n: i + 1 })}>
                  <TextField label={t('exp.decide.approved')} isCode inputMode="text" maxLength={20} value={draft.approved} onChange={(v) => set(l.id, { approved: v })} error={e.approved ? t(e.approved as MessageKey) : null}
                    help={t('exp.decide.approved.help')} />
                  <TextField label={t('exp.decide.reason')} maxLength={500} value={draft.reason} onChange={(v) => set(l.id, { reason: v })} error={e.reason ? t(e.reason as MessageKey) : null} />
                </div>)}
              {!decide && l.decidedAt !== null && <span><Money value={l.approvedAmount} currency={view.currencyCode} />{l.decisionReason ? <> · <bdi>{l.decisionReason}</bdi></> : null}</span>}
            </li>);
        })}
      </ul>
      {decide && (
        <div className="erp-stack">
          <p className="erp-muted">{t('exp.decide.note')}</p>
          {message && <p role="alert" className="erp-field-error">{message}</p>}
          <div><Button variant="secondary" isDisabled={!writable} isPending={save.isPending} onPress={() => { setTried(true); if (valid) save.mutate(); else setMessage(t('exp.fix')); }}>{t('exp.decide.save')}</Button></div>
        </div>)}
      <p><Link href={`/expenses/claims/${view.id}`}>{t('exp.card.open')}</Link></p>
    </section>
  );
}
