import { cn } from '@/lib/cn';
import type { NodeStatus } from '@/types';
import {
  CheckCircle2,
  AlertTriangle,
  XCircle,
  HelpCircle,
} from 'lucide-react';
import type { LucideIcon } from 'lucide-react';
import { useT, type TranslationKey } from '@/lib/i18n-runtime';

interface StatusIndicatorProps {
  status: NodeStatus;
  showLabel?: boolean;
  showIcon?: boolean;
  size?: 'sm' | 'md' | 'lg';
  className?: string;
  /** Overrides the accessible label (defaults to the localized status name). */
  label?: string;
}

const statusConfig: Record<NodeStatus, { labelKey: TranslationKey; dotClass: string; Icon: LucideIcon }> = {
  healthy: { labelKey: 'statusHealthy', dotClass: 'bg-status-healthy', Icon: CheckCircle2 },
  warning: { labelKey: 'statusWarning', dotClass: 'bg-status-warning', Icon: AlertTriangle },
  critical: { labelKey: 'statusCritical', dotClass: 'bg-status-critical', Icon: XCircle },
  unknown: { labelKey: 'statusUnknown', dotClass: 'bg-status-unknown', Icon: HelpCircle },
};

const sizeConfig = {
  sm: { dot: 'h-2 w-2', icon: 14, text: 'text-xs' },
  md: { dot: 'h-2.5 w-2.5', icon: 16, text: 'text-sm' },
  lg: { dot: 'h-3 w-3', icon: 18, text: 'text-base' },
};

export function StatusIndicator({
  status,
  showLabel = false,
  showIcon = false,
  size = 'md',
  className,
  label,
}: StatusIndicatorProps) {
  const t = useT();
  const config = statusConfig[status] ?? statusConfig.unknown;
  const text = label ?? t(config.labelKey);
  const sizing = sizeConfig[size];
  const { Icon } = config;

  return (
    <span className={cn('inline-flex items-center gap-1.5', className)} role="img" aria-label={text}>
      {showIcon ? (
        <Icon size={sizing.icon} className={cn('shrink-0', config.dotClass.replace('bg-', 'text-'))} />
      ) : (
        <span className={cn('shrink-0 rounded-full', config.dotClass, sizing.dot)} />
      )}
      {showLabel && (
        <span className={cn('font-medium', sizing.text, 'text-neutral-700 dark:text-dark-text-secondary')}>
          {text}
        </span>
      )}
    </span>
  );
}
