// The licence and usage page (slice S1.16c Build item 12; FR-ADM-OPS-07, FR-KRN-ENT-05, FR-KRN-ENT-06): edition, modules, limits with
// consumption and warnings, term and maintenance dates, the state with what it means and the next state, and the monthly usage
// statements. Only holders of `cfg.licence.read` see it (the server refuses everyone else, so seat numbers never reach them). It
// still answers when the licence is locked. Uploading a licence file is part of the on-premises package (1.1, ruling Q10).
import { useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { Banner, Button, EmptyState, Link, PageHeader, StatusChip, TextField, type Status } from '@erp/ui';
import { useDocumentTitle } from '@erp/engine';
import { useText, type MessageKey } from '@erp/i18n';
import { opsApi, type Licence, type LimitUse } from '../../api/ops';
import { useShell } from '../../shell/context';
import { Loaded, useFormats } from './ops-parts';

const STATE_CHIP: Record<string, Status> = { active: 'active', warning: 'pending', grace: 'pending', read_only: 'on_hold', locked: 'blocked' };
const LIMIT_NAMES: Record<string, MessageKey> = {
  'users.named': 'licence.limit.usersNamed',
  'users.concurrent': 'licence.limit.usersConcurrent',
  companies: 'licence.limit.companies',
  'employees.active': 'licence.limit.employees',
  'storage.gb': 'licence.limit.storage',
};
/** The server's period `202611` as `2026-11`. */
const month = (period: string) => (/^\d{6}$/.test(period) ? `${period.slice(0, 4)}-${period.slice(4)}` : period);
const MONTH = /^\d{4}-(0[1-9]|1[0-2])$/;

export default function LicenceScreen() {
  const { t } = useText();
  const { can } = useShell();
  useDocumentTitle(t('lic.title'));
  const allowed = can('cfg.licence.read');
  const query = useQuery({ queryKey: ['lic'], queryFn: opsApi.licence, refetchOnMount: 'always', enabled: allowed });
  return (
    <div className="erp-page">
      <PageHeader title={t('lic.title')} description={t('lic.description')} />
      {!allowed ? <Banner tone="danger">{t('ops.denied')}</Banner> : (
        <Loaded query={query}>
          {(licence) => (
            <>
              <State licence={licence} />
              <Plan licence={licence} />
              <Limits limits={licence.limits} />
              <Usage />
              <p className="erp-small erp-muted">{t('lic.upload')}</p>
            </>
          )}
        </Loaded>
      )}
    </div>
  );
}

function State({ licence }: { licence: Licence }) {
  const { t } = useText();
  const { can } = useShell();
  const f = useFormats();
  const known = ['active', 'warning', 'grace', 'read_only', 'locked'].includes(licence.state);
  const state = (key: string) => (['active', 'warning', 'grace', 'read_only', 'locked'].includes(key) ? t(`lic.state.${key}` as MessageKey) : key);
  return (
    <section className="erp-stack" aria-labelledby="lic-state">
      <h2 id="lic-state" className="erp-h3">{t('lic.state')}</h2>
      <div className="erp-row erp-wrap"><StatusChip status={STATE_CHIP[licence.state] ?? 'info'} label={state(licence.state)} /></div>
      {known && <p>{t(`lic.state.${licence.state}.body` as MessageKey)}</p>}
      <p className="erp-muted">{licence.nextState && licence.nextStateOn ? t('lic.next', { state: state(licence.nextState), date: f.day(licence.nextStateOn) }) : t('lic.next.none')}</p>
      {licence.scheduledFrom && <Banner tone="info" title={t('lic.title')}>{t('lic.scheduled', { date: f.day(licence.scheduledFrom) })}</Banner>}
      {(licence.state === 'read_only' || licence.state === 'locked') && can('aud.export') && <div><Link href="/admin/exports">{t('lic.exports')}</Link></div>}
    </section>
  );
}

function Plan({ licence }: { licence: Licence }) {
  const { t } = useText();
  const f = useFormats();
  const list = (label: string, items: string[]) => items.length === 0 ? null : (
    <div><dt className="erp-small erp-muted">{label}</dt><dd><bdi dir="ltr">{items.join(', ')}</bdi></dd></div>
  );
  return (
    <section className="erp-stack" aria-labelledby="lic-plan">
      <h2 id="lic-plan" className="erp-h3">{t('lic.plan')}</h2>
      <dl className="erp-stack">
        <div><dt className="erp-small erp-muted">{t('lic.edition')}</dt><dd><bdi>{licence.edition ?? '–'}</bdi></dd></div>
        <div><dt className="erp-small erp-muted">{t('lic.deployment')}</dt><dd><bdi>{licence.deployment}</bdi></dd></div>
        {licence.version !== null && <div><dt className="erp-small erp-muted">{t('lic.version')}</dt><dd>{f.int(licence.version)}</dd></div>}
        {licence.supportTier && <div><dt className="erp-small erp-muted">{t('lic.support')}</dt><dd><bdi>{licence.supportTier}</bdi></dd></div>}
        <div><dt className="erp-small erp-muted">{t('lic.from')}</dt><dd>{f.day(licence.effectiveFrom)}</dd></div>
        <div><dt className="erp-small erp-muted">{t('lic.until')}</dt><dd>{licence.expiresAt ? f.day(licence.expiresAt) : t('lic.until.none')}</dd></div>
        {licence.maintenanceExpiresAt && <div><dt className="erp-small erp-muted">{t('lic.maintenance')}</dt><dd>{f.day(licence.maintenanceExpiresAt)}</dd></div>}
        {list(t('lic.modules'), licence.modules)}
        {list(t('lic.features'), licence.features)}
        {list(t('lic.packs'), licence.packs)}
        {list(t('lic.addons'), licence.addons)}
      </dl>
    </section>
  );
}

export function useLimitName() {
  const { t } = useText();
  return (key: string) => (LIMIT_NAMES[key] ? t(LIMIT_NAMES[key] as MessageKey) : key);
}

function Limits({ limits }: { limits: LimitUse[] }) {
  const { t } = useText();
  const f = useFormats();
  const name = useLimitName();
  return (
    <section className="erp-stack" aria-labelledby="lic-limits">
      <h2 id="lic-limits" className="erp-h3">{t('lic.limits')}</h2>
      {limits.length === 0 ? <p className="erp-muted">{t('lic.limits.empty')}</p> : (
        <table className="erp-table">
          <caption className="erp-visually-hidden">{t('lic.limits')}</caption>
          <thead><tr><th scope="col">{t('lic.usage.meter')}</th><th scope="col">{t('lic.usage.value')}</th><th scope="col">{t('lic.usage.limit')}</th></tr></thead>
          <tbody>
            {limits.map((l) => {
              const full = (l.percent ?? 0) >= 100;
              const near = !full && (l.percent ?? 0) >= 90;
              return (
                <tr key={l.key}>
                  <th scope="row"><bdi>{name(l.key)}</bdi></th>
                  <td>
                    {l.used === null ? <span className="erp-muted">{t('lic.limit.unknown')}</span> : (
                      <>
                        {l.max !== null ? t('lic.limit.of', { used: f.int(l.used), max: f.int(l.max) }) : f.int(l.used)}
                        {l.max !== null && l.max > 0 && <meter className="erp-meter" min={0} max={l.max} value={Math.min(l.used, l.max)} aria-label={name(l.key)} />}
                        {l.percent !== null && l.max !== null && <span className="erp-small erp-muted"> {t('lic.limit.percent', { percent: f.int(l.percent) })}</span>}
                      </>
                    )}
                    {full && <> <StatusChip status="blocked" label={t('lic.limit.full')} /></>}
                    {near && <> <StatusChip status="pending" label={t('lic.limit.warn')} /></>}
                  </td>
                  <td>{l.max === null ? t('lic.limit.unlimited') : f.int(l.max)}</td>
                </tr>
              );
            })}
          </tbody>
        </table>
      )}
    </section>
  );
}

function Usage() {
  const { t } = useText();
  const f = useFormats();
  const name = useLimitName();
  const [from, setFrom] = useState('');
  const [to, setTo] = useState('');
  const [applied, setApplied] = useState<{ from: string; to: string } | null>(null);
  const [invalid, setInvalid] = useState(false);
  const query = useQuery({ queryKey: ['lic', 'usage', applied], queryFn: () => opsApi.usage(applied?.from.replace('-', '') ?? '', applied?.to.replace('-', '') ?? ''), refetchOnMount: 'always' });
  const show = () => {
    const a = from.trim();
    const b = to.trim() || a;
    if ((a && !MONTH.test(a)) || (b && !MONTH.test(b))) { setInvalid(true); return; }
    setInvalid(false);
    setApplied(a ? { from: a, to: b } : null);
  };
  return (
    <section className="erp-stack" aria-labelledby="lic-usage">
      <h2 id="lic-usage" className="erp-h3">{t('lic.usage')}</h2>
      <div className="erp-filters" role="search" aria-label={t('lic.usage')}>
        <TextField label={t('lic.usage.from')} value={from} onChange={setFrom} {...(invalid ? { error: t('lic.usage.invalid') } : {})} />
        <TextField label={t('lic.usage.to')} value={to} onChange={setTo} />
        <Button variant="secondary" onPress={show}>{t('lic.usage.show')}</Button>
      </div>
      <Loaded query={query}>
        {(statements) => statements.length === 0 || statements.every((s) => s.lines.length === 0) ? <EmptyState title={t('lic.usage')}>{t('lic.usage.empty')}</EmptyState> : (
          <>
            {statements.map((s) => (
              <div key={s.period} className="erp-stack">
                <h3 className="erp-h3"><bdi dir="ltr">{month(s.period)}</bdi></h3>
                {s.highestPercent !== null && <p className="erp-small erp-muted">{t('lic.usage.highest', { percent: f.int(s.highestPercent) })}</p>}
                <table className="erp-table">
                  <caption className="erp-visually-hidden">{month(s.period)}</caption>
                  <thead><tr><th scope="col">{t('lic.usage.meter')}</th><th scope="col">{t('lic.usage.value')}</th><th scope="col">{t('lic.usage.limit')}</th></tr></thead>
                  <tbody>
                    {s.lines.map((l) => (
                      <tr key={l.meter}>
                        <th scope="row"><bdi>{l.limitKey ? name(l.limitKey) : l.meter}</bdi></th>
                        <td>{f.int(l.value)}</td>
                        <td>{l.limit === null ? t('lic.limit.unlimited') : f.int(l.limit)}{l.percent !== null && l.limit !== null ? ` (${t('lic.limit.percent', { percent: f.int(l.percent) })})` : ''}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            ))}
          </>
        )}
      </Loaded>
    </section>
  );
}
