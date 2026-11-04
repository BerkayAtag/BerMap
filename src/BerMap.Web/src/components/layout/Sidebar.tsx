import React, { useEffect, useRef } from 'react';
import { NavLink, useLocation } from 'react-router-dom';
import {
  LayoutDashboard,
  Network,
  Server,
  Cpu,
  BarChart3,
  FileText,
  PlugZap,
  Radio,
  History,
  Camera,
  Users,
  ChevronLeft,
  ChevronRight,
  X,
} from 'lucide-react';
import { useSidebarStore } from '@/stores/sidebarStore';
import { ROLES, useAuthStore } from '@/stores/authStore';
import { useIsMobile } from '@/hooks/useMediaQuery';
import { useEscapeKey } from '@/hooks/useEscapeKey';
import { useI18n, type TranslationKey } from '@/lib/i18n-runtime';

/** id of the <aside>, referenced by the TopNav hamburger (aria-controls). */
export const SIDEBAR_ID = 'bm-sidebar';

interface NavItem {
  key: TranslationKey;
  path: string;
  icon: React.ComponentType<{ className?: string }>;
  /** When set, only these roles see the item. */
  roles?: string[];
}

const NAV_ITEMS: NavItem[] = [
  { key: 'dashboard', path: '/', icon: LayoutDashboard },
  { key: 'infrastructureMap', path: '/map', icon: Network },
  { key: 'inventory', path: '/inventory', icon: Server },
  { key: 'simulation', path: '/simulation', icon: Cpu },
  { key: 'snapshots', path: '/snapshots', icon: Camera },
  { key: 'capacityIntelligence', path: '/capacity', icon: BarChart3 },
  { key: 'reports', path: '/reports', icon: FileText },
  { key: 'integrations', path: '/integrations', icon: PlugZap },
  { key: 'collectors', path: '/collectors', icon: Radio },
  // Matches the API's ViewAudit policy.
  { key: 'auditLogs', path: '/audit', icon: History, roles: [ROLES.PlatformAdministrator, ROLES.SecurityAuditor] },
  { key: 'users', path: '/users', icon: Users, roles: [ROLES.PlatformAdministrator] },
];

export const Sidebar: React.FC = () => {
  const { isCollapsed, toggle, isMobileOpen, closeMobile } = useSidebarStore();
  const role = useAuthStore((state) => state.user?.role);
  const isMobile = useIsMobile();
  const { t } = useI18n();
  const location = useLocation();
  const asideRef = useRef<HTMLElement>(null);

  // The drawer always shows labels; icon-only mode applies to the desktop rail.
  const collapsed = isCollapsed && !isMobile;
  const drawerOpen = isMobile && isMobileOpen;

  // Any navigation (link click, back/forward, search result) closes the drawer.
  useEffect(() => {
    closeMobile();
  }, [location.pathname, location.search, closeMobile]);

  // Leaving the small-screen layout must not leave a stale open drawer behind.
  useEffect(() => {
    if (!isMobile) closeMobile();
  }, [isMobile, closeMobile]);

  useEscapeKey(drawerOpen, closeMobile);

  useEffect(() => {
    if (drawerOpen) asideRef.current?.querySelector<HTMLElement>('a, button')?.focus();
  }, [drawerOpen]);

  const items = NAV_ITEMS.filter((item) => !item.roles || (role !== undefined && item.roles.includes(role)));

  return (
    <>
      {drawerOpen && (
        <div
          className="fixed inset-0 z-40 bg-black/50 backdrop-blur-xs md:hidden bm-fade-in"
          aria-hidden="true"
          onClick={closeMobile}
        />
      )}
      <aside
        id={SIDEBAR_ID}
        ref={asideRef}
        // Off-canvas on small screens: hidden from focus and assistive tech while closed.
        inert={isMobile && !isMobileOpen}
        aria-label={isMobile ? t('mainNavigation') : undefined}
        className={`
          flex flex-col border-r select-none z-50 transition-all duration-300
          fixed inset-y-0 left-0 w-64 max-w-[85vw] shadow-2xl
          ${isMobileOpen ? 'translate-x-0' : '-translate-x-full'}
          md:relative md:inset-auto md:translate-x-0 md:shadow-none md:max-w-none md:z-40
          ${collapsed ? 'md:w-18' : 'md:w-64'}
          bg-white border-neutral-200 text-neutral-800
          dark:bg-dark-sidebar dark:border-dark-border dark:text-dark-text
        `}
      >
        {/* Brand Header */}
        <div className="flex items-center justify-between h-16 px-4 border-b border-neutral-200 dark:border-dark-border">
          <div className="flex items-center gap-3 overflow-hidden">
            <div className="flex items-center justify-center w-9 h-9 rounded-lg bg-brand-primary text-white font-black text-xl shadow-md flex-shrink-0">
              B
            </div>
            {!collapsed && (
              <div className="flex flex-col min-w-0">
                <span className="font-bold text-lg leading-tight tracking-tight text-neutral-900 dark:text-white">
                  Ber<span className="text-brand-primary dark:text-brand-primary-dark">Map</span>
                </span>
                <span className="text-[10px] uppercase font-semibold tracking-wider text-neutral-500 dark:text-neutral-400 truncate">
                  {t('enterprise')}
                </span>
              </div>
            )}
          </div>
          {isMobile && (
            <button
              type="button"
              onClick={closeMobile}
              aria-label={t('closeNavigation')}
              className="inline-flex items-center justify-center w-9 h-9 rounded-lg text-neutral-500 hover:bg-neutral-100 dark:hover:bg-neutral-800 focus:outline-none focus:ring-2 focus:ring-brand-primary"
            >
              <X className="w-5 h-5" aria-hidden="true" />
            </button>
          )}
        </div>

        {/* Navigation List */}
        <nav aria-label={t('mainNavigation')} className="flex-1 overflow-y-auto px-3 py-4 space-y-1">
          {items.map((item) => {
            const Icon = item.icon;
            const label = t(item.key);
            return (
              <NavLink
                key={item.path}
                to={item.path}
                end={item.path === '/'}
                title={collapsed ? label : undefined}
                aria-label={collapsed ? label : undefined}
                className={({ isActive }) => `
                  group flex items-center gap-3 px-3 py-2.5 rounded-lg text-sm font-medium transition-all duration-150
                  ${
                    isActive
                      ? 'bg-brand-primary text-white shadow-sm'
                      : 'text-neutral-600 hover:text-neutral-900 hover:bg-neutral-100 dark:text-neutral-300 dark:hover:text-white dark:hover:bg-neutral-800/60'
                  }
                `}
              >
                {({ isActive }) => (
                  <>
                    <Icon
                      className={`w-5 h-5 flex-shrink-0 transition-colors ${
                        isActive ? 'text-white' : 'text-neutral-500 group-hover:text-brand-primary dark:text-neutral-400 dark:group-hover:text-brand-primary-dark'
                      }`}
                    />
                    {!collapsed && <span className="truncate flex-1">{label}</span>}
                  </>
                )}
              </NavLink>
            );
          })}
        </nav>

        {/* Collapse Toggle Footer (desktop rail only) */}
        <div className="hidden md:block p-3 border-t border-neutral-200 dark:border-dark-border">
          <button
            type="button"
            onClick={toggle}
            aria-label={collapsed ? t('expandSidebar') : t('collapseSidebar')}
            aria-expanded={!collapsed}
            className="flex items-center justify-center w-full h-9 rounded-lg border border-neutral-200 dark:border-dark-border text-neutral-600 dark:text-neutral-400 hover:bg-neutral-100 dark:hover:bg-neutral-800 transition-colors"
          >
            {collapsed ? <ChevronRight className="w-4 h-4" /> : <ChevronLeft className="w-4 h-4" />}
            {!collapsed && <span className="ml-2 text-xs font-medium">{t('collapseSidebar')}</span>}
          </button>
        </div>
      </aside>
    </>
  );
};
