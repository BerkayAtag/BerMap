import React from 'react';
import { ArrowUpRight, ArrowDownRight, Minus } from 'lucide-react';
import { Card } from '@/components/ui/Card';

interface KPICardProps {
  title: string;
  value: string | number;
  subtitle?: string;
  change?: string;
  trend?: 'up' | 'down' | 'neutral';
  icon: React.ComponentType<{ className?: string }>;
  accent?: 'primary' | 'healthy' | 'warning' | 'critical' | 'info';
}

export const KPICard: React.FC<KPICardProps> = ({
  title,
  value,
  subtitle,
  change,
  trend = 'neutral',
  icon: Icon,
  accent = 'primary',
}) => {
  const accentClasses = {
    primary: 'text-brand-primary bg-brand-primary/10 dark:bg-brand-primary-dark/20 dark:text-brand-primary-dark',
    healthy: 'text-emerald-600 bg-emerald-50 dark:bg-emerald-950/40 dark:text-emerald-400',
    warning: 'text-amber-600 bg-amber-50 dark:bg-amber-950/40 dark:text-amber-400',
    critical: 'text-rose-600 bg-rose-50 dark:bg-rose-950/40 dark:text-rose-400',
    info: 'text-blue-600 bg-blue-50 dark:bg-blue-950/40 dark:text-blue-400',
  };

  return (
    <Card className="p-5 flex flex-col justify-between hover:border-neutral-300 dark:hover:border-neutral-700 transition-all">
      <div className="flex items-start justify-between">
        <span className="text-xs font-semibold uppercase tracking-wider text-neutral-500 dark:text-neutral-400">
          {title}
        </span>
        <div className={`p-2.5 rounded-lg ${accentClasses[accent]}`}>
          <Icon className="w-5 h-5" />
        </div>
      </div>

      <div className="mt-3">
        <div className="text-2xl font-bold tracking-tight text-neutral-900 dark:text-white">
          {value}
        </div>
        <div className="flex items-center gap-2 mt-1">
          {change && (
            <span
              className={`inline-flex items-center text-xs font-medium ${
                trend === 'up'
                  ? 'text-emerald-600 dark:text-emerald-400'
                  : trend === 'down'
                  ? 'text-rose-600 dark:text-rose-400'
                  : 'text-neutral-500 dark:text-neutral-400'
              }`}
            >
              {trend === 'up' && <ArrowUpRight className="w-3.5 h-3.5 mr-0.5" />}
              {trend === 'down' && <ArrowDownRight className="w-3.5 h-3.5 mr-0.5" />}
              {trend === 'neutral' && <Minus className="w-3.5 h-3.5 mr-0.5" />}
              {change}
            </span>
          )}
          {subtitle && (
            <span className="text-xs text-neutral-400 dark:text-neutral-500">
              {subtitle}
            </span>
          )}
        </div>
      </div>
    </Card>
  );
};