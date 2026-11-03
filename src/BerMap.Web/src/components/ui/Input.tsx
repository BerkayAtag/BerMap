import { type InputHTMLAttributes, forwardRef } from 'react';
import { cn } from '@/lib/cn';

interface InputProps extends InputHTMLAttributes<HTMLInputElement> {
  label?: string;
  error?: string;
}

export const Input = forwardRef<HTMLInputElement, InputProps>(
  ({ className, label, error, id, ...props }, ref) => {
    const inputId = id || label?.toLowerCase().replace(/\s+/g, '-');

    return (
      <div className="flex flex-col gap-1.5">
        {label && (
          <label
            htmlFor={inputId}
            className="text-sm font-medium text-neutral-700 dark:text-dark-text-secondary"
          >
            {label}
          </label>
        )}
        <input
          ref={ref}
          id={inputId}
          className={cn(
            'h-10 w-full rounded-md border px-3 text-sm',
            'bg-white text-neutral-900 placeholder:text-neutral-400',
            'border-neutral-300 focus:border-brand-primary focus:ring-2 focus:ring-brand-primary/20',
            'dark:bg-dark-card dark:text-dark-text dark:border-dark-border',
            'dark:placeholder:text-neutral-500 dark:focus:border-brand-primary-dark',
            'outline-none transition-colors duration-150',
            error && 'border-status-critical focus:border-status-critical focus:ring-status-critical/20',
            className,
          )}
          aria-invalid={error ? 'true' : undefined}
          aria-describedby={error ? `${inputId}-error` : undefined}
          {...props}
        />
        {error && (
          <p id={`${inputId}-error`} className="text-xs text-status-critical" role="alert">
            {error}
          </p>
        )}
      </div>
    );
  },
);

Input.displayName = 'Input';
