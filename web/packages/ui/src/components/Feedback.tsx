// Feedback states (spec 13 section 16 STA, FR-UX-CMP-01; slice S1.12a Build item 3): status chip and badge (text and
// icon, never colour alone, FR-UX-TOK-03), banner, toast (shown at least 6 seconds and announced), skeleton, spinner
// (shown only after 300 ms, so quick answers do not flicker), empty state and error panel with the reference code.
import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { AlertCircle, AlertTriangle, Ban, CheckCircle2, CircleDashed, Clock, Info, Inbox, Loader2, PauseCircle, X, XCircle } from 'lucide-react';
import { isolate, useText } from '@erp/i18n';
import { Button as AriaButton } from 'react-aria-components';

export type Tone = 'neutral' | 'info' | 'success' | 'warning' | 'danger';

/** The product's status model (FR-UX-TOK-04): the same colour, icon and label in every module. */
export const STATUSES = {
  draft: { tone: 'neutral', icon: CircleDashed },
  pending: { tone: 'warning', icon: Clock },
  approved: { tone: 'success', icon: CheckCircle2 },
  rejected: { tone: 'danger', icon: XCircle },
  blocked: { tone: 'danger', icon: Ban },
  on_hold: { tone: 'warning', icon: PauseCircle },
  info: { tone: 'info', icon: Info },
} as const;

export type Status = keyof typeof STATUSES;

export function StatusChip({ status, label }: { status: Status; label?: string }) {
  const { t } = useText();
  const { tone, icon: Icon } = STATUSES[status];
  return (
    <span className="erp-chip" data-tone={tone}>
      <Icon size={14} aria-hidden="true" />
      <span>{label ?? t(`ui.status.${status}`)}</span>
    </span>
  );
}

/**
 * A count on an icon (unread notifications); the label says what is counted. `decorative` when the control it sits on
 * already names the count (the bell's own label, S1.12b), so it is not announced twice.
 */
export function Badge({ count, label, decorative = false }: { count: number; label: string; decorative?: boolean }) {
  if (count <= 0) return null;
  const text = count > 99 ? '99+' : count;
  return decorative
    ? <span className="erp-badge" aria-hidden="true">{text}</span>
    : <span className="erp-badge" role="status" aria-label={label}>{text}</span>;
}

const TONE_ICON = { neutral: Info, info: Info, success: CheckCircle2, warning: AlertTriangle, danger: AlertCircle } as const;

export interface BannerProps {
  tone: Exclude<Tone, 'neutral'>;
  title?: string;
  children: ReactNode;
  action?: ReactNode;
  /** Shown only when the banner may be hidden (spec 13 STA, S1.12 Q17: Warning for the day). */
  onDismiss?: () => void;
}

export function Banner({ tone, title, children, action, onDismiss }: BannerProps) {
  const { t } = useText();
  const Icon = TONE_ICON[tone];
  // Errors interrupt; everything else is announced politely.
  return (
    <div className="erp-banner" data-tone={tone} role={tone === 'danger' ? 'alert' : 'status'}>
      <Icon size={20} aria-hidden="true" />
      <div className="erp-banner-body erp-stack">
        {title && <strong>{title}</strong>}
        <div>{children}</div>
        {action}
      </div>
      {onDismiss && (
        <AriaButton className="erp-button erp-icon-button" data-variant="quiet" aria-label={t('ui.banner.dismiss')} onPress={onDismiss}>
          <X size={18} aria-hidden="true" />
        </AriaButton>
      )}
    </div>
  );
}

export function Skeleton({ lines = 3 }: { lines?: number }) {
  const { t } = useText();
  return (
    <div className="erp-stack" role="status" aria-label={t('ui.skeleton')} aria-busy="true">
      {Array.from({ length: lines }, (_, i) => <span key={i} className="erp-skeleton" />)}
    </div>
  );
}

/** A spinner that appears only after 300 ms (spec 13 STA-01), unless `immediate`. */
export function Spinner({ size = 'medium', immediate = false, label }: { size?: 'small' | 'medium'; immediate?: boolean; label?: string }) {
  const { t } = useText();
  const [visible, setVisible] = useState(immediate);
  useEffect(() => {
    if (immediate) return undefined;
    const timer = window.setTimeout(() => setVisible(true), 300);
    return () => window.clearTimeout(timer);
  }, [immediate]);
  if (!visible) return null;
  return (
    <span className="erp-spinner" role="status" aria-label={label ?? t('ui.spinner')}>
      <Loader2 size={size === 'small' ? 16 : 24} aria-hidden="true" />
    </span>
  );
}

