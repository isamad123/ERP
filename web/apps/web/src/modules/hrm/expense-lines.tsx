// The lines of an expense claim (slice S1.42; FR-HRM-EXP-02, EXP-03): a card per line while the claim is a draft (category, date, amount, currency, days or kilometres where the
// category asks for them, description, project, cost centre, receipt) and a table when it is read-only. After a save each line shows what the server worked out: the amount
// in the claim's currency with the rate it was converted at, and the policy flags as words with an icon (never colour alone).
import { parseDate } from '@internationalized/date';
import { Plus, Trash2 } from 'lucide-react';
import { Button, DateField, IconButton, ScrollRegion, SelectField, TextField } from '@erp/ui';
import { formatDate, formatDecimal, useLocaleSettings, useText } from '@erp/i18n';
import type { Category, ClaimLine } from '../../api/expenses';
import type { ProofFile } from '../../api/attendance-requests';
import { MAX_LINES, emptyLine, needsQuantity, type LineDraft, type LineErrors } from './expense-model';
import { Flags, Money, useCategoryName } from './expense-parts';
import { ReceiptLink } from './expense-receipts';
import { nameOf } from '../../api/workflow';
import './expenses-messages';

interface EditorProps {
  lines: LineDraft[];
  onChange: (lines: LineDraft[]) => void;
  categories: Category[];
  claimCurrency: string;
  today: string;
  /** What the server worked out for the saved lines, by line id. */
  saved: Map<string, ClaimLine>;
  receipts: ProofFile[];
  /** Errors by line key (this form's own checks and the server's answers, already in words). */
  errors: Map<string, Partial<Record<keyof LineErrors | 'receipt' | 'line', string>>>;
}

export function LinesEditor({ lines, onChange, categories, claimCurrency, today, saved, receipts, errors }: EditorProps) {
  const { t } = useText();
  const { locale, timeZone, numbers } = useLocaleSettings();
  const day = (d: string) => formatDate(`${d}T12:00:00Z`, locale === 'ur' ? 'ur' : 'en', timeZone, numbers);
  const categoryName = useCategoryName();
  const byId = new Map(categories.map((c) => [c.id, c]));
  const patch = (key: string, change: Partial<LineDraft>) => onChange(lines.map((l) => (l.key === key ? { ...l, ...change } : l)));
  return (
    <section aria-labelledby="erp-exp-lines" className="erp-stack">
      <h2 id="erp-exp-lines">{t('exp.lines')}</h2>
      {lines.length === 0 && <p className="erp-muted">{t('exp.lines.empty')}</p>}
      <ol className="erp-exp-lines">
        {lines.map((line, i) => {
          const category = byId.get(line.categoryId);
          const result = line.id ? saved.get(line.id) : undefined;
          const error = errors.get(line.key) ?? {};
          const converted = result && result.amount !== null && result.originalCurrencyCode !== claimCurrency;
          return (
            <li key={line.key}>
              <fieldset className="erp-exp-line" aria-label={t('exp.line.n', { n: i + 1 })}>
                <legend className="erp-label">{t('exp.line.n', { n: i + 1 })}</legend>
                <div className="erp-exp-fields">
                  <SelectField label={t('exp.line.category')} isRequired options={categories.map((c) => ({ id: c.id, label: categoryName(c), detail: c.code }))} value={line.categoryId || null}
                    onChange={(id) => patch(line.key, { categoryId: id })} error={error.category ?? null} />
                  <DateField label={t('exp.line.date')} isRequired value={line.expenseDate ? parseDate(line.expenseDate) : null} onChange={(v) => patch(line.key, { expenseDate: v ? v.toString() : '' })} error={error.date ?? null} />
                  <TextField label={t('exp.line.amount')} isRequired isCode inputMode="text" maxLength={20} value={line.amount} onChange={(v) => patch(line.key, { amount: v })} error={error.amount ?? null} />
                  <TextField label={t('exp.line.currency')} isRequired isCode maxLength={3} value={line.currencyCode} onChange={(v) => patch(line.key, { currencyCode: v.toUpperCase() })} error={error.currency ?? null}
                    help={line.currencyCode && line.currencyCode !== claimCurrency ? t('exp.line.currency.converted', { currency: claimCurrency }) : undefined} />
                  {needsQuantity(category) && (
                    <TextField label={t(category?.kind === 'per_diem' ? 'exp.line.days' : 'exp.line.km')} isRequired isCode inputMode="text" maxLength={14} value={line.quantity}
                      onChange={(v) => patch(line.key, { quantity: v })} error={error.quantity ?? null} />)}
                  <SelectField label={t('exp.line.receipt')} options={[{ id: '', label: t('exp.line.receipt.none') }, ...receipts.map((f) => ({ id: f.id, label: f.fileName }))]} value={line.receiptFileId}
                    onChange={(id) => patch(line.key, { receiptFileId: id })} error={error.receipt ?? null}
                    help={category?.requiresReceipt ? t('exp.line.receipt.required') : undefined} />
                  <TextField label={t('exp.line.project')} maxLength={80} value={line.project} onChange={(v) => patch(line.key, { project: v })} />
                  <TextField label={t('exp.line.costCentre')} maxLength={80} value={line.costCentre} onChange={(v) => patch(line.key, { costCentre: v })} />
                  <div className="erp-exp-wide"><TextField label={t('exp.line.description')} maxLength={500} value={line.description} onChange={(v) => patch(line.key, { description: v })} /></div>
                </div>
                {error.line && <p role="alert" className="erp-field-error">{error.line}</p>}
                {result && (
                  <div className="erp-stack" aria-live="polite">
                    {converted && <p className="erp-small">{t('exp.line.convertedAs', { rate: result.exchangeRate ? formatDecimal(result.exchangeRate, numbers) : '', date: result.rateDate ? day(result.rateDate) : '' })} <Money value={result.amount} currency={claimCurrency} /></p>}
                    <Flags flags={result.flags} blocksSubmit={result.blocksSubmit} />
                  </div>)}
                <div className="erp-row">
                  <IconButton label={t('exp.line.remove', { n: i + 1 })} icon={<Trash2 size={16} aria-hidden="true" />} onPress={() => onChange(lines.filter((l) => l.key !== line.key))} />
                  <span className="erp-small erp-muted">{t('exp.line.remove.label')}</span>
                </div>
              </fieldset>
            </li>);
        })}
      </ol>
      <div><Button variant="secondary" icon={<Plus size={16} aria-hidden="true" />} isDisabled={lines.length >= MAX_LINES} onPress={() => onChange([...lines, emptyLine(claimCurrency, today)])}>{t('exp.line.add')}</Button></div>
    </section>
  );
}

