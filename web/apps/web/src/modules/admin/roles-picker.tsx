// The permission picker of a role (slice S1.13b Build item 7; FR-ADM-ROL-02): permissions grouped by module and resource,
// search, "select all in resource", sensitive ones marked. Saving asks for a reason; a change that needs a second
// administrator says so. The protected Tenant Admin role is shown read-only.
import { useMemo, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { Banner, Button, CheckboxField, EmptyState, ErrorPanel, Skeleton, TextField } from '@erp/ui';
import { formatInteger, useLocaleSettings, useText } from '@erp/i18n';
import { access, isPending, type RoleSummary } from '../../api/access';
import { ReasonDialog } from './users-parts';
import { SensitiveMark, groupCatalogue } from './roles-catalogue';
import { catalogKey, useRoleAnswer } from './roles-parts';

export function PermissionPicker({ role, canEdit, protectedRole }: { role: RoleSummary; canEdit: boolean; protectedRole: boolean }) {
  const { t, server } = useText();
  const { numbers } = useLocaleSettings();
  const answer = useRoleAnswer();
  const catalog = useQuery({ queryKey: catalogKey, queryFn: access.catalog });
  const [search, setSearch] = useState('');
  const [chosen, setChosen] = useState<Set<string>>(() => new Set(role.permissions));
  const [saving, setSaving] = useState(false);
  const groups = useMemo(() => groupCatalogue(catalog.data ?? [], search), [catalog.data, search]);
  const editable = canEdit && !protectedRole;
  const original = useMemo(() => new Set(role.permissions), [role.permissions]);
  const added = [...chosen].filter((p) => !original.has(p)).length;
  const removed = [...original].filter((p) => !chosen.has(p)).length;
  const dirty = added + removed > 0;

  const toggle = (codes: string[], on: boolean) => setChosen((prev) => {
    const next = new Set(prev);
    for (const c of codes) { if (on) next.add(c); else next.delete(c); }
    return next;
  });

  if (catalog.isPending) return <Skeleton lines={8} />;
  if (catalog.isError) return <ErrorPanel message={t('perms.loadError')} action={<Button variant="primary" onPress={() => { void catalog.refetch(); }}>{t('common.retry')}</Button>} />;
  return (
    <div className="erp-stack">
      {protectedRole && <Banner tone="info" title={t('roles.protected.title')}>{t('roles.protected.body')}</Banner>}
      <p>{t('roles.picker.count', { count: formatInteger(chosen.size, numbers) })}</p>
      <TextField type="search" label={t('perms.search')} value={search} onChange={setSearch} />
      {groups.length === 0 ? <EmptyState title={t('perms.noMatches')} /> : groups.map((m) => (
        <section key={m.module} className="erp-perm-module" aria-label={m.module}>
          <h3 className="erp-h3">{m.module}</h3>
          {m.resources.map((r) => {
            const codes = r.entries.map((e) => e.code);
            const all = codes.every((c) => chosen.has(c));
            return (
              <fieldset key={r.resource} className="erp-perm-resource">
                <legend className="erp-visually-hidden">{r.resource}</legend>
                <CheckboxField label={t('roles.picker.selectAll', { resource: r.resource })} isSelected={all} isDisabled={!editable} onChange={(on) => toggle(codes, on)} />
                {r.entries.map((e) => (
                  <CheckboxField key={e.code} label={e.code} isSelected={chosen.has(e.code)} isDisabled={!editable} onChange={(on) => toggle([e.code], on)}
                    help={<>{server(e.descriptionKey) ?? e.action}{e.isSensitive && <> · <SensitiveMark /></>}</>} />
                ))}
              </fieldset>
            );
          })}
        </section>
      ))}
      {editable && (
        <div className="erp-row">
          <Button variant="primary" isDisabled={!dirty} onPress={() => setSaving(true)}>{t('roles.picker.save')}</Button>
          <Button variant="secondary" isDisabled={!dirty} onPress={() => setChosen(new Set(role.permissions))}>{t('roles.picker.reset')}</Button>
          {dirty && <span className="erp-muted">{t('roles.picker.changes', { added: formatInteger(added, numbers), removed: formatInteger(removed, numbers) })}</span>}
        </div>
      )}
      {saving && (
        <ReasonDialog isOpen title={t('roles.picker.save.title')} confirmLabel={t('roles.picker.save')} onClose={() => setSaving(false)}
          effect={t('roles.picker.save.effect', { added: formatInteger(added, numbers), removed: formatInteger(removed, numbers), users: formatInteger(role.userCount, numbers) })}
          onConfirm={async (reason) => {
            const result = await access.setPermissions(role.id, [...chosen].sort(), reason);
            if (isPending(result)) setChosen(new Set(role.permissions));
            answer(result, 'roles.picker.saved');
          }} />
      )}
    </div>
  );
}
