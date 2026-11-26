// The loan reports (slice S1.41 Build items 5 and 6; FR-HRM-LON-05, LON-07, GHR-06): the outstanding loans in the caller's data scope and the reconciliation of the loan ledger with the
// schedule per company and currency (D-43: no Finance ledger yet, so this report and the outbox events are the feed; Finance's own account is compared once Finance is in use).
// Amounts are salary-class data: a person without that field class sees the counts and the next installments only.
import { useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { useDocumentTitle } from '@erp/engine';
import { Banner, Button, EmptyState, ErrorPanel, Link, PageHeader, ScrollRegion, SelectField, Skeleton, Tabs } from '@erp/ui';
import { formatDate, formatInteger, formatMoney, useLocaleSettings, useText, type MessageKey } from '@erp/i18n';
import { loansApi, type OutstandingRow, type ReconciliationCompany } from '../../api/loans';
import { ApiProblem } from '../../api/client';
import { useShell } from '../../shell/context';
import './loans-messages';
import './loans.css';

const PAGE = 50;

export default function LoanReportsScreen() {
  const { t } = useText();
  const { can, me, activeCompany } = useShell();
  useDocumentTitle(t('loan.rep.title'));
  const allowed = can('hrm.loan_reports.read');
  const [company, setCompany] = useState(activeCompany?.id ?? me.defaultCompanyId ?? me.companies[0]?.id ?? '');
  const [tab, setTab] = useState('outstanding');
  if (!allowed) return <div className="erp-page"><PageHeader title={t('loan.rep.title')} /><EmptyState title={t('loan.rep.denied')} /></div>;
  return (
    <div className="erp-page">
      <PageHeader title={t('loan.rep.title')} />
      <div className="erp-stack">
        {me.companies.length > 1 && <SelectField label={t('loan.rep.company')} options={me.companies.map((c) => ({ id: c.id, label: c.name.en ?? c.code }))} value={company} onChange={setCompany} />}
        <Tabs label={t('loan.rep.tabs')} selectedKey={tab} onSelectionChange={setTab} tabs={[
          { id: 'outstanding', title: t('loan.rep.outstanding'), content: <Outstanding company={company} /> },
          { id: 'reconciliation', title: t('loan.rep.reconciliation'), content: <Reconciliation company={company} /> },
        ]} />
      </div>
    </div>
  );
}

function useFormatters() {
  const { numbers, locale, timeZone } = useLocaleSettings();
  const lang = locale === 'ur' ? 'ur' : 'en';
  return {
    day: (iso: string | null) => (iso ? formatDate(`${iso}T12:00:00Z`, lang, timeZone, numbers) : ''),
    count: (n: number) => formatInteger(n, numbers),
    money: (v: number | string | undefined, currency: string) => (v === undefined ? '' : formatMoney(String(v), currency, lang, numbers)),
  };
}

function Outstanding({ company }: { company: string }) {
  const { t } = useText();
  const f = useFormatters();
  const [offset, setOffset] = useState(0);
  const report = useQuery({ queryKey: ['hrm', 'loan-reports', 'outstanding', company, offset], queryFn: ({ signal }) => loansApi.outstanding(company, { limit: PAGE, offset }, signal), enabled: !!company, retry: false });
  if (report.isPending) return <Skeleton lines={5} />;
  if (report.isError) return <ErrorPanel message={report.error instanceof ApiProblem && report.error.status === 403 ? t('loan.rep.denied') : t('loan.error.generic')} />;
  const { items, total } = report.data;
  if (total === 0) return <EmptyState title={t('loan.rep.empty')} />;
  const hidden = items.every((i: OutstandingRow) => i.balance === undefined);
  return (
    <div className="erp-stack">
      {hidden && <Banner tone="info">{t('loan.amounts.hidden')}</Banner>}
      <ScrollRegion label={t('loan.rep.outstanding')}>
        <table className="erp-loan-table">
          <caption className="erp-visually-hidden">{t('loan.rep.outstanding')}</caption>
          <thead><tr>
            <th scope="col">{t('loan.rep.col.loan')}</th><th scope="col">{t('loan.rep.col.employee')}</th><th scope="col">{t('loan.rep.col.type')}</th><th scope="col">{t('loan.col.status')}</th>
            {!hidden && <><th scope="col" className="erp-loan-num">{t('loan.rep.col.principal')}</th><th scope="col" className="erp-loan-num">{t('loan.rep.col.balance')}</th><th scope="col" className="erp-loan-num">{t('loan.rep.col.repayable')}</th></>}
            <th scope="col" className="erp-loan-num">{t('loan.rep.col.left')}</th><th scope="col">{t('loan.rep.col.next')}</th>
          </tr></thead>
          <tbody>
            {items.map((r) => (
              <tr key={r.loanId}>
                <td><Link href={`/payroll/loans/${r.loanId}`}><bdi>{r.loanNo}</bdi></Link></td>
                <td><bdi>{r.employeeNo}</bdi></td><td><bdi>{r.typeCode}</bdi></td><td>{t(`loan.status.${r.status}` as MessageKey)}</td>
                {!hidden && <><td className="erp-loan-num">{f.money(r.principal, r.currencyCode)}</td><td className="erp-loan-num">{f.money(r.balance, r.currencyCode)}</td><td className="erp-loan-num">{f.money(r.repayable, r.currencyCode)}</td></>}
                <td className="erp-loan-num">{f.count(r.installmentsLeft)}</td><td>{f.day(r.nextDue)}</td>
              </tr>))}
          </tbody>
        </table>
      </ScrollRegion>
      <div className="erp-row" role="navigation" aria-label={t('loan.rep.tabs')}>
        <Button variant="quiet" isDisabled={offset === 0} onPress={() => setOffset(Math.max(0, offset - PAGE))}>{t('loan.rep.prev')}</Button>
        <span role="status">{t('loan.rep.paging', { from: f.count(offset + 1), to: f.count(offset + items.length), total: f.count(total) })}</span>
        <Button variant="quiet" isDisabled={offset + items.length >= total} onPress={() => setOffset(offset + PAGE)}>{t('loan.rep.next')}</Button>
      </div>
    </div>
  );
}

function Reconciliation({ company }: { company: string }) {
  const { t } = useText();
  const f = useFormatters();
  const report = useQuery({ queryKey: ['hrm', 'loan-reports', 'reconciliation', company], queryFn: ({ signal }) => loansApi.reconciliation(company, signal), enabled: !!company, retry: false });
  if (report.isPending) return <Skeleton lines={5} />;
  if (report.isError) return <ErrorPanel message={t('loan.error.generic')} />;
  if (report.data.companies.length === 0) return <EmptyState title={t('loan.rep.rec.none')} />;
  return (
    <div className="erp-stack">
      <p className="erp-muted">{t('loan.rep.rec.note')}</p>
      {report.data.companies.map((c: ReconciliationCompany) => (
        <section key={`${c.companyId}-${c.currencyCode}`} className="erp-stack" aria-label={c.currencyCode}>
          <Banner tone={c.reconciles ? 'success' : 'danger'}>{c.reconciles ? t('loan.rep.rec.ok') : t('loan.rep.rec.bad', { count: f.count(c.differenceCount) })}</Banner>
          <dl className="erp-loan-facts">
            <dt>{t('loan.rep.rec.loans')}</dt><dd>{f.count(c.loans)}</dd>
            {c.disbursed !== undefined && <>
              <dt>{t('loan.rep.rec.disbursed')}</dt><dd>{f.money(c.disbursed, c.currencyCode)}</dd>
              <dt>{t('loan.rep.rec.recovered')}</dt><dd>{f.money(c.recovered, c.currencyCode)}</dd>
              <dt>{t('loan.rep.rec.expected')}</dt><dd>{f.money(c.expectedOutstanding, c.currencyCode)}</dd>
              <dt>{t('loan.rep.rec.outstanding')}</dt><dd><strong>{f.money(c.outstanding, c.currencyCode)}</strong></dd>
              <dt>{t('loan.rep.rec.scheduled')}</dt><dd>{f.money(c.scheduledPrincipalOutstanding, c.currencyCode)}</dd>
            </>}
          </dl>
          {c.differences && c.differences.length > 0 && (
            <ul>{c.differences.map((d) => <li key={d.loanId}><Link href={`/payroll/loans/${d.loanId}`}><bdi>{d.loanNo}</bdi></Link>: {t('loan.rep.rec.diff')} {f.money(d.difference, c.currencyCode)}</li>)}</ul>)}
        </section>))}
    </div>
  );
}
