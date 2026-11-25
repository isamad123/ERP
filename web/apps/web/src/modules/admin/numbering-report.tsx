// The void report of one numbering scheme (slice S1.15c Build item 9; FR-KRN-NUM-05, FR-XM-06): the numbers issued by period, and the numbers voided with
// their reasons. A void number keeps its place in the series and is never used again.
import { useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { parseDate, type CalendarDate } from '@internationalized/date';
import { Banner, Button, DateField, Dialog, EmptyState, ErrorPanel, Skeleton, ScrollRegion } from '@erp/ui';
import { formatDateTime, useLocaleSettings, useText } from '@erp/i18n';
import { ApiProblem } from '../../api/client';
import { numberingApi, type SchemeRow } from '../../api/numbering';
import './numbering-messages';

export function VoidReportDialog({ scheme, onClose }: { scheme: SchemeRow; onClose: () => void }) {
  const { t } = useText();
  const { locale, timeZone, numbers } = useLocaleSettings();
  const [from, setFrom] = useState<CalendarDate | null>(null);
  const [to, setTo] = useState<CalendarDate | null>(null);
  const [asked, setAsked] = useState<{ from?: string; to?: string }>({});
  const rangeError = from && to && from.compare(to) > 0 ? t('num.report.rangeError') : null;
  const report = useQuery({ queryKey: ['cfg', 'numbering', 'report', scheme.id, asked.from ?? '', asked.to ?? ''], queryFn: () => numberingApi.voidReport(scheme.id, asked.from, asked.to) });
  const problem = report.error instanceof ApiProblem ? report.error : null;
  const apply = () => { if (!rangeError) setAsked({ ...(from ? { from: from.toString() } : {}), ...(to ? { to: to.toString() } : {}) }); };
  return (
    <Dialog title={t('num.report.title', { pattern: scheme.pattern })} isOpen onOpenChange={(open) => { if (!open) onClose(); }}
      actions={() => <Button variant="secondary" onPress={onClose}>{t('num.close')}</Button>}>
      <div className="erp-stack">
        <div className="erp-row erp-wrap">
          <DateField label={t('num.report.from')} value={from} onChange={(v) => setFrom(v ? parseDate(v.toString()) : null)} />
          <DateField label={t('num.report.to')} value={to} onChange={(v) => setTo(v ? parseDate(v.toString()) : null)} error={rangeError} />
          <Button variant="secondary" onPress={apply}>{t('num.report.show')}</Button>
        </div>
        {report.isPending && <Skeleton lines={4} />}
        {report.isError && <ErrorPanel message={problem?.status === 403 ? t('num.report.denied') : t('num.report.loadError')} reference={problem?.reference ?? null} action={<Button variant="primary" onPress={() => { void report.refetch(); }}>{t('common.retry')}</Button>} />}
        {report.isSuccess && (
          <>
            <h3 className="erp-h3">{t('num.report.issued')}</h3>
            {report.data.issued.length === 0 ? <EmptyState level={2} title={t('num.report.issued.none')} /> : (
              <ScrollRegion className="erp-table-scroll" label={t('num.report.issued')}>
                <table className="erp-table" aria-label={t('num.report.issued')}>
                  <thead><tr>
                    <th scope="col">{t('num.report.col.period')}</th><th scope="col">{t('num.report.col.first')}</th><th scope="col">{t('num.report.col.last')}</th>
                    {report.data.gapFree && <th scope="col">{t('num.report.col.count')}</th>}
                  </tr></thead>
                  <tbody>
                    {report.data.issued.map((r) => (
                      <tr key={r.period}>
                        <th scope="row"><bdi>{r.period === 'all' ? t('num.report.period.all') : r.period}</bdi></th>
                        <td><span className="erp-code"><bdi>{r.firstNumber}</bdi></span></td>
                        <td><span className="erp-code"><bdi>{r.lastNumber}</bdi></span></td>
                        {report.data.gapFree && <td>{r.count ?? ''}</td>}
                      </tr>
                    ))}
                  </tbody>
                </table>
              </ScrollRegion>
            )}
            <h3 className="erp-h3">{t('num.report.voids')}</h3>
            <Banner tone="info">{t('num.report.gapNote')}</Banner>
            {report.data.voids.length === 0 ? <EmptyState level={2} title={t('num.report.voids.none')} /> : (
              <ScrollRegion className="erp-table-scroll" label={t('num.report.voids')}>
                <table className="erp-table" aria-label={t('num.report.voids')}>
                  <thead><tr><th scope="col">{t('num.report.col.number')}</th><th scope="col">{t('num.report.col.reason')}</th><th scope="col">{t('num.report.col.when')}</th></tr></thead>
                  <tbody>
                    {report.data.voids.map((v) => (
                      <tr key={v.id}>
                        <th scope="row"><span className="erp-code"><bdi>{v.number}</bdi></span></th>
                        <td>{v.reason}</td>
                        <td>{formatDateTime(v.voidedAt, locale === 'ur' ? 'ur' : 'en', timeZone, numbers)}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </ScrollRegion>
            )}
          </>
        )}
      </div>
    </Dialog>
  );
}
