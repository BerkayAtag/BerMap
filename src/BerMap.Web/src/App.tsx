import React, { useEffect } from 'react';
import { BrowserRouter, Routes, Route, Navigate, useLocation } from 'react-router-dom';
import { QueryClientProvider } from '@tanstack/react-query';
import { AppLayout } from '@/components/layout/AppLayout';
import { ProtectedRoute } from '@/components/auth/ProtectedRoute';
import { queryClient } from '@/lib/queryClient';
import { DashboardPage } from '@/pages/DashboardPage';
import { InfrastructureMapPage } from '@/pages/InfrastructureMapPage';
import { InventoryPage } from '@/pages/InventoryPage';
import { SimulationStudioPage } from '@/pages/SimulationStudioPage';
import { IntegrationsPage } from '@/pages/IntegrationsPage';
import { CollectorsPage } from '@/pages/CollectorsPage';
import { SnapshotsPage } from '@/pages/SnapshotsPage';
import { LoginPage } from '@/pages/LoginPage';
import { NotFoundPage } from '@/pages/NotFoundPage';
import { CapacityPage } from '@/pages/CapacityPage';
import { ReportsPage } from '@/pages/ReportsPage';
import { AuditPage } from '@/pages/AuditPage';
import { UsersPage } from '@/pages/UsersPage';
import { useThemeStore } from '@/stores/themeStore';
import { useLocaleStore } from '@/stores/localeStore';
import { ROLES, useAuthStore } from '@/stores/authStore';

/** Impact Analysis was merged into Simulation Studio; old links keep their ?target=&scenario=. */
const ImpactRedirect: React.FC = () => {
  const { search } = useLocation();
  return <Navigate to={{ pathname: '/simulation', search }} replace />;
};

/** User management is for platform administrators only; others get the 404 page. */
const AdminOnly: React.FC<{ children: React.ReactElement }> = ({ children }) => {
  const role = useAuthStore((state) => state.user?.role);
  return role === ROLES.PlatformAdministrator ? children : <NotFoundPage />;
};

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

          <Route element={<ProtectedRoute />}>
            <Route path="/" element={<AppLayout />}>
              <Route index element={<DashboardPage />} />
              <Route path="map" element={<InfrastructureMapPage />} />
              <Route path="inventory" element={<InventoryPage />} />
              <Route path="dependencies" element={<Navigate to="/map" replace />} />
              <Route path="impact" element={<ImpactRedirect />} />
              <Route path="simulation" element={<SimulationStudioPage />} />
              <Route path="snapshots" element={<SnapshotsPage />} />
              <Route path="capacity" element={<CapacityPage />} />
              <Route path="events" element={<Navigate to="/" replace />} />
              <Route path="reports" element={<ReportsPage />} />
              <Route path="integrations" element={<IntegrationsPage />} />
              <Route path="collectors" element={<CollectorsPage />} />
              <Route path="admin" element={<Navigate to="/integrations" replace />} />
              <Route path="audit" element={<AuditPage />} />
              <Route
                path="users"
                element={
                  <AdminOnly>
                    <UsersPage />
                  </AdminOnly>
                }
              />
              <Route path="*" element={<NotFoundPage />} />
            </Route>
          </Route>
        </Routes>
      </BrowserRouter>
    </QueryClientProvider>
  );
};

export default App;