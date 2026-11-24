// The position picker for the employee change dialog (slice S1.22b Build item 10; FR-HRM-POS-02): the positions of a company that exist on
// a date and are not frozen or closed, full ones marked. The dialog (S1.21) sends the chosen id as <c>assignment.positionId</c>; the server
// refuses a placement beyond the budget (409 errors.hrm.strength.*) and the dialog then offers "Ask for a strength exception".
import { useMemo } from 'react';
import { useQuery } from '@tanstack/react-query';
import { SelectField } from '@erp/ui';
import { useText } from '@erp/i18n';
import { positionsApi } from '../../api/positions';
import { ANY } from './positions-parts';
import './positions-messages';

export interface PositionPickerProps {
  companyId: string;
  /** The day the placement starts (yyyy-mm-dd). */
  asOf: string;
  value: string | null;
  onChange: (positionId: string | null) => void;
  isDisabled?: boolean;
}

export function PositionPicker({ companyId, asOf, value, onChange, isDisabled }: PositionPickerProps) {
  const { t } = useText();
  const list = useQuery({ queryKey: ['hrm', 'positions', 'picker', companyId, asOf], queryFn: () => positionsApi.list(companyId, asOf) });
  const holders = useQuery({ queryKey: ['hrm', 'position', 'picker', 'full', value, asOf], enabled: value !== null, queryFn: () => positionsApi.holders(value as string, asOf) });
  const options = useMemo(() => [
    { id: ANY, label: t('pos.picker.none') },
    ...(list.data?.items ?? []).filter((p) => p.status === 'open').map((p) => ({ id: p.id, label: p.title, detail: `${p.positionCode}${holders.data?.position.id === p.id && holders.data.vacantFte <= 0 ? ` · ${t('pos.picker.full')}` : ''}` })),
  ], [list.data, holders.data, t]);
  return (
    <SelectField label={t('pos.picker.label')} help={list.isError ? t('pos.picker.loadError') : t('pos.picker.help')} options={options} value={value ?? ANY}
      onChange={(id) => onChange(id === ANY ? null : id)} isDisabled={isDisabled ?? list.isPending} />
  );
}
