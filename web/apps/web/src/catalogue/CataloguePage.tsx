// Component catalogue (slice S1.12a, Build item 3; spec 13 FR-UX-CMP-01, FR-UX-GOV-02, ruling Q19): every component in
// its states, in the current language, direction, theme and density. Built only for development and tests; Playwright
// screenshots it in English and Urdu, light, dark and high contrast. Sample texts are catalogue data, not product text.
import { useState } from 'react';
import { parseDate } from '@internationalized/date';
import {
  ActionMenu, Badge, Banner, Breadcrumbs, Button, CheckboxField, ComboBoxField, DateField, Dialog, EmptyState, ErrorPanel, IconButton,
  Link, NumberField, OneTimeCodeField, PageHeader, PasswordField, QrCode, RadioGroupField, SelectField, Skeleton, Spinner, STATUSES,
  StatusChip, SwitchField, Tabs, TextAreaField, TextField, Tooltip, useToast, type Option, type Status,
} from '@erp/ui';
import { formatDecimal, formatMoney, isolate } from '@erp/i18n';
import { Bell, Settings } from 'lucide-react';
import { useDisplay } from '../app/display-context';
import type { Density, Theme } from '../app/preferences';

const SAMPLE = {
  en: { title: 'Component catalogue', name: 'Branch name', code: 'Code', notes: 'Notes', help: 'Shown on letters and payslips.', required: 'Enter a name.', count: 'Employees', date: 'Start date', city: 'City', company: 'Company', save: 'Save', cancel: 'Cancel', delete: 'Delete', more: 'Open settings', banner: 'The licence ends in 12 days. Contact your administrator.', toast: 'Branch saved.', dialog: 'Delete this branch?', dialogBody: 'The branch and its address are removed. This cannot be undone.', tabs: ['Details', 'History'], tooltip: 'Settings of this screen', empty: 'No branches yet', error: 'The branch could not be saved.', active: 'Active', remember: 'Remember this browser', option: ['Karachi', 'Lahore', 'Islamabad'] },
  ur: { title: 'اجزاء کی فہرست', name: 'برانچ کا نام', code: 'کوڈ', notes: 'نوٹس', help: 'خطوط اور تنخواہ کی پرچیوں پر دکھایا جاتا ہے۔', required: 'نام درج کریں۔', count: 'ملازمین', date: 'آغاز کی تاریخ', city: 'شہر', company: 'کمپنی', save: 'محفوظ کریں', cancel: 'منسوخ کریں', delete: 'حذف کریں', more: 'ترتیبات کھولیں', banner: 'لائسنس 12 دن میں ختم ہو رہا ہے۔ اپنے منتظم سے رابطہ کریں۔', toast: 'برانچ محفوظ ہو گئی۔', dialog: 'کیا یہ برانچ حذف کریں؟', dialogBody: 'برانچ اور اس کا پتہ ہٹا دیا جائے گا۔ یہ واپس نہیں ہو سکتا۔', tabs: ['تفصیلات', 'تاریخچہ'], tooltip: 'اس اسکرین کی ترتیبات', empty: 'ابھی کوئی برانچ نہیں', error: 'برانچ محفوظ نہیں ہو سکی۔', active: 'فعال', remember: 'اس براؤزر کو یاد رکھیں', option: ['کراچی', 'لاہور', 'اسلام آباد'] },
} as const;