/** An empty state; `level` 1 when it is the whole page (every page has one main heading). */
export function EmptyState({ title, children, action, level = 2 }: { title?: string; children?: ReactNode; action?: ReactNode; level?: 1 | 2 }) {
  const { t } = useText();
  const Heading = level === 1 ? 'h1' : 'h2';
  return (
    <section className="erp-panel">
      <Inbox className="erp-panel-icon" size={32} aria-hidden="true" />
      <Heading>{title ?? t('ui.empty.title')}</Heading>
      {children && <div className="erp-muted">{children}</div>}
      {action}
    </section>
  );
}

/** What happened, what to do, and the reference for support (spec 13 FR-ADM-UXA-04, S1.12 Q18). Nothing technical; `level` 1 when it is the whole page. */
export function ErrorPanel({ title, message, reference, action, level = 2 }: { title?: string; message: string; reference?: string | null; action?: ReactNode; level?: 1 | 2 }) {
  const { t } = useText();
  const Heading = level === 1 ? 'h1' : 'h2';
  return (
    <section className="erp-panel" data-tone="danger" role="alert">
      <AlertCircle className="erp-panel-icon" size={32} aria-hidden="true" />
      <Heading>{title ?? t('ui.error.title')}</Heading>
      <p>{message}</p>
      {reference && <p className="erp-small erp-muted">{t('common.reference', { reference: isolate(reference) })}</p>}
      {action}
    </section>
  );
}

// Toasts: a small queue of short confirmations. Each stays at least 6 seconds (longer for long texts), is announced by
// the live region, pauses while hovered or focused, and can be dismissed by keyboard.
interface ToastItem {
  id: number;
  tone: Exclude<Tone, 'neutral'>;
  text: string;
}

const ToastContext = createContext<(tone: ToastItem['tone'], text: string) => void>(() => undefined);

export const TOAST_MIN_MS = 6000;

export function ToastProvider({ children }: { children: ReactNode }) {
  const { t } = useText();
  const [items, setItems] = useState<ToastItem[]>([]);
  const next = useRef(1);
  const timers = useRef(new Map<number, number>());
  const remove = useCallback((id: number) => {
    window.clearTimeout(timers.current.get(id));
    timers.current.delete(id);
    setItems((list) => list.filter((item) => item.id !== id));
  }, []);
  const schedule = useCallback((id: number, text: string) => {
    timers.current.set(id, window.setTimeout(() => remove(id), Math.max(TOAST_MIN_MS, text.length * 60)));
  }, [remove]);
  const show = useCallback((tone: ToastItem['tone'], text: string) => {
    const id = next.current++;
    setItems((list) => [...list.slice(-2), { id, tone, text }]);
    schedule(id, text);
  }, [schedule]);
  useEffect(() => () => timers.current.forEach((timer) => window.clearTimeout(timer)), []);
  const pause = (id: number) => window.clearTimeout(timers.current.get(id));
  return (
    <ToastContext.Provider value={show}>
      {children}
      <section className="erp-toast-region" aria-label={t('ui.toast.region')} aria-live="polite">
        {items.map((item) => (
          <ToastView key={item.id} item={item} onPause={() => pause(item.id)} onResume={() => schedule(item.id, item.text)} onDismiss={() => remove(item.id)} />
        ))}
      </section>
    </ToastContext.Provider>
  );
}

/** One toast; pointer or keyboard focus inside it pauses its timer (WCAG 2.2.1, timing adjustable). */
function ToastView({ item, onPause, onResume, onDismiss }: { item: ToastItem; onPause: () => void; onResume: () => void; onDismiss: () => void }) {
  const ref = useRef<HTMLDivElement>(null);
  const handlers = useRef({ onPause, onResume });
  handlers.current = { onPause, onResume };
  useEffect(() => {
    const element = ref.current;
    if (!element) return undefined;
    const pause = () => handlers.current.onPause();
    const resume = () => handlers.current.onResume();
    element.addEventListener('pointerenter', pause);
    element.addEventListener('pointerleave', resume);
    element.addEventListener('focusin', pause);
    element.addEventListener('focusout', resume);
    return () => {
      element.removeEventListener('pointerenter', pause);
      element.removeEventListener('pointerleave', resume);
      element.removeEventListener('focusin', pause);
      element.removeEventListener('focusout', resume);
    };
  }, []);
  return (
    <div ref={ref} className="erp-toast">
      <Banner tone={item.tone} onDismiss={onDismiss}>{item.text}</Banner>
    </div>
  );
}

export function useToast() {
  const show = useContext(ToastContext);
  return useMemo(() => ({
    success: (text: string) => show('success', text),
    info: (text: string) => show('info', text),
    warning: (text: string) => show('warning', text),
    error: (text: string) => show('danger', text),
  }), [show]);
}
