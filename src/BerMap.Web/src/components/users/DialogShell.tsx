import React, { useEffect, useId, useRef } from 'react';
import { X } from 'lucide-react';
import { Card } from '@/components/ui/Card';
import { useDialogFocus } from '@/hooks/useDialogFocus';

interface DialogShellProps {
  title: string;
  subtitle?: string;
  closeLabel: string;
  onClose: () => void;
  /** Escape and the close button are ignored while true (e.g. during a request). */
  busy?: boolean;
  children: React.ReactNode;
  footer: React.ReactNode;
}

/** Modal frame used by the user-management dialogs. */
export const DialogShell: React.FC<DialogShellProps> = ({
  title,
  subtitle,
  closeLabel,
  onClose,
  busy = false,
  children,
  footer,
}) => {
  const titleId = useId();
  const dialogRef = useRef<HTMLDivElement>(null);
  useDialogFocus(dialogRef);

  useEffect(() => {
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape' && !busy) onClose();
    };
    window.addEventListener('keydown', onKeyDown);
    return () => window.removeEventListener('keydown', onKeyDown);
  }, [busy, onClose]);

  return (
    <div
      ref={dialogRef}
      className="fixed inset-0 z-50 flex items-center justify-center p-2 sm:p-4 bg-black/60 backdrop-blur-xs bm-fade-in"
      role="dialog"
      aria-modal="true"
      aria-labelledby={titleId}
    >
      <Card padding="none" className="w-full max-w-lg shadow-2xl overflow-hidden flex flex-col max-h-[92vh]">
        <div className="flex items-start justify-between gap-3 p-4 sm:p-5 border-b border-neutral-200 dark:border-dark-border bg-neutral-50 dark:bg-dark-card">
          <div className="min-w-0">
            <h3 id={titleId} className="font-bold text-lg text-neutral-900 dark:text-white">
              {title}
            </h3>
            {subtitle && <p className="text-xs text-neutral-500 mt-0.5 break-words">{subtitle}</p>}
          </div>
          <button
            type="button"
            onClick={onClose}
            disabled={busy}
            aria-label={closeLabel}
            data-dialog-close
            className="p-1 rounded-lg text-neutral-400 hover:text-neutral-600 dark:hover:text-neutral-200 shrink-0 disabled:opacity-50"
          >
            <X className="w-5 h-5" />
          </button>
        </div>
        <div className="p-4 sm:p-5 flex-1 overflow-y-auto space-y-4">{children}</div>
        <div className="flex flex-wrap items-center justify-end gap-2 p-4 border-t border-neutral-200 dark:border-dark-border bg-neutral-50 dark:bg-dark-card">
          {footer}
        </div>
      </Card>
    </div>
  );
};

/** Inline error/warning/success banner used inside the dialogs and on the Users page. */
const BANNER_CLASS: Record<'error' | 'warning' | 'success', string> = {
  error: 'border-rose-200 dark:border-rose-900 bg-rose-50 dark:bg-rose-950/30 text-rose-700 dark:text-rose-300',
  warning: 'border-amber-200 dark:border-amber-900 bg-amber-50 dark:bg-amber-950/30 text-amber-800 dark:text-amber-300',
  success: 'border-emerald-200 dark:border-emerald-900 bg-emerald-50 dark:bg-emerald-950/30 text-emerald-800 dark:text-emerald-300',
};

export const MessageBanner: React.FC<{ tone: 'error' | 'warning' | 'success'; children: React.ReactNode }> = ({ tone, children }) => (
  <div
    role={tone === 'error' ? 'alert' : tone === 'warning' ? 'note' : 'status'}
    className={`p-3 rounded-lg border text-xs break-words ${BANNER_CLASS[tone]}`}
  >
    {children}
  </div>
);