export default function CataloguePage() {
  const { display, setDisplay } = useDisplay();
  const s = SAMPLE[display.language];
  const toast = useToast();
  const [code, setCode] = useState('123');
  const [on, setOn] = useState(true);
  const options: Option[] = s.option.map((label, i) => ({ id: String(i), label, detail: `PK-${i + 1}` }));
  const search = async (text: string) => options.filter((o) => o.label.includes(text) || (o.detail ?? '').includes(text.toUpperCase()));

  return (
    <main className="erp-catalogue erp-stack" id="main">
      <PageHeader title={s.title} breadcrumbs={[{ label: s.company, href: '/' }, { label: s.title }]}
        actions={(
          <div className="erp-row">
            <SelectField label="Language" value={display.language} onChange={(v) => setDisplay({ ...display, language: v === 'ur' ? 'ur' : 'en' })} options={[{ id: 'en', label: 'English' }, { id: 'ur', label: 'اردو' }]} />
            <SelectField label="Theme" value={display.theme} onChange={(v) => setDisplay({ ...display, theme: v as Theme })} options={['system', 'light', 'dark', 'contrast'].map((id) => ({ id, label: id }))} />
            <SelectField label="Density" value={display.density} onChange={(v) => setDisplay({ ...display, density: v as Density })} options={['compact', 'standard', 'comfortable'].map((id) => ({ id, label: id }))} />
          </div>
        )} />

      <section className="erp-stack" aria-labelledby="c-buttons">
        <h2 id="c-buttons">Buttons, links, chips</h2>
        <div className="erp-row">
          <Button variant="primary">{s.save}</Button>
          <Button variant="secondary">{s.cancel}</Button>
          <Button variant="danger">{s.delete}</Button>
          <Button variant="quiet">{s.more}</Button>
          <Button variant="primary" isDisabled>{s.save}</Button>
          <Button variant="primary" isPending>{s.save}</Button>
          <Tooltip text={s.tooltip}><IconButton label={s.more} icon={<Settings size={18} aria-hidden="true" />} /></Tooltip>
          <span className="erp-row"><Bell size={18} aria-hidden="true" /><Badge count={7} label="7" /></span>
          <ActionMenu actions={[{ id: 'a', label: s.save }, { id: 'b', label: s.delete, isDisabled: true }]} onAction={() => undefined} />
          <Link href="/catalogue">{s.more}</Link>
        </div>
        <div className="erp-row">
          {(Object.keys(STATUSES) as Status[]).map((status) => <StatusChip key={status} status={status} />)}
        </div>
      </section>

      <section className="erp-stack" aria-labelledby="c-feedback">
        <h2 id="c-feedback">Feedback</h2>
        <Banner tone="warning" onDismiss={() => undefined}>{s.banner}</Banner>
        <Banner tone="info">{s.help}</Banner>
        <Banner tone="success">{s.toast}</Banner>
        <Banner tone="danger">{s.error}</Banner>
        <div className="erp-row">
          <Button variant="secondary" onPress={() => toast.success(s.toast)}>{s.toast}</Button>
          <Spinner immediate />
        </div>
        <Skeleton lines={2} />
        <EmptyState title={s.empty}>{s.help}</EmptyState>
        <ErrorPanel message={s.error} reference="0HN8K2-3F" />
      </section>

      <section className="erp-stack" aria-labelledby="c-fields">
        <h2 id="c-fields">Fields</h2>
        <div className="erp-catalogue-grid">
          <TextField label={s.name} help={s.help} isRequired defaultValue="" />
          <TextField label={s.name} error={s.required} isRequired />
          <TextField label={s.code} isCode defaultValue="KHI-01" isReadOnly />
          <TextField label={s.code} defaultValue="KHI-01" isDisabled />
          <PasswordField label="Password" autoComplete="current-password" />
          <TextAreaField label={s.notes} />
          <NumberField label={s.count} value={1250} />
          <DateField label={s.date} value={parseDate('2026-11-20')} />
          <SelectField label={s.city} options={options} value="1" />
          <ComboBoxField label={s.company} search={search} />
          <OneTimeCodeField label="Code" value={code} onChange={setCode} />
          <RadioGroupField label={s.city} value="0" onChange={() => undefined} options={options} />
          <CheckboxField label={s.remember} help={s.help} isSelected={on} onChange={setOn} />
          <SwitchField label={s.active} isSelected={on} onChange={setOn} />
        </div>
        <p>
          {formatDecimal('1234567.5')} · {formatDecimal('1234567.5', { digits: 'western', grouping: 'lakh_crore' })} · {formatDecimal('1234567.5', { digits: 'eastern_arabic_indic', grouping: 'lakh_crore' })} · {formatMoney('250000', 'PKR', display.language)} · {isolate('ali@example.com')}
        </p>
      </section>

      <section className="erp-stack" aria-labelledby="c-overlays">
        <h2 id="c-overlays">Overlays and navigation</h2>
        <Breadcrumbs items={[{ label: s.company, href: '/' }, { label: s.city, href: '/' }, { label: s.name }]} />
        <Tabs label={s.title} tabs={[{ id: 'a', title: s.tabs[0], content: <p>{s.help}</p> }, { id: 'b', title: s.tabs[1], content: <Skeleton /> }]} />
        <div className="erp-row">
          <Dialog title={s.dialog} trigger={<Button variant="danger">{s.delete}</Button>} actions={(close) => <><Button onPress={close}>{s.cancel}</Button><Button variant="danger" onPress={close}>{s.delete}</Button></>}>
            <p>{s.dialogBody}</p>
          </Dialog>
          <Dialog title={s.tabs[1]} drawer trigger={<Button>{s.tabs[1]}</Button>}><Skeleton lines={4} /></Dialog>
        </div>
        <QrCode value="otpauth://totp/ERP:catalogue?secret=JBSWY3DPEHPK3PXP&issuer=ERP" label="QR" />
      </section>
    </main>
  );
}
