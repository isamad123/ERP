// The pack manager (slice S1.23b Build items 11 and 12; FR-ADM-PKG-01, FR-KRN-CFG-02, FR-HRM-POL-06, FR-ADM-ONB-01): installed packs with version, contents,
// compatibility, the objects the customer changed and the conflicts of the last upgrade (pack value against the customer's value, "take the pack value"),
// newer versions with release notes ("install update"), and the packs of the catalogue that can be installed. The wizard's pack step links here.
// Every action goes through the pack manager API, which may hold it for a second person (202, cfg.packs.requires_approval).
import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useDocumentTitle } from '@erp/engine';
import { Banner, Button, Dialog, EmptyState, ErrorPanel, PageHeader, Skeleton, StatusChip, TextAreaField, useToast } from '@erp/ui';
import { formatDate, useLocaleSettings, useText, type MessageKey } from '@erp/i18n';
import { ApiProblem } from '../../api/client';
import { describeValue, packsApi, textOf, type AvailablePack, type InstalledPack, type PackActionAnswer, type PackContentCount, type PackItem } from '../../api/packs';
import { useShell } from '../../shell/context';
import './packs-messages';
import './users.css';
import './configure.css';

type Pending = { kind: 'install'; pack: AvailablePack } | { kind: 'upgrade'; pack: InstalledPack; version: string } | { kind: 'take'; item: PackItem; name: string };

const OVERVIEW = ['cfg', 'packs'] as const;

export default function PacksScreen() {
  const { t } = useText();
  useDocumentTitle(t('packs.title'));
  const { can, writable } = useShell();
  const overview = useQuery({ queryKey: OVERVIEW, queryFn: packsApi.overview, refetchOnMount: 'always' });
  const [pending, setPending] = useState<Pending | null>(null);
  const problem = overview.error instanceof ApiProblem ? overview.error : undefined;
  const mayInstall = writable && can('cfg.installed_packs.create');
  const mayUpgrade = writable && can('cfg.installed_packs.update');
  return (
    <div className="erp-page">
      <PageHeader title={t('packs.title')} description={t('packs.description')} />
      {overview.isPending && <Skeleton lines={6} />}
      {overview.isError && (
        <ErrorPanel message={problem?.status === 403 ? t('packs.denied') : t('packs.loadError')} reference={problem?.reference}
          action={<Button variant="primary" onPress={() => void overview.refetch()}>{t('common.retry')}</Button>} />
      )}
      {overview.isSuccess && (
        <div className="erp-stack">
          <p className="erp-small erp-muted">{t('packs.product', { version: overview.data.productVersion })}</p>
          {!mayInstall && !mayUpgrade && <Banner tone="info">{t('packs.readOnly')}</Banner>}
          {overview.data.approvalRequired && <Banner tone="info">{t('packs.approval')}</Banner>}
          {overview.data.pending.length > 0 && (
            <section className="erp-stack" aria-labelledby="packs-pending">
              <h2 id="packs-pending" className="erp-h3">{t('packs.pending.title')}</h2>
              <ul className="erp-stack">
                {overview.data.pending.map((p) => (
                  <li key={p.requestId}>
                    <StatusChip status="pending" label={t('packs.pending.item', { action: t(`packs.action.${p.action === 'take_value' ? 'take' : p.action}` as MessageKey), pack: p.packId, version: p.version ?? '' })} />
                    {p.mine && <span className="erp-small erp-muted"> · {t('packs.pending.mine')}</span>}
                  </li>
                ))}
              </ul>
            </section>
          )}
          <section className="erp-stack" aria-labelledby="packs-installed">
            <h2 id="packs-installed" className="erp-h3">{t('packs.installed.title')}</h2>
            {overview.data.installed.length === 0
              ? <EmptyState title={t('packs.installed.empty')}>{t('packs.installed.empty.body')}</EmptyState>
              : overview.data.installed.map((p) => (
                <InstalledCard key={p.packId} pack={p} mayUpgrade={mayUpgrade}
                  onUpgrade={(version) => setPending({ kind: 'upgrade', pack: p, version })}
                  onTake={(item, name) => setPending({ kind: 'take', item, name })} />
              ))}
          </section>
          <section className="erp-stack" aria-labelledby="packs-available">
            <h2 id="packs-available" className="erp-h3">{t('packs.available.title')}</h2>
            {overview.data.available.length === 0
              ? <EmptyState title={t('packs.available.empty')}>{t('packs.available.empty.body')}</EmptyState>
              : <AvailableTable packs={overview.data.available} mayInstall={mayInstall} onInstall={(pack) => setPending({ kind: 'install', pack })} />}
          </section>
        </div>
      )}
      {pending && <ConfirmDialog pending={pending} onClose={() => setPending(null)} />}
    </div>
  );
}

function Contents({ contents }: { contents: PackContentCount[] }) {
  const { t } = useText();
  return <>{contents.map((c) => t('packs.contents.item', { count: c.count, type: t(`packs.type.${c.type}` as MessageKey) })).join(' · ')}</>;
}

function Compatibility({ from, to }: { from: string; to: string | null }) {
  const { t } = useText();
  return <>{to ? t('packs.field.compatibleRange', { from, to }) : t('packs.field.compatible', { from })}</>;
}

function InstalledCard({ pack, mayUpgrade, onUpgrade, onTake }: { pack: InstalledPack; mayUpgrade: boolean; onUpgrade: (version: string) => void; onTake: (item: PackItem, name: string) => void }) {
  const { t } = useText();
  const { locale, formatDate } = useDates();
  const [shown, setShown] = useState<'changed' | 'all' | null>(pack.conflicts > 0 ? 'changed' : null);
  const name = textOf(pack.title, locale) || pack.packId;
  return (
    <article className="erp-config-card erp-stack" aria-labelledby={`pack-${pack.packId}`}>
      <div className="erp-row erp-wrap">
        <h3 id={`pack-${pack.packId}`} className="erp-h3"><bdi>{name}</bdi></h3>
        <StatusChip status={pack.compatible ? 'active' : 'blocked'} label={t(pack.compatible ? 'packs.compatible' : 'packs.incompatible')} />
      </div>
      <dl className="erp-num-facts">
        <dt>{t('packs.field.version')}</dt><dd><bdi className="erp-code">{pack.version}</bdi> · <span className="erp-code"><bdi>{pack.packId}</bdi></span> · {t(`packs.kind.${pack.kind}` as MessageKey)}{pack.country && <> · <bdi>{pack.country}</bdi></>}</dd>
        <dt>{t('packs.field.installed')}</dt><dd>{formatDate(pack.installedAt)}{pack.previousVersion && pack.upgradedAt && <> · {t('packs.field.upgraded', { version: pack.previousVersion })} ({formatDate(pack.upgradedAt)})</>}</dd>
        <dt>{t('packs.field.compatibility')}</dt><dd><Compatibility from={pack.compatibleFrom} to={pack.compatibleTo} /></dd>
        <dt>{t('packs.field.contents')}</dt><dd><Contents contents={pack.contents} /></dd>
        {textOf(pack.releaseNotes, locale) && <><dt>{t('packs.field.releaseNotes')}</dt><dd>{textOf(pack.releaseNotes, locale)}</dd></>}
      </dl>
      <p>
        <StatusChip status={pack.overridden > 0 ? 'info' : 'active'} label={t('packs.overridden', { count: pack.overridden })} />{' '}
        <StatusChip status={pack.conflicts > 0 ? 'pending' : 'active'} label={t('packs.conflicts', { count: pack.conflicts })} />
        {pack.retired > 0 && <span className="erp-small erp-muted"> · {t('packs.retired', { count: pack.retired })}</span>}
      </p>
      <section className="erp-stack" aria-label={t('packs.updates.title')}>
        <h4>{t('packs.updates.title')}</h4>
        {pack.updates.length === 0 && <p className="erp-small erp-muted">{t('packs.updates.none')}</p>}
        {pack.updates.map((u) => (
          <div key={u.version} className="erp-row erp-wrap">
            <span>{t('packs.update.notes', { version: u.version, date: formatDate(u.releasedAt) })}{textOf(u.releaseNotes, locale) && <>: {textOf(u.releaseNotes, locale)}</>}</span>
            {!u.licensed && <StatusChip status="blocked" label={t('packs.notLicensed')} />}
            {!u.compatible && <StatusChip status="blocked" label={t('packs.incompatible')} />}
            {mayUpgrade && u.licensed && u.compatible && (
              <Button variant="primary" aria-label={t('packs.action.upgrade.aria', { version: u.version, name })} onPress={() => onUpgrade(u.version)}>{t('packs.action.upgrade')}</Button>
            )}
          </div>
        ))}
      </section>
      <div className="erp-row erp-wrap">
        <Button variant={shown === 'changed' ? 'primary' : 'secondary'} aria-pressed={shown === 'changed'} onPress={() => setShown(shown === 'changed' ? null : 'changed')}>{t('packs.action.items')}</Button>
        <Button variant={shown === 'all' ? 'primary' : 'secondary'} aria-pressed={shown === 'all'} onPress={() => setShown(shown === 'all' ? null : 'all')}>{t('packs.action.items.all')}</Button>
      </div>
      {shown && <ItemsTable pack={pack} name={name} changedOnly={shown === 'changed'} mayTake={mayUpgrade} onTake={(item) => onTake(item, name)} />}
    </article>
  );
}

function ItemsTable({ pack, name, changedOnly, mayTake, onTake }: { pack: InstalledPack; name: string; changedOnly: boolean; mayTake: boolean; onTake: (item: PackItem) => void }) {
  const { t } = useText();
  const { locale } = useLocaleSettings();
  const items = useQuery({ queryKey: ['cfg', 'packs', pack.packId, 'items', changedOnly], queryFn: () => packsApi.items(pack.packId, changedOnly) });
  if (items.isPending) return <Skeleton lines={4} />;
  if (items.isError) return <ErrorPanel message={t('packs.items.loadError')} action={<Button variant="primary" onPress={() => void items.refetch()}>{t('common.retry')}</Button>} />;
  if (items.data.items.length === 0) return <EmptyState title={t('packs.items.empty')}>{t('packs.items.title', { name })}</EmptyState>;
  return (
    <div className="erp-stack">
      {items.data.truncated && <Banner tone="info">{t('packs.items.truncated', { shown: items.data.items.length, total: items.data.total })}</Banner>}
      <div className="erp-table-scroll">
        <table className="erp-table" aria-label={t('packs.items.title', { name })}>
          <thead><tr>
            <th scope="col">{t('packs.items.col.object')}</th><th scope="col">{t('packs.items.col.company')}</th><th scope="col">{t('packs.items.col.pack')}</th>
            <th scope="col">{t('packs.items.col.customer')}</th><th scope="col">{t('packs.items.col.newer')}</th>
            <th scope="col"><span className="erp-visually-hidden">{t('packs.items.col.actions')}</span></th>
          </tr></thead>
          <tbody>
            {items.data.items.map((item) => (
              <tr key={item.id}>
                <th scope="row">
                  <div>{t(`packs.type.${item.type}` as MessageKey)}</div>
                  <span className="erp-code erp-small"><bdi>{item.key}</bdi></span>
                </th>
                <td><bdi>{item.companyCode ?? ''}</bdi></td>
                <td><bdi>{describeValue(item.packValue, locale)}</bdi></td>
                <td>
                  {item.retiredIn
                    ? <StatusChip status="inactive" label={t('packs.items.retired', { version: item.retiredIn })} />
                    : <StatusChip status={item.changed ? 'info' : 'active'} label={t(item.customerValue === null ? 'packs.items.missing' : item.changed ? 'packs.items.changed' : 'packs.items.same')} />}
                  {item.changed && item.customerValue && <div><bdi>{describeValue(item.customerValue, locale)}</bdi></div>}
                </td>
                <td>{item.conflictVersion && <bdi>{t('packs.items.newer', { version: item.conflictVersion, value: describeValue(item.conflictValue, locale) })}</bdi>}</td>
                <td>
                  {mayTake && !item.retiredIn && (item.conflictVersion || item.changed) && (
                    <Button variant="secondary" aria-label={t('packs.action.take.aria', { key: item.key })} onPress={() => onTake(item)}>{t('packs.action.take')}</Button>
                  )}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
}

function AvailableTable({ packs, mayInstall, onInstall }: { packs: AvailablePack[]; mayInstall: boolean; onInstall: (pack: AvailablePack) => void }) {
  const { t } = useText();
  const { locale, formatDate } = useDates();
  return (
    <div className="erp-table-scroll">
      <table className="erp-table" aria-label={t('packs.available.title')}>
        <thead><tr>
          <th scope="col">{t('packs.title')}</th><th scope="col">{t('packs.field.version')}</th><th scope="col">{t('packs.field.contents')}</th>
          <th scope="col">{t('packs.field.releaseNotes')}</th><th scope="col"><span className="erp-visually-hidden">{t('packs.items.col.actions')}</span></th>
        </tr></thead>
        <tbody>
          {packs.map((p) => {
            const name = textOf(p.title, locale) || p.packId;
            return (
              <tr key={p.packId}>
                <th scope="row">
                  <div><bdi>{name}</bdi></div>
                  <span className="erp-small erp-muted">{t(`packs.kind.${p.kind}` as MessageKey)}{p.country && <> · <bdi>{p.country}</bdi></>}</span>
                </th>
                <td><bdi className="erp-code">{p.version}</bdi><div className="erp-small erp-muted">{formatDate(p.releasedAt)}</div></td>
                <td><Contents contents={p.contents} /></td>
                <td>{textOf(p.releaseNotes, locale)}</td>
                <td>
                  {!p.licensed && <StatusChip status="blocked" label={t('packs.notLicensed')} />}
                  {p.licensed && mayInstall && <Button variant="primary" aria-label={t('packs.action.install.aria', { name })} onPress={() => onInstall(p)}>{t('packs.action.install')}</Button>}
                  {p.licensed && !mayInstall && <StatusChip status="active" label={t('packs.licensed')} />}
                </td>
              </tr>
            );
          })}
        </tbody>
      </table>
    </div>
  );
}

function ConfirmDialog({ pending, onClose }: { pending: Pending; onClose: () => void }) {
  const { t, server } = useText();
  const { locale } = useLocaleSettings();
  const toast = useToast();
  const queries = useQueryClient();
  const [reason, setReason] = useState('');
  const mutation = useMutation({
    mutationFn: (why: string): Promise<PackActionAnswer> => pending.kind === 'install'
      ? packsApi.install(pending.pack.packId, pending.pack.version, why)
      : pending.kind === 'upgrade' ? packsApi.upgrade(pending.pack.packId, pending.version, why) : packsApi.takePackValue(pending.item.id, why),
    onSuccess: (answer) => {
      void queries.invalidateQueries({ queryKey: OVERVIEW });
      const r = answer.result ?? {};
      toast.success(answer.status === 'pending' ? t('packs.done.pending')
        : pending.kind === 'install' ? t('packs.done.installed', { added: r.added ?? 0, kept: r.kept ?? 0 })
          : pending.kind === 'upgrade' ? t('packs.done.upgraded', { added: r.added ?? 0, updated: r.updated ?? 0, kept: r.kept ?? 0, retired: r.retired ?? 0 })
            : t('packs.done.applied'));
      onClose();
    },
  });
  const failed = mutation.error instanceof ApiProblem ? mutation.error : undefined;
  const name = pending.kind === 'take' ? pending.name : textOf(pending.pack.title, locale) || pending.pack.packId;
  const title = pending.kind === 'install' ? t('packs.confirm.install.title', { name })
    : pending.kind === 'upgrade' ? t('packs.confirm.upgrade.title', { version: pending.version }) : t('packs.confirm.take.title');
  const body = pending.kind === 'install' ? t('packs.confirm.install.body')
    : pending.kind === 'upgrade' ? t('packs.confirm.upgrade.body') : t('packs.confirm.take.body', { key: pending.item.key });
  return (
    <Dialog title={title} isOpen role="alertdialog" onOpenChange={(open) => { if (!open) onClose(); }}
      actions={(close) => (
        <>
          <Button variant="secondary" onPress={close}>{t('common.cancel')}</Button>
          <Button variant="primary" isDisabled={mutation.isPending} onPress={() => mutation.mutate(reason)}>{t('packs.confirm.go')}</Button>
        </>
      )}>
      <div className="erp-stack">
        <p>{body}</p>
        <TextAreaField label={t('packs.reason.label')} value={reason} onChange={setReason} maxLength={500} />
        {failed && <Banner tone="danger">{server(failed.key)}</Banner>}
      </div>
    </Dialog>
  );
}

function useDates() {
  const { locale, timeZone, numbers } = useLocaleSettings();
  return { locale, formatDate: (iso: string) => formatDate(iso, locale === 'ur' ? 'ur' : 'en', timeZone, numbers) };
}
