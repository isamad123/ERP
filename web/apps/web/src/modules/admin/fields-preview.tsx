// "Preview the form as this role" (slice S1.13b Build item 9; FR-ADM-ROL-07): what each field of a table looks like to a role,
// from the server's field preview. Every state has a text, never colour alone: hidden is struck and named, masked shows dots.
import { useQuery } from '@tanstack/react-query';
import { Button, ErrorPanel, Skeleton } from '@erp/ui';
import { useLocaleSettings, useText, type MessageKey } from '@erp/i18n';
import { access, type FieldPreviewItem } from '../../api/access';
import { ApiProblem } from '../../api/client';

export const MASK = '••••••';
export const levelKey = (level: string) => `fsec.level.${level}` as MessageKey;

export function FormPreview({ roleId, resource, roleName, label }: { roleId: string; resource: string; roleName: string; label: (field: string) => string }) {
  const { t } = useText();
  const { locale } = useLocaleSettings();
  const preview = useQuery({ queryKey: ['iam', 'field-preview', roleId, resource], queryFn: () => access.fieldPreview(roleId, resource), refetchOnMount: 'always' });
  if (preview.isPending) return <Skeleton lines={4} />;
  if (preview.isError) {
    const problem = preview.error instanceof ApiProblem ? preview.error : null;
    return <ErrorPanel message={problem?.status === 403 ? t('fsec.denied') : problem?.status === 404 ? t('fsec.noFields') : t('fsec.loadError')} reference={problem?.reference ?? null}
      action={problem?.status === 404 ? undefined : <Button variant="primary" onPress={() => { void preview.refetch(); }}>{t('common.retry')}</Button>} />;
  }
  return (
    <section className="erp-mock" aria-label={t('fsec.preview.label', { role: roleName })}>
      {preview.data.fields.map((f) => <MockField key={f.field} item={f} label={f.label?.[locale] || f.label?.en || label(f.field)} />)}
    </section>
  );
}

function MockField({ item, label }: { item: FieldPreviewItem; label: string }) {
  const { t } = useText();
  const state = t(levelKey(item.access));
  const id = `mock-${item.field}`;
  return (
    <div className="erp-mock-field">
      <label htmlFor={id} className={item.access === 'hidden' ? 'erp-mock-hidden' : undefined}>{label} <span className="erp-small erp-muted">({state})</span></label>
      {item.access === 'hidden'
        ? <span id={id} className="erp-mock-hidden">{t('fsec.preview.hidden')}</span>
        : <input id={id} className="erp-input" readOnly={item.access !== 'editable'} disabled={item.access === 'read_only'}
            value={item.access === 'masked' ? MASK : undefined} defaultValue={item.access === 'masked' ? undefined : ''} aria-label={`${label}: ${state}`} />}
    </div>
  );
}
