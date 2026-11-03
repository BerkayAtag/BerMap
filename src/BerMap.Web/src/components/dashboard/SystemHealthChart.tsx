import React from 'react';
import ReactECharts from 'echarts-for-react';
import { useThemeStore } from '@/stores/themeStore';
import { useLocale } from '@/lib/i18n';
import { Card } from '@/components/ui/Card';

interface HealthData {
  healthy: number;
  warning: number;
  critical: number;
  unknown: number;
}

interface SystemHealthChartProps {
  data?: HealthData;
}

export const SystemHealthChart: React.FC<SystemHealthChartProps> = ({
  data = { healthy: 0, warning: 0, critical: 0, unknown: 0 },
}) => {
  const { theme } = useThemeStore();
  const isDark = theme === 'dark';
  const locale = useLocale();

  const labels = {
    normal: locale === 'tr' ? 'Normal' : 'Normal',
    warning: locale === 'tr' ? 'Uyarı' : 'Warning',
    critical: locale === 'tr' ? 'Kritik' : 'Critical',
    unknown: locale === 'tr' ? 'Bilinmeyen' : 'Unknown',
    health: locale === 'tr' ? 'Sistem Sağlığı Dağılımı' : 'System Health Distribution',
    total: locale === 'tr' ? 'Toplam' : 'Total',
    components: locale === 'tr' ? 'Bileşen' : 'Components',
    accessibility: locale === 'tr' ? 'Erişilebilirlik' : 'Accessibility',
  };

  const total = data.healthy + data.warning + data.critical + data.unknown;

  const option = {
    backgroundColor: 'transparent',
    tooltip: {
      trigger: 'item',
      backgroundColor: isDark ? '#20242D' : '#FFFFFF',
      borderColor: isDark ? '#343945' : '#E4E6EB',
      textStyle: {
        color: isDark ? '#F5F6F8' : '#17191F',
      },
      formatter: '{b}: {c} ({d}%)',
    },
    legend: {
      bottom: '2%',
      left: 'center',
      textStyle: {
        color: isDark ? '#AEB4C0' : '#4B5563',
        fontSize: 12,
      },
      itemWidth: 10,
      itemHeight: 10,
    },
    series: [
      {
        name: labels.health,
        type: 'pie',
        radius: ['58%', '80%'],
        center: ['50%', '45%'],
        avoidLabelOverlap: false,
        itemStyle: {
          borderRadius: 6,
          borderColor: isDark ? '#111318' : '#FFFFFF',
          borderWidth: 3,
        },
        label: {
          show: false,
        },
        emphasis: {
          label: {
            show: true,
            fontSize: 16,
            fontWeight: 'bold',
            color: isDark ? '#FFFFFF' : '#17191F',
            formatter: '{b}\n{c}',
          },
        },
        data: [
          { value: data.healthy, name: labels.normal, itemStyle: { color: '#10B981' } },
          { value: data.warning, name: labels.warning, itemStyle: { color: '#F59E0B' } },
          { value: data.critical, name: labels.critical, itemStyle: { color: '#E30613' } },
          { value: data.unknown, name: labels.unknown, itemStyle: { color: '#6B7280' } },
        ],
      },
    ],
  };

  return (
    <Card className="p-5 flex flex-col h-full">
      <div className="flex items-center justify-between mb-2">
        <h3 className="font-semibold text-base text-neutral-900 dark:text-white">
          {labels.health}
        </h3>
        <span className="text-xs px-2 py-0.5 rounded-full bg-neutral-100 dark:bg-dark-border text-neutral-600 dark:text-neutral-400 font-medium">
          {labels.total}: {total.toLocaleString()} {labels.components}
        </span>
      </div>

      <div className="flex-1 w-full min-h-[260px] relative">
        <ReactECharts
          option={option}
          style={{ height: '100%', width: '100%' }}
          opts={{ renderer: 'svg' }}
        />
        <div className="absolute inset-0 flex flex-col items-center justify-center pointer-events-none pb-8">
          <span className="text-2xl font-black text-neutral-900 dark:text-white">
            {total > 0 ? `${((data.healthy / total) * 100).toFixed(1)}%` : '—'}
          </span>
          <span className="text-[11px] uppercase font-bold text-emerald-600 dark:text-emerald-400">
            {labels.accessibility}
          </span>
        </div>
      </div>
    </Card>
  );
};