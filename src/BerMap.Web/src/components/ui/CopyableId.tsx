import React, { useEffect, useId, useRef, useState } from 'react';
import { Check, Copy } from 'lucide-react';
import { copyText } from '@/lib/clipboard';

type CopyState = 'idle' | 'copied' | 'failed';

/** Copy state with the "Copied" feedback cleared after a moment. */
function useCopy(value: string) {
  const [state, setState] = useState<CopyState>('idle');
  const timer = useRef<number | undefined>(undefined);

  useEffect(() => () => window.clearTimeout(timer.current), []);

  const copy = async (): Promise<boolean> => {
    window.clearTimeout(timer.current);
    const ok = await copyText(value);
    setState(ok ? 'copied' : 'failed');
    if (ok) timer.current = window.setTimeout(() => setState('idle'), 2500);
    return ok;
  };

  return { state, setState, copy };
}

function selectAll(event: React.SyntheticEvent<HTMLInputElement>): void {
  event.currentTarget.select();
}

const fieldClass =
  'min-w-0 w-full rounded-md border px-2 py-1 font-mono text-[11px] bg-white text-neutral-900 border-neutral-300 ' +
  'focus:border-brand-primary focus:ring-2 focus:ring-brand-primary/20 outline-none ' +
  'dark:bg-dark-card dark:text-dark-text dark:border-dark-border';

interface CopyIdChipProps {
  value: string;
  isTr: boolean;
  /** Tooltip / accessible description of what is copied. */
  title: string;
}

/**
 * A short id chip that copies the full id. When the browser refuses the copy
 * (plain HTTP without a clipboard, permission denied), the full id appears in
 * a selected read-only field so it can be copied by hand.
 */
export const CopyIdChip: React.FC<CopyIdChipProps> = ({ value, isTr, title }) => {
  const { state, setState, copy } = useCopy(value);
  const fieldRef = useRef<HTMLInputElement>(null);
  const hintId = useId();

  useEffect(() => {
    if (state === 'failed') {
      fieldRef.current?.focus();
      fieldRef.current?.select();
    }
  }, [state]);

  return (
    <span className="inline-flex flex-col items-start gap-1 max-w-full">
      <span className="inline-flex items-center gap-1.5">
        <button
          type="button"
          className="inline-flex items-center gap-1 px-2 py-0.5 rounded-full border border-neutral-200 dark:border-dark-border text-[10px] font-mono text-neutral-600 dark:text-neutral-400 hover:border-brand-primary"
          title={title}
          aria-label={`${title}: ${value}`}
          onClick={() => {
            if (state === 'failed') setState('idle');
            else void copy();
          }}
        >
          {state === 'copied' ? <Check className="w-3 h-3 text-emerald-600" /> : <Copy className="w-3 h-3" />}
          {value.slice(0, 8)}…
        </button>
        <span role="status" className="text-[10px] font-semibold text-emerald-700 dark:text-emerald-400">
          {state === 'copied' ? (isTr ? 'Kopyalandı' : 'Copied') : ''}
        </span>
      </span>
      {state === 'failed' && (
        <span className="flex flex-col gap-0.5 w-full max-w-[22rem]">
          <input
            ref={fieldRef}
            readOnly
            value={value}
            aria-label={isTr ? 'Entegrasyon kimliği' : 'Integration id'}
            aria-describedby={hintId}
            className={fieldClass}
            onFocus={selectAll}
            onClick={selectAll}
            onKeyDown={(event) => {
              if (event.key === 'Escape') {
                event.preventDefault();
                setState('idle');
              }
            }}
          />
          <span id={hintId} role="status" className="text-[10px] text-amber-700 dark:text-amber-300">
            {isTr
              ? 'Tarayıcı panoya kopyalamaya izin vermedi; seçili kimliği Ctrl+C (⌘C) ile kopyalayın.'
              : 'The browser did not allow copying; copy the selected id with Ctrl+C (⌘C).'}
          </span>
        </span>
      )}
    </span>
  );
};

interface CopyIdFieldProps {
  id?: string;
  value: string;
  isTr: boolean;
  label: string;
  hint?: string;
}

/** The full id in a selectable read-only field with a copy button and its feedback. */
export const CopyIdField: React.FC<CopyIdFieldProps> = ({ id, value, isTr, label, hint }) => {
  const { state, copy } = useCopy(value);
  const generatedId = useId();
  const fieldId = id ?? generatedId;
  const fieldRef = useRef<HTMLInputElement>(null);

  return (
    <div>
      <label htmlFor={fieldId} className="block text-xs font-semibold text-neutral-600 dark:text-neutral-400 mb-1">
        {label}
      </label>
      <div className="flex items-center gap-2">
        <input
          ref={fieldRef}
          id={fieldId}
          readOnly
          value={value}
          className={`${fieldClass} h-9 text-xs`}
          onFocus={selectAll}
          onClick={selectAll}
        />
        <button
          type="button"
          className="shrink-0 inline-flex items-center gap-1 h-9 px-2.5 rounded-md border border-neutral-300 dark:border-dark-border text-xs font-semibold text-neutral-700 dark:text-neutral-300 hover:border-brand-primary"
          onClick={() =>
            void copy().then((ok) => {
              if (!ok) {
                fieldRef.current?.focus();
                fieldRef.current?.select();
              }
            })
          }
        >
          {state === 'copied' ? <Check className="w-3.5 h-3.5 text-emerald-600" /> : <Copy className="w-3.5 h-3.5" />}
          {isTr ? 'Kopyala' : 'Copy'}
        </button>
      </div>
      <p role="status" className="text-[11px] mt-1 text-neutral-500">
        {state === 'copied'
          ? <span className="font-semibold text-emerald-700 dark:text-emerald-400">{isTr ? 'Kopyalandı.' : 'Copied.'}</span>
          : state === 'failed'
            ? <span className="text-amber-700 dark:text-amber-300">
                {isTr
                  ? 'Tarayıcı panoya kopyalamaya izin vermedi; seçili kimliği Ctrl+C (⌘C) ile kopyalayın.'
                  : 'The browser did not allow copying; copy the selected id with Ctrl+C (⌘C).'}
              </span>
            : hint}
      </p>
    </div>
  );
};