/** The lines of a claim that is not a draft (or that the person only reads): amounts, rate, flags, the approver's decision. */
export function LinesTable({ claimId, lines, currency, showDecision }: { claimId: string; lines: ClaimLine[]; currency: string; showDecision: boolean }) {
  const { t } = useText();
  const { locale, timeZone, numbers } = useLocaleSettings();
  const lang = locale === 'ur' ? 'ur' : 'en';
  const day = (d: string) => formatDate(`${d}T12:00:00Z`, lang, timeZone, numbers);
  return (
    <section aria-labelledby="erp-exp-lines" className="erp-stack">
      <h2 id="erp-exp-lines">{t('exp.lines')}</h2>
      {lines.length === 0 ? <p className="erp-muted">{t('exp.lines.empty')}</p> : (
        <ScrollRegion label={t('exp.lines')}>
          <table className="erp-exp-table">
            <caption className="erp-visually-hidden">{t('exp.lines')}</caption>
            <thead>
              <tr>
                <th scope="col">{t('exp.line.category')}</th><th scope="col">{t('exp.line.date')}</th><th scope="col">{t('exp.col.claimed')}</th><th scope="col">{t('exp.col.inCurrency', { currency })}</th>
                <th scope="col">{t('exp.col.details')}</th><th scope="col">{t('exp.line.receipt')}</th><th scope="col">{t('exp.col.flags')}</th>
                {showDecision && <th scope="col">{t('exp.col.approved')}</th>}
              </tr>
            </thead>
            <tbody>
              {lines.map((l) => {
                const converted = l.originalCurrencyCode !== currency;
                return (
                  <tr key={l.id}>
                    <th scope="row"><bdi>{nameOf(l.categoryName, lang) || l.categoryCode}</bdi></th>
                    <td><bdi>{day(l.expenseDate)}</bdi></td>
                    <td><Money value={l.originalAmount} currency={l.originalCurrencyCode} /></td>
                    <td>
                      <Money value={l.amount} currency={currency} />
                      {converted && l.exchangeRate && <div className="erp-small erp-muted">{t('exp.line.rate', { rate: formatDecimal(l.exchangeRate, numbers), from: l.originalCurrencyCode, to: currency, date: l.rateDate ? day(l.rateDate) : '' })}</div>}
                    </td>
                    <td>
                      {l.quantity && l.quantity !== '0' && <div>{t('exp.line.quantity', { value: formatDecimal(l.quantity.replace(/\.?0+$/, '') || '0', numbers) })}</div>}
                      {l.description && <div><bdi>{l.description}</bdi></div>}
                      {(l.project || l.costCentre) && <div className="erp-small erp-muted"><bdi>{[l.project, l.costCentre].filter(Boolean).join(' / ')}</bdi></div>}
                    </td>
                    <td><ReceiptLink claimId={claimId} fileId={l.receiptFileId} /></td>
                    <td><Flags flags={l.flags} blocksSubmit={l.blocksSubmit} />{l.flags.length === 0 && <span className="erp-muted">{t('exp.flags.none')}</span>}</td>
                    {showDecision && (
                      <td>
                        {l.decidedAt === null ? <span className="erp-muted">{t('exp.decision.open')}</span> : (
                          <>
                            <Money value={l.approvedAmount} currency={currency} />
                            {l.approvedAmount !== null && /^0(\.0+)?$/.test(l.approvedAmount) && <div>{t('exp.decision.rejected')}</div>}
                            {l.decisionReason && <div className="erp-small"><bdi>{l.decisionReason}</bdi></div>}
                          </>)}
                      </td>)}
                  </tr>);
              })}
            </tbody>
          </table>
        </ScrollRegion>)}
    </section>
  );
}
