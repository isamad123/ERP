// One leave type and leave year of the ledger screen (slice S1.33b; FR-HRM-LVE-10, FR-XM-05, -06): the balance the server returns, the entries with their date, kind, signed quantity, running
// balance, expiry, reason, the server's explanation, the source (a request opens the request) and the "reversed by / reverses" markers, and the Reverse button. A warning appears when the
// balance worked out here from the entries is not the one the server returned.
import { Banner, Button, Link, ScrollRegion } from '@erp/ui';
import { digitsOf, formatDate, formatDecimal, useLocaleSettings } from '@erp/i18n';
import type { BalanceLine, LedgerEntry } from '../../api/leave';
import { TypeLabel } from './leave-parts';
import { checkBalance, decimalText, isReversible, matches, sourceOf, withRunning, type LedgerFilter } from './leave-ledger-model';
import { useLedgerText } from './leave-ledger-messages';
import './leave.css';

export function LineSection({ line, name, token, filter, canReverse, onReverse }: {
  line: BalanceLine; name: string; token: string | null | undefined; filter: LedgerFilter; canReverse: boolean; onReverse: (entry: LedgerEntry) => void;
}) {
  const { t, server } = useLedgerText();
  const { locale, timeZone, numbers } = useLocaleSettings();
  const lang = locale === 'ur' ? 'ur' : 'en';
  const day = (d: string) => formatDate(`${d}T12:00:00Z`, lang, timeZone, numbers);
  const unit = line.unit === 'hour' ? t('lled.unit.hours') : t('lled.unit.days');
  const num = (v: number | string) => formatDecimal(decimalText(v), numbers);
  const check = checkBalance(line.entries, line.balance);
  const rows = withRunning(line.entries);
  const shown = rows.filter((r) => matches(r.entry, filter));
  const byId = new Map(line.entries.map((e) => [e.id, e]));
  const heading = `erp-lled-line-${line.leaveTypeId}`;
  return (
    <section aria-labelledby={heading} className="erp-stack">
      <div className="erp-row erp-wrap">
        <h3 id={heading}><TypeLabel name={name} token={token} /></h3>
        <span className="erp-muted">{t('lled.line.year', { year: digitsOf(String(line.leaveYear), numbers.digits), from: day(line.yearStart), to: day(line.yearEnd) })}</span>
      </div>
      <p>
        <span className="erp-muted">{t('lled.line.balance')} </span>
        <strong className="erp-leave-balance" aria-label={t('lled.line.balance.aria', { name, value: `${num(line.balance)} ${unit}` })}><bdi dir="ltr">{num(line.balance)}</bdi></strong> {unit}
      </p>
      {!check.matches && <Banner tone="warning" title={t('lled.mismatch.title')}>{t('lled.mismatch.body', { screen: formatDecimal(check.computed, numbers), server: formatDecimal(check.server, numbers) })}</Banner>}
      {line.noPolicy && <p className="erp-muted">{server(line.noPolicy.message) ?? t('lled.noPolicy')}</p>}
      {shown.length === 0 ? <p className="erp-muted">{t(rows.length === 0 ? 'lled.entries.none' : 'lled.entries.filtered')}</p> : (
        <ScrollRegion className="erp-leave-scroll erp-table-scroll" label={t('lled.entries.caption', { name })}>
          <table className="erp-leave-table">
            <caption className="erp-visually-hidden">{t('lled.entries.caption', { name })}</caption>
            <thead><tr>
              <th scope="col">{t('lled.col.date')}</th><th scope="col">{t('lled.col.what')}</th><th scope="col">{t('lled.col.quantity')}</th><th scope="col">{t('lled.col.running')}</th>
              <th scope="col">{t('lled.col.expires')}</th><th scope="col">{t('lled.col.note')}</th><th scope="col">{t('lled.col.source')}</th><th scope="col">{t('lled.col.actions')}</th>
            </tr></thead>
            <tbody>
              {shown.map(({ entry: e, running }) => {
                const source = sourceOf(e);
                const reversedBy = e.reversedById ? byId.get(e.reversedById) : undefined;
                const reverses = e.reversesEntryId ? byId.get(e.reversesEntryId) : undefined;
                return (
                  <tr key={e.id} data-entry={e.id}>
                    <td><bdi>{day(e.entryDate)}</bdi></td>
                    <td>{server(e.explanation) ?? t(`lled.kind.${e.kind}` as 'lled.kind.grant')}</td>
                    <td><bdi dir="ltr">{e.quantity > 0 ? '+' : ''}{num(e.quantity)}</bdi></td>
                    <td><bdi dir="ltr">{num(running)}</bdi></td>
                    <td>{e.expiresOn ? <bdi>{day(e.expiresOn)}</bdi> : null}</td>
                    <td>
                      {[e.reason, e.period].filter(Boolean).join(' · ')}
                      {e.reversedById && <span className="erp-chip" data-tone="warning">{reversedBy ? t('lled.reversedBy', { date: day(reversedBy.entryDate) }) : t('lled.reversedBy.unknown')}</span>}
                      {e.reversesEntryId && <span className="erp-chip" data-tone="info">{reverses ? t('lled.reverses', { date: day(reverses.entryDate) }) : t('lled.reverses.unknown')}</span>}
                    </td>
                    <td>{source.type === 'request' ? <Link href={source.href}>{t('lled.source.openRequest')}</Link> : t(`lled.source.${source.key}`)}</td>
                    <td>{canReverse && isReversible(e) ? <Button variant="quiet" onPress={() => onReverse(e)}>{t('lled.reverse.open')}</Button> : null}</td>
                  </tr>);
              })}
            </tbody>
          </table>
        </ScrollRegion>)}
    </section>
  );
}
