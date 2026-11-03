import React, { useEffect } from 'react';
import { BrowserRouter, Routes, Route } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { AppLayout } from '@/components/layout/AppLayout';
import { DashboardPage } from '@/pages/DashboardPage';
import { InfrastructureMapPage } from '@/pages/InfrastructureMapPage';
import { InventoryPage } from '@/pages/InventoryPage';
import { SimulationStudioPage } from '@/pages/SimulationStudioPage';
import { ImpactAnalysisPage } from '@/pages/ImpactAnalysisPage';
import { IntegrationsPage } from '@/pages/IntegrationsPage';
import { CollectorsPage } from '@/pages/CollectorsPage';
import { SnapshotsPage } from '@/pages/SnapshotsPage';
import { LoginPage } from '@/pages/LoginPage';
import { NotFoundPage } from '@/pages/NotFoundPage';
import { CapacityPage } from '@/pages/CapacityPage';
import { EventsPage } from '@/pages/EventsPage';
import { ReportsPage } from '@/pages/ReportsPage';
import { AuditPage } from '@/pages/AuditPage';
import { useThemeStore } from '@/stores/themeStore';
import { useLocaleStore } from '@/stores/localeStore';

const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      refetchOnWindowFocus: false,
      retry: 1,
    },
  },
});

export const App: React.FC = () => {
  const { theme } = useThemeStore();
  const { locale } = useLocaleStore();

  useEffect(() => {
    document.documentElement.setAttribute('data-theme', theme);
  }, [theme]);

  useEffect(() => {
    document.documentElement.setAttribute('data-locale', locale);
    document.documentElement.lang = locale;
  }, [locale]);

  return (
    <QueryClientProvider client={queryClient}>
      <BrowserRouter>
        <Routes>
          <Route path="/login" element={<LoginPage />} />

          <Route path="/" element={<AppLayout />}>
            <Route index element={<DashboardPage />} />
            <Route path="map" element={<InfrastructureMapPage />} />
            <Route path="inventory" element={<InventoryPage />} />
            <Route path="dependencies" element={<InfrastructureMapPage />} />
            <Route path="impact" element={<ImpactAnalysisPage />} />
            <Route path="simulation" element={<SimulationStudioPage />} />
            <Route path="snapshots" element={<SnapshotsPage />} />
            <Route path="capacity" element={<CapacityPage />} />
            <Route path="events" element={<EventsPage />} />
            <Route path="reports" element={<ReportsPage />} />
            <Route path="integrations" element={<IntegrationsPage />} />
            <Route path="collectors" element={<CollectorsPage />} />
            <Route path="admin" element={<IntegrationsPage />} />
            <Route path="audit" element={<AuditPage />} />
            <Route path="*" element={<NotFoundPage />} />
          </Route>
        </Routes>
      </BrowserRouter>
    </QueryClientProvider>
  );
};

export default App;