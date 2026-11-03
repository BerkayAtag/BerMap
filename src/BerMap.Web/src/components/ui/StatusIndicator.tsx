import { cn } from '@/lib/cn';
import type { NodeStatus } from '@/types';
import {
  CheckCircle2,
  AlertTriangle,
  XCircle,
  HelpCircle,
} from 'lucide-react';
import type { LucideIcon } from 'lucide-react';

interface StatusIndicatorProps {
  status: NodeStatus;
  showLabel?: boolean;
  showIcon?: boolean;
  size?: 'sm' | 'md' | 'lg';
  className?: string;
}

const statusConfig: Record<NodeStatus, { label: string; dotClass: string; Icon: LucideIcon }> = {
  healthy: { label: 'Healthy', dotClass: 'bg-status-healthy', Icon: CheckCircle2 },
  warning: { label: 'Warning', dotClass: 'bg-status-warning', Icon: AlertTriangle },
  critical: { label: 'Critical', dotClass: 'bg-status-critical', Icon: XCircle },
  unknown: { label: 'Unknown', dotClass: 'bg-status-unknown', Icon: HelpCircle },
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
}: StatusIndicatorProps) {
  const config = statusConfig[status];
  const sizing = sizeConfig[size];
  const { Icon } = config;

  return (
    <span className={cn('inline-flex items-center gap-1.5', className)} role="status" aria-label={config.label}>
      {showIcon ? (
        <Icon size={sizing.icon} className={cn('shrink-0', config.dotClass.replace('bg-', 'text-'))} />
      ) : (
        <span className={cn('shrink-0 rounded-full', config.dotClass, sizing.dot)} />
      )}
      {showLabel && (
        <span className={cn('font-medium', sizing.text, 'text-neutral-700 dark:text-dark-text-secondary')}>
          {config.label}
        </span>
      )}
    </span>
  );
}
