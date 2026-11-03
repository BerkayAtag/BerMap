import type { HTMLAttributes } from 'react';
import { cn } from '@/lib/cn';
import type { NodeStatus } from '@/types';

type BadgeVariant = NodeStatus | 'info';

interface BadgeProps extends HTMLAttributes<HTMLSpanElement> {
  variant?: BadgeVariant;
}

const variantStyles: Record<BadgeVariant, string> = {
  healthy: 'bg-status-healthy-bg text-status-healthy dark:bg-status-healthy/15 dark:text-emerald-400',
  warning: 'bg-status-warning-bg text-status-warning dark:bg-status-warning/15 dark:text-amber-400',
  critical: 'bg-status-critical-bg text-status-critical dark:bg-status-critical/15 dark:text-red-400',
  unknown: 'bg-status-unknown-bg text-status-unknown dark:bg-neutral-700 dark:text-neutral-400',
  info: 'bg-status-info-bg text-status-info dark:bg-status-info/15 dark:text-blue-400',
};

export function Badge({ className, variant = 'info', children, ...props }: BadgeProps) {
  return (
    <span
      className={cn(
        'inline-flex items-center rounded-full px-2.5 py-0.5',
        'text-xs font-medium',
        variantStyles[variant],
        className,
      )}
      {...props}
    >
      {children}
    </span>
  );
}
