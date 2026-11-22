// Two roles side by side (slice S1.13b Build item 7; FR-ADM-ROL-02): permissions only in the left role, in both, only in the right.
import { useSearchParams } from 'react-router';
import { useQuery } from '@tanstack/react-query';
import { Banner, Button, EmptyState, ErrorPanel, PageHeader, SelectField, Skeleton } from '@erp/ui';
import { useDocumentTitle } from '@erp/engine';
import { formatInteger, useLocaleSettings, useText } from '@erp/i18n';
import { ApiProblem } from '../../api/client';
import { access } from '../../api/access';
import { nameOf } from '../../api/workflow';
import { ROLES_PATH, rolesKey } from './roles-parts';

export function Compare() {
  const { t } = useText();
  const { locale, numbers } = useLocaleSettings();
  useDocumentTitle(t('roles.compare'));
  const [params, setParams] = useSearchParams();
  const left = params.get('left');
  const right = params.get('right');
  const roles = useQuery({ queryKey: rolesKey, queryFn: access.roles, staleTime: 30_000 });
  const result = useQuery({ queryKey: ['iam', 'role-compare', left, right], queryFn: () => access.compare(left ?? '', right ?? ''), enabled: !!left && !!right });
  const options = (roles.data ?? []).map((r) => ({ id: r.id, label: nameOf(r.nameI18n, locale) || r.code, detail: r.code }));
  const label = (id: string | null) => options.find((o) => o.id === id)?.label ?? '';
  const choose = (name: 'left' | 'right', value: string) => {
    const next = new URLSearchParams(params);
    next.set(name, value);
    setParams(next, { replace: true });
  };
  const problem = result.error instanceof ApiProblem ? result.error : null;
  const column = (title: string, items: string[]) => (
    <section aria-label={title} className="erp-stack">
      <h2 className="erp-h3">{title} ({formatInteger(items.length, numbers)})</h2>
      {items.length === 0 ? <p className="erp-muted">{t('roles.compare.none')}</p> : <ul>{items.map((p) => <li key={p}><bdi className="erp-code">{p}</bdi></li>)}</ul>}
    </section>
  );
  return (
    <div className="erp-page">
      <PageHeader title={t('roles.compare')} description={t('roles.compare.description')} breadcrumbs={[{ label: t('roles.title'), href: ROLES_PATH }, { label: t('roles.compare') }]} />
      <div className="erp-row erp-wrap">
        <SelectField label={t('roles.compare.left')} value={left} onChange={(v) => choose('left', v)} options={options} />
        <SelectField label={t('roles.compare.right')} value={right} onChange={(v) => choose('right', v)} options={options} />
      </div>
      {!left || !right ? <EmptyState title={t('roles.compare.pick')}>{t('roles.compare.pick.body')}</EmptyState>
        : result.isPending ? <Skeleton lines={6} />
        : result.isError || !result.data ? (
          <ErrorPanel message={problem?.status === 403 ? t('roles.denied') : t('roles.compare.error')} reference={problem?.reference ?? null}
            action={<Button variant="primary" onPress={() => { void result.refetch(); }}>{t('common.retry')}</Button>} />
        ) : (
          <>
            {result.data.onlyLeft.length === 0 && result.data.onlyRight.length === 0 && <Banner tone="info" title={t('roles.compare.same')}>{t('roles.compare.same.body')}</Banner>}
            <div className="erp-compare">
              {column(t('roles.compare.onlyIn', { role: label(left) }), result.data.onlyLeft)}
              {column(t('roles.compare.both'), result.data.both)}
              {column(t('roles.compare.onlyIn', { role: label(right) }), result.data.onlyRight)}
            </div>
          </>
        )}
    </div>
  );
}
