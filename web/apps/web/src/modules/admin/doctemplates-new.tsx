// The "new template" dialog of the document template editor (slice S1.15d): one template in one language with its first draft version. A language variant is
// the same dialog started from an existing template (other language, same text to translate).
import { useState } from 'react';
import { Banner, Button, Dialog, SelectField, TextAreaField, TextField, type Option } from '@erp/ui';
import { useLocaleSettings, useText, type MessageKey } from '@erp/i18n';
import { FORMATS, KINDS, templatesApi, type EntityFields, type Format, type TemplateRow } from '../../api/templates';
import { companyName, useShell } from '../../shell/context';
import { useRefused } from './config-parts';

const ALL = 'all';
const NONE = '-';

export function NewTemplate({ seed, fields, onClose, onCreated }: { seed: Partial<TemplateRow & { body: string; format: Format }>; fields: EntityFields[]; onClose: () => void; onCreated: (id: string) => Promise<void> }) {
  const { t } = useText();
  const { locale } = useLocaleSettings();
  const { me } = useShell();
  const refused = useRefused();
  const [code, setCode] = useState(seed.code ?? '');
  const [nameEn, setNameEn] = useState('');
  const [nameUr, setNameUr] = useState('');
  const [kind, setKind] = useState(seed.kind ?? 'letter');
  const [entityType, setEntityType] = useState(seed.entityType ?? fields[0]?.entityType ?? NONE);
  const [company, setCompany] = useState(seed.companyId ?? ALL);
  const [language, setLanguage] = useState(seed.language ?? 'en');
  const [format, setFormat] = useState<Format>(seed.format ?? 'pdf');
  const [body, setBody] = useState(seed.body ?? '');
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const isPart = kind === 'part';
  const entities: Option[] = [...(isPart ? [{ id: NONE, label: t('tpl.new.entity.none') }] : []), ...fields.map((f) => ({ id: f.entityType, label: f.entityType }))];
  const companies: Option[] = [{ id: ALL, label: t('tpl.new.company.all') }, ...me.companies.map((c) => ({ id: c.id, label: companyName(c, locale), detail: c.code }))];
  const create = async () => {
    setBusy(true);
    setError(null);
    try {
      const made = await templatesApi.create({ code: code.trim(), name: { en: nameEn.trim(), ...(nameUr.trim() ? { ur: nameUr.trim() } : {}) }, kind, entityType: isPart ? NONE : entityType, companyId: company === ALL ? null : company, language, outputFormat: format, body });
      await onCreated(made.id);
    } catch (e) { setError(refused(e).summary); } finally { setBusy(false); }
  };
  return (
    <Dialog title={t('tpl.new')} isOpen onOpenChange={(open) => { if (!open) onClose(); }}
      actions={() => (
        <>
          <Button variant="secondary" onPress={onClose}>{t('common.cancel')}</Button>
          <Button variant="primary" isPending={busy} isDisabled={code.trim() === '' || nameEn.trim() === '' || body.trim() === ''} onPress={() => { void create(); }}>{t('tpl.new.create')}</Button>
        </>
      )}>
      <div className="erp-stack">
        {error && <Banner tone="danger">{error}</Banner>}
        <TextField label={t('tpl.new.code')} help={t('tpl.new.code.help')} value={code} onChange={setCode} isRequired isCode />
        <TextField label={t('tpl.new.name.en')} value={nameEn} onChange={setNameEn} isRequired />
        <div dir="rtl"><TextField label={t('tpl.new.name.ur')} value={nameUr} onChange={setNameUr} /></div>
        <SelectField label={t('tpl.new.kind')} options={KINDS.map((k) => ({ id: k, label: t(`tpl.kind.${k}` as MessageKey) }))} value={kind} onChange={setKind} />
        <SelectField label={t('tpl.new.entity')} options={entities} value={isPart ? NONE : entityType} onChange={setEntityType} isDisabled={isPart} help={fields.length === 0 && !isPart ? t('tpl.new.entity.empty') : undefined} />
        {me.companies.length > 1 && <SelectField label={t('tpl.new.company')} options={companies} value={company} onChange={setCompany} />}
        <SelectField label={t('tpl.new.language')} options={['en', 'ur'].map((l) => ({ id: l, label: t(`tpl.language.${l}` as MessageKey) }))} value={language} onChange={setLanguage} />
        <SelectField label={t('tpl.format')} options={FORMATS.map((f) => ({ id: f, label: t(`tpl.format.${f}` as MessageKey) }))} value={format} onChange={(f) => setFormat(f as Format)} />
        <div dir={language === 'ur' ? 'rtl' : 'ltr'}><TextAreaField label={t('tpl.body')} value={body} onChange={setBody} isRequired /></div>
      </div>
    </Dialog>
  );
}
