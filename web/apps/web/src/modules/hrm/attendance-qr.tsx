// Gate QR code (slice S1.27a Build item 4; FR-HRM-ATT-01, Q5): a screen at the gate shows a code for one location that changes every
// `hrm.att.qr_window_seconds`. The QR carries the address of "My attendance" with the code in the part after `#` (never sent to the server's logs);
// an employee scans it with their phone camera and punches inside their own signed-in session, so a photograph of the code is useless.
import { useEffect, useMemo, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import qrcode from 'qrcode-generator';
import { useDocumentTitle } from '@erp/engine';
import { EmptyState, ErrorPanel, PageHeader, SelectField, Skeleton } from '@erp/ui';
import { useLocaleSettings, useText } from '@erp/i18n';
import { systemClock } from '../../session/clock';
import { attendanceApi } from '../../api/attendance';
import { ApiProblem } from '../../api/client';
import { nameOf } from '../../api/workflow';
import { companyName, useShell } from '../../shell/context';
import { useRefusal } from './attendance-parts';
import './attendance-messages';

/** The QR as an SVG path made of the dark modules (drawn by the browser, no raster and no inline style). */
function QrSvg({ text, label }: { text: string; label: string }) {
  const { path, size } = useMemo(() => {
    const qr = qrcode(0, 'M');
    qr.addData(text);
    qr.make();
    const count = qr.getModuleCount();
    let d = '';
    for (let r = 0; r < count; r += 1) for (let c = 0; c < count; c += 1) if (qr.isDark(r, c)) d += `M${c + 4} ${r + 4}h1v1h-1z`;
    return { path: d, size: count + 8 };
  }, [text]);
  // A QR code is black on white in every theme, or cameras cannot read it.
  return (
    <svg role="img" aria-label={label} viewBox={`0 0 ${size} ${size}`} width="320" height="320" shapeRendering="crispEdges">
      <rect width={size} height={size} fill="white" />
      <path d={path} fill="black" />
    </svg>
  );
}

export default function QrScreen() {
  const { t } = useText();
  const { locale } = useLocaleSettings();
  const { can, me, activeCompany } = useShell();
  const refusal = useRefusal();
  useDocumentTitle(t('att.qr.title'));
  const lang = locale === 'ur' ? 'ur' : 'en';
  const [company, setCompany] = useState<string>(activeCompany?.id ?? me.companies[0]?.id ?? '');
  const [location, setLocation] = useState<string | null>(null);
  const [now, setNow] = useState(() => systemClock.now());
  useEffect(() => { const id = setInterval(() => setNow(systemClock.now()), 1000); return () => clearInterval(id); }, []);
  const locations = useQuery({ queryKey: ['hrm', 'attendance', 'locations', company], queryFn: ({ signal }) => attendanceApi.locations(company, signal), enabled: !!company && can('hrm.attendance.qr_display') });
  const chosen = location ?? locations.data?.items[0]?.id ?? null;
  const code = useQuery({
    queryKey: ['hrm', 'attendance', 'qr', company, chosen],
    queryFn: ({ signal }) => attendanceApi.qrCode(company, chosen ?? '', signal),
    enabled: !!chosen,
    refetchInterval: (query) => Math.max(2000, (query.state.data ? new Date(query.state.data.expiresAt).getTime() - systemClock.now() : 5000) + 500),
  });
  if (!can('hrm.attendance.qr_display')) return <div className="erp-page"><PageHeader title={t('att.qr.title')} /><EmptyState title={t('att.denied')} /></div>;
  const place = locations.data?.items.find((l) => l.id === chosen);
  const placeName = place ? nameOf(place.nameI18n, locale) || place.code : '';
  const seconds = code.data ? Math.max(0, Math.ceil((new Date(code.data.expiresAt).getTime() - now) / 1000)) : 0;
  const problem = code.error instanceof ApiProblem ? code.error : locations.error instanceof ApiProblem ? locations.error : null;
  return (
    <div className="erp-page">
      <PageHeader title={t('att.qr.title')} description={code.data ? t('att.qr.description', { seconds: code.data.windowSeconds }) : undefined} />
      <div className="erp-stack">
        <div className="erp-row erp-wrap">
          {me.companies.length > 1 && <SelectField label={t('att.company')} options={me.companies.map((c) => ({ id: c.id, label: companyName(c, lang) }))} value={company} onChange={(v) => { setCompany(v); setLocation(null); }} />}
          {(locations.data?.items.length ?? 0) > 1 && <SelectField label={t('att.qr.location')} options={(locations.data?.items ?? []).map((l) => ({ id: l.id, label: nameOf(l.nameI18n, locale) || l.code, detail: l.code }))} value={chosen} onChange={setLocation} />}
        </div>
        {(locations.isPending || (!!chosen && code.isPending)) && <Skeleton />}
        {problem && <ErrorPanel title={t('att.loadError')} message={refusal(problem)} />}
        {locations.data && locations.data.items.length === 0 && <EmptyState title={t('att.qr.noLocations')}>{t('att.qr.noLocations.body')}</EmptyState>}
        {code.data && (
          <figure className="erp-stack">
            <QrSvg text={`${window.location.origin}/time/punch#code=${encodeURIComponent(code.data.code)}`} label={t('att.qr.image', { name: placeName })} />
            <figcaption>
              <p><bdi className="erp-code">{t('att.qr.manual', { code: code.data.code })}</bdi></p>
              <p className="erp-muted" role="status">{t('att.qr.expires', { seconds })}</p>
            </figcaption>
          </figure>
        )}
      </div>
    </div>
  );
}
