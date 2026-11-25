// The rule blocks of a leave policy version (slice S1.33b Build item 6; FR-HRM-LVE-03): one block for each leave type the policy lists, its rules in grouped sections made from the policy type's schema
// by `ParameterForm` (the S1.23 form), so labels, limits and every refusal come from the schema and the server. A version in force is read-only; a draft can be edited. A leave type the policy does
// not list cannot be taken by the employees under it, and the screen says so in plain words. No number is filled in for anybody.
import { useState } from 'react';
import { Plus, Trash2 } from 'lucide-react';
import { Banner, Button, SelectField, type Option } from '@erp/ui';
import { useLocaleSettings, useText, type MessageKey } from '@erp/i18n';
import type { JsonObject, ParamSchema } from '../../api/policies';
import type { LeaveTypeRow } from '../../api/leave-types';
import { nameOf } from '../../api/workflow';
import { ParameterForm } from '../hr/policy-form';
import { blockErrors, blockSchema, blocksOf, listedCodes, listError, mergePart, newBlock, partValue, sectionsOf, TYPE_KEY, typeError } from './leave-rules-model';
import './leave-types-messages';
import './leave-types.css';

const same = (a: string, b: string) => a.toLowerCase() === b.toLowerCase();

interface Props {
  schema: ParamSchema;
  parameters: JsonObject;
  /** The company's leave types; null when the person may not read them (names and the unlisted note are then left out). */
  leaveTypes: LeaveTypeRow[] | null;
  errors: Record<string, string>;
  onChange?: (parameters: JsonObject) => void;
}

export function RuleBlocks({ schema, parameters, leaveTypes, errors, onChange }: Props) {
  const { t } = useText();
  const { locale } = useLocaleSettings();
  const editable = !!onChange;
  const block = blockSchema(schema);
  const blocks = blocksOf(parameters);
  const listed = listedCodes(parameters);
  const unlisted = (leaveTypes ?? []).filter((x) => x.isActive && !listed.some((c) => same(c, x.code)));
  const setBlocks = (next: JsonObject[]) => onChange?.({ ...parameters, types: next });
  if (!block) return <ParameterForm schema={schema} value={parameters} errors={errors} {...(onChange ? { onChange } : {})} readOnly={!editable} />;
  const nameFor = (code: string) => {
    const row = (leaveTypes ?? []).find((x) => same(x.code, code));
    return row ? nameOf(row.nameI18n, locale) || row.code : null;
  };
  const listProblem = listError(errors);
  return (
    <div className="erp-stack">
      {listProblem && <Banner tone="danger">{listProblem}</Banner>}
      {blocks.length === 0 && <p className="erp-small erp-muted">{t('lrl.blocks.none')}</p>}
      {blocks.map((b, i) => {
        const code = typeof b[TYPE_KEY] === 'string' ? b[TYPE_KEY] : '';
        const name = nameFor(code);
        const sections = sectionsOf(block, editable ? undefined : b);
        const headingId = `lrl-block-${i}`;
        const own = typeError(errors, i);
        return (
          // A block has no identity of its own: its position in the list is what the server's errors name.
          <section key={i} className="erp-lrule-block erp-stack" aria-labelledby={headingId}>
            <div className="erp-lrule-head">
              <h3 id={headingId} className="erp-lrule-title">{name ? <>{name} <span className="erp-code erp-small"><bdi>{code}</bdi></span></> : <bdi>{code}</bdi>}</h3>
              {editable && <Button variant="quiet" icon={<Trash2 size={16} aria-hidden="true" />} onPress={() => setBlocks(blocks.filter((_, j) => j !== i))}
                aria-label={t('lrl.block.remove.aria', { name: name ?? code })}>{t('lrl.block.remove')}</Button>}
            </div>
            {own && <p className="erp-field-error" role="alert">{own}</p>}
            {leaveTypes && !name && code !== '' && <Banner tone="warning">{t('lrl.block.unknown', { code })}</Banner>}
            {sections.length === 0 && <p className="erp-small erp-muted">{t('lrl.block.empty')}</p>}
            {sections.map((s) => (
              <section key={s.id} className="erp-lrule-section erp-stack" aria-labelledby={`${headingId}-${s.id}`}>
                <h4 id={`${headingId}-${s.id}`} className="erp-lrule-subtitle">{t(`lrl.section.${s.id}` as MessageKey)}</h4>
                <p className="erp-small erp-muted">{t(`lrl.section.${s.id}.help` as MessageKey)}</p>
                {s.parts.map((part) => (
                  <ParameterForm key={part.scope || 'type'} schema={part.schema} value={partValue(b, part)} errors={blockErrors(errors, i, part.scope)} readOnly={!editable}
                    {...(onChange ? { onChange: (v: JsonObject) => setBlocks(blocks.map((x, j) => (j === i ? mergePart(x, part, v) : x))) } : {})} />
                ))}
              </section>
            ))}
          </section>
        );
      })}
      {unlisted.length > 0 && (
        <section className="erp-lrule-section erp-stack" aria-labelledby="lrl-unlisted">
          <h3 id="lrl-unlisted" className="erp-lrule-subtitle">{t('lrl.unlisted.title')}</h3>
          <p>{t('lrl.unlisted.body', { types: unlisted.map((x) => nameOf(x.nameI18n, locale) || x.code).join(', ') })}</p>
          {editable && <AddBlock types={unlisted} onAdd={(code) => setBlocks([...blocks, newBlock(code)])} />}
        </section>
      )}
    </div>
  );
}

function AddBlock({ types, onAdd }: { types: LeaveTypeRow[]; onAdd: (code: string) => void }) {
  const { t } = useText();
  const { locale } = useLocaleSettings();
  const [code, setCode] = useState('');
  const options: Option[] = types.map((x) => ({ id: x.code, label: nameOf(x.nameI18n, locale) || x.code, detail: x.code }));
  const chosen = types.some((x) => x.code === code) ? code : types[0]?.code ?? '';
  return (
    <div className="erp-row erp-wrap">
      <SelectField label={t('lrl.add.type')} options={options} value={chosen} onChange={setCode} />
      <Button variant="secondary" icon={<Plus size={16} aria-hidden="true" />} isDisabled={chosen === ''} onPress={() => { onAdd(chosen); setCode(''); }}>{t('lrl.add')}</Button>
    </div>
  );
}
