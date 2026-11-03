import React from 'react';
import ReactECharts from 'echarts-for-react';
import { useLocale } from '@/lib/i18n';
import { useThemeTokens } from '@/hooks/useThemeTokens';
import { formatNumber, formatPercent } from '@/lib/format';
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
  const tokens = useThemeTokens();
  const locale = useLocale();

  const labels = {
    normal: locale === 'tr' ? 'Normal' : 'Normal',
    warning: locale === 'tr' ? 'Uyarı' : 'Warning',
    critical: locale === 'tr' ? 'Kritik' : 'Critical',
    unknown: locale === 'tr' ? 'Bilinmeyen' : 'Unknown',
    health: locale === 'tr' ? 'Sistem Sağlığı Dağılımı' : 'System Health Distribution',
    total: locale === 'tr' ? 'Toplam' : 'Total',
    components: locale === 'tr' ? 'Bileşen' : 'Components',
    healthyShare: locale === 'tr' ? 'Sağlıklı' : 'Healthy',
  };

  const total = data.healthy + data.warning + data.critical + data.unknown;

  const option = {
    backgroundColor: 'transparent',
    tooltip: {
      trigger: 'item',
      backgroundColor: tokens.surface,
      borderColor: tokens.border,
      textStyle: {
        color: tokens.text,
      },
      formatter: '{b}: {c} ({d}%)',
    },
    legend: {
      bottom: '2%',
      left: 'center',
      textStyle: {
        color: tokens.textMuted,
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
          borderColor: tokens.surface,
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
            color: tokens.text,
            formatter: '{b}\n{c}',
          },
        },
        data: [
          { value: data.healthy, name: labels.normal, itemStyle: { color: tokens.healthy } },
          { value: data.warning, name: labels.warning, itemStyle: { color: tokens.warning } },
          { value: data.critical, name: labels.critical, itemStyle: { color: tokens.critical } },
          { value: data.unknown, name: labels.unknown, itemStyle: { color: tokens.unknown } },
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
          {labels.total}: {formatNumber(total)} {labels.components}
        </span>
      </div>

      {/* Fixed height: the overlay label is centred on the same box ECharts
          draws in. With a stretching container they drift apart. */}
      <div className="w-full h-[300px] relative my-auto">
        <ReactECharts
          option={option}
          notMerge
          style={{ height: '100%', width: '100%' }}
          opts={{ renderer: 'svg' }}
        />
        <div className="absolute inset-0 flex flex-col items-center justify-center pointer-events-none pb-[30px]">
          <span className="text-2xl font-black text-neutral-900 dark:text-white">
            {total > 0 ? formatPercent((data.healthy / total) * 100) : '—'}
          </span>
          <span className="text-[11px] uppercase font-bold text-emerald-600 dark:text-emerald-400">
            {labels.healthyShare}
          </span>
        </div>
      </div>
    </Card>
  );
};