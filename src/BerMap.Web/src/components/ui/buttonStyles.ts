import { cn } from '@/lib/cn';

export type ButtonVariant = 'primary' | 'secondary' | 'outline' | 'ghost' | 'danger';
export type ButtonSize = 'sm' | 'md' | 'lg';

const variantStyles: Record<ButtonVariant, string> = {
  primary:
    'bg-brand-primary text-white not-disabled:hover:bg-brand-primary-hover focus-visible:ring-brand-primary dark:bg-brand-primary-dark dark:not-disabled:hover:bg-brand-primary',
  secondary:
    'bg-neutral-200 text-neutral-800 not-disabled:hover:bg-neutral-300 dark:bg-dark-card dark:text-dark-text dark:not-disabled:hover:bg-dark-border',
  outline:
    'border border-neutral-300 text-neutral-700 not-disabled:hover:bg-neutral-100 dark:border-dark-border dark:text-dark-text dark:not-disabled:hover:bg-dark-card',
  ghost:
    'text-neutral-600 not-disabled:hover:bg-neutral-100 not-disabled:hover:text-neutral-900 dark:text-dark-text-secondary dark:not-disabled:hover:bg-dark-card dark:not-disabled:hover:text-dark-text',
  danger:
    'bg-status-critical text-white not-disabled:hover:bg-red-600 focus-visible:ring-status-critical',
};

const sizeStyles: Record<ButtonSize, string> = {
  sm: 'h-8 px-3 text-sm gap-1.5',
  md: 'h-10 px-4 text-sm gap-2',
  lg: 'h-12 px-6 text-base gap-2.5',
};

/** Class names for button-styled elements (buttons and links rendered as buttons). */
export function buttonClassName(
  variant: ButtonVariant = 'primary',
  size: ButtonSize = 'md',
  className?: string,
): string {
  return cn(
    'inline-flex items-center justify-center rounded-md font-medium',
    'transition-colors duration-150',
    'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-offset-2',
    // Disabled buttons keep hover so their title can explain why (e.g. missing permission).
    'disabled:cursor-not-allowed disabled:opacity-50',
    variantStyles[variant],
    sizeStyles[size],
    className,
  );
}
