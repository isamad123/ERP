// The balance check of the leave ledger screen (slice S1.33b; FR-HRM-LVE-14): the ledger worked out again from the policies by the server, every difference with its period and cause, and a
// button that opens the post form filled in with the difference. Nothing is ever posted by itself: the check changes nothing, and HR posts an adjustment (R-148).
import { useMutation } from '@tanstack/react-query';
import type { LeaveType } from '@erp/generated/api/hrm';
import { Banner, Button, EmptyState, ScrollRegion } from '@erp/ui';
import { digitsOf, formatDate, formatDecimal, useLocaleSettings } from '@erp/i18n';
import { leaveLedgerApi, type RecomputeDifference } from '../../api/leave-ledger';
import { useShell } from '../../shell/context';
import { typeNameOf, useLeaveRefusal } from './leave-parts';
import { adjustmentFor, decimalText } from './leave-ledger-model';
import { useLedgerText } from './leave-ledger-messages';
import type { PostPrefill } from './leave-ledger-forms';
import './leave.css';

export function RecomputePanel({ employmentId, types, onAdjust }: { employmentId: string; types: LeaveType[]; onAdjust: (prefill: PostPrefill) => void }) {
  const { t, server } = useLedgerText();
  const { can, writable } = useShell();
  const { locale, timeZone, numbers } = useLocaleSettings();
  const lang = locale === 'ur' ? 'ur' : 'en';
  const refusal = useLeaveRefusal();
  const check = useMutation({ mutationFn: () => leaveLedgerApi.recompute(employmentId) });
  const num = (v: number | null) => (v === null ? t('lled.recompute.none') : formatDecimal(decimalText(v), numbers));
  const typeName = (d: RecomputeDifference) => typeNameOf(types.find((x) => x.id === d.leaveTypeId), locale) || d.leaveTypeCode;
  const answer = check.data;
  return (
    <section aria-labelledby="erp-lled-recompute" className="erp-stack">
      <h2 id="erp-lled-recompute">{t('lled.recompute.title')}</h2>
      <p className="erp-muted">{t('lled.recompute.body')}</p>
      <div><Button variant="secondary" isPending={check.isPending} onPress={() => check.mutate()}>{t('lled.recompute.run')}</Button></div>
      {check.isError && <Banner tone="danger">{refusal(check.error)}</Banner>}
      {answer && (
        <>
          <Banner tone="info" title={t('lled.recompute.unchanged')}>{t('lled.recompute.unchanged.body')}</Banner>
          {answer.differences.length === 0 ? (
            <EmptyState title={t('lled.recompute.clean')}>{t('lled.recompute.clean.body', { count: answer.checked, date: formatDate(`${answer.asOf}T12:00:00Z`, lang, timeZone, numbers) })}</EmptyState>
          ) : (
            <ScrollRegion className="erp-leave-scroll erp-table-scroll" label={t('lled.recompute.caption')}>
              <table className="erp-leave-table">
                <caption className="erp-visually-hidden">{t('lled.recompute.caption')}</caption>
                <thead><tr>
                  <th scope="col">{t('lled.col.type')}</th><th scope="col">{t('lled.recompute.col.year')}</th><th scope="col">{t('lled.recompute.col.period')}</th><th scope="col">{t('lled.recompute.col.cause')}</th>
                  <th scope="col">{t('lled.recompute.col.expected')}</th><th scope="col">{t('lled.recompute.col.posted')}</th><th scope="col">{t('lled.col.actions')}</th>
                </tr></thead>
                <tbody>
                  {answer.differences.map((d, i) => {
                    const suggestion = adjustmentFor(d);
                    return (
                      <tr key={`${d.leaveTypeId}-${d.leaveYear}-${d.period ?? ''}-${d.kind}-${i}`}>
                        <td><bdi>{typeName(d)}</bdi></td>
                        <td><bdi>{digitsOf(String(d.leaveYear), numbers.digits)}</bdi></td>
                        <td>{d.period ? <bdi dir="ltr">{d.period}</bdi> : t('lled.recompute.wholeYear')}</td>
                        <td>{server(d.explanation) ?? t('lled.recompute.unknownCause')}</td>
                        <td><bdi dir="ltr">{num(d.expected)}</bdi></td>
                        <td><bdi dir="ltr">{num(d.posted)}</bdi></td>
                        <td>
                          {suggestion && can('hrm.leave_ledger.post')
                            ? <Button variant="secondary" isDisabled={!writable} onPress={() => onAdjust({ leaveTypeId: suggestion.leaveTypeId, quantity: suggestion.quantity, settlesPeriod: suggestion.settlesPeriod, entryDate: suggestion.entryDate })}>{t('lled.recompute.adjust')}</Button>
                            : <span className="erp-muted">{suggestion ? t('lled.recompute.cannotPost') : t('lled.recompute.noSuggestion')}</span>}
                        </td>
                      </tr>);
                  })}
                </tbody>
              </table>
            </ScrollRegion>)}
        </>)}
    </section>
  );
}
