import React, { useEffect, useId, useRef, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import { Loader2, LogOut, Search, Server, Shield } from 'lucide-react';
import { ThemeToggle } from './ThemeToggle';
import { LocaleToggle } from './LocaleToggle';
import { StatusIndicator } from '@/components/ui/StatusIndicator';
import { useT, type TranslationKey } from '@/lib/i18n-runtime';
import { useDashboard } from '@/hooks/useDashboard';
import { useAuthStore } from '@/stores/authStore';
import { api } from '@/lib/api';
import { cn } from '@/lib/cn';
import type { InfrastructureNode, NodeStatus } from '@/types';

const ROLE_KEYS: Record<string, TranslationKey> = {
  PlatformAdministrator: 'rolePlatformAdministrator',
  IntegrationAdministrator: 'roleIntegrationAdministrator',
  InfrastructureOperator: 'roleInfrastructureOperator',
  SecurityAuditor: 'roleSecurityAuditor',
  ReadOnlyViewer: 'roleReadOnlyViewer',
};

function getInitials(name: string): string {
  const parts = name.trim().split(/[\s._-]+/).filter(Boolean);
  if (parts.length === 0) return '?';
  if (parts.length === 1) return parts[0].slice(0, 2).toLocaleUpperCase();
  return (parts[0][0] + parts[parts.length - 1][0]).toLocaleUpperCase();
}

const HEALTH_BADGE: Record<NodeStatus, { key: TranslationKey; className: string }> = {
  healthy: {
    key: 'systemHealthNormal',
    className:
      'bg-status-healthy-bg border-emerald-200 dark:border-emerald-800/50 text-emerald-700 dark:text-emerald-400',
  },
  warning: {
    key: 'systemHealthWarning',
    className:
      'bg-status-warning-bg border-amber-200 dark:border-amber-800/50 text-amber-700 dark:text-amber-400',
  },
  critical: {
    key: 'systemHealthCritical',
    className:
      'bg-status-critical-bg border-rose-200 dark:border-rose-800/50 text-rose-700 dark:text-rose-400',
  },
  unknown: {
    key: 'systemHealthUnknown',
    className:
      'bg-status-unknown-bg border-neutral-200 dark:border-dark-border text-neutral-600 dark:text-dark-text-secondary',
  },
};

const GlobalSearch: React.FC = () => {
  const t = useT();
  const navigate = useNavigate();
  const [input, setInput] = useState('');
  const [term, setTerm] = useState('');
  const [open, setOpen] = useState(false);
  const [activeIndex, setActiveIndex] = useState(-1);
  const inputRef = useRef<HTMLInputElement>(null);
  const containerRef = useRef<HTMLDivElement>(null);
  const listId = useId();

  useEffect(() => {
    const timer = window.setTimeout(() => setTerm(input.trim()), 250);
    return () => window.clearTimeout(timer);
  }, [input]);

  // ⌘K / Ctrl+K focuses the search box, as advertised in the placeholder.
  useEffect(() => {
    const onKeyDown = (event: KeyboardEvent) => {
      if ((event.metaKey || event.ctrlKey) && event.key.toLowerCase() === 'k') {
        event.preventDefault();
        inputRef.current?.focus();
        setOpen(true);
      }
    };
    window.addEventListener('keydown', onKeyDown);
    return () => window.removeEventListener('keydown', onKeyDown);
  }, []);

  useEffect(() => {
    if (!open) return;
    const onPointerDown = (event: PointerEvent) => {
      if (!containerRef.current?.contains(event.target as Node)) setOpen(false);
    };
    document.addEventListener('pointerdown', onPointerDown);
    return () => document.removeEventListener('pointerdown', onPointerDown);
  }, [open]);

  const searchQuery = useQuery<InfrastructureNode[]>({
    queryKey: ['graph', 'search', term],
    queryFn: () => api.get<InfrastructureNode[]>(`/graph/search?q=${encodeURIComponent(term)}`),
    enabled: term.length >= 2,
    staleTime: 15_000,
  });

  const results = term.length >= 2 ? (searchQuery.data ?? []).slice(0, 10) : [];
  const showDropdown = open && term.length >= 2;

  const goToNode = (node: InfrastructureNode) => {
    setOpen(false);
    setInput('');
    setTerm('');
    navigate(`/inventory?node=${encodeURIComponent(node.id)}`);
  };

  const goToInventorySearch = () => {
    const value = input.trim();
    if (!value) return;
    setOpen(false);
    navigate(`/inventory?q=${encodeURIComponent(value)}`);
  };

  const onKeyDown = (event: React.KeyboardEvent<HTMLInputElement>) => {
    if (event.key === 'ArrowDown') {
      event.preventDefault();
      setOpen(true);
      setActiveIndex((index) => Math.min(results.length - 1, index + 1));
    } else if (event.key === 'ArrowUp') {
      event.preventDefault();
      setActiveIndex((index) => Math.max(-1, index - 1));
    } else if (event.key === 'Enter') {
      event.preventDefault();
      if (activeIndex >= 0 && results[activeIndex]) goToNode(results[activeIndex]);
      else goToInventorySearch();
    } else if (event.key === 'Escape') {
      // Handled here: page-level Escape handlers (open panels) must ignore it.
      event.preventDefault();
      setOpen(false);
      inputRef.current?.blur();
    }
  };

  return (
    <div className="flex items-center w-96 max-w-full relative" ref={containerRef}>
      <Search className="w-4 h-4 absolute left-3 text-neutral-400 dark:text-neutral-500 pointer-events-none" aria-hidden="true" />
      <input
        ref={inputRef}
        type="search"
        role="combobox"
        aria-label={t('searchLabel')}
        aria-expanded={showDropdown}
        aria-controls={listId}
        aria-autocomplete="list"
        aria-activedescendant={activeIndex >= 0 ? `${listId}-${activeIndex}` : undefined}
        value={input}
        onChange={(event) => {
          setInput(event.target.value);
          setOpen(true);
          setActiveIndex(-1);
        }}
        onFocus={() => setOpen(true)}
        onKeyDown={onKeyDown}
        placeholder={t('searchPlaceholder')}
        className="w-full h-9 pl-9 pr-4 text-sm rounded-lg bg-neutral-100 dark:bg-dark-card border border-neutral-200 dark:border-dark-border text-neutral-900 dark:text-dark-text placeholder-neutral-400 dark:placeholder-neutral-500 focus:outline-none focus:ring-2 focus:ring-brand-primary focus:border-transparent transition-all"
      />

      {showDropdown && (
        <div className="absolute left-0 right-0 top-full mt-1 z-50 rounded-lg border shadow-xl bg-white dark:bg-dark-card border-neutral-200 dark:border-dark-border overflow-hidden">
          {searchQuery.isFetching && results.length === 0 ? (
            <div className="flex items-center gap-2 px-3 py-3 text-xs text-neutral-500">
              <Loader2 className="w-3.5 h-3.5 animate-spin" />
              {t('searching')}
            </div>
          ) : searchQuery.isError ? (
            <div className="px-3 py-3 text-xs text-rose-600 dark:text-rose-400">{t('searchFailed')}</div>
          ) : results.length === 0 ? (
            <div className="px-3 py-3 text-xs text-neutral-500">{t('searchNoResults')}</div>
          ) : (
            <ul id={listId} role="listbox" className="max-h-80 overflow-y-auto py-1">
              {results.map((node, index) => (
                <li
                  key={node.id}
                  id={`${listId}-${index}`}
                  role="option"
                  aria-selected={index === activeIndex}
                  onPointerDown={(event) => event.preventDefault()}
                  onClick={() => goToNode(node)}
                  onMouseEnter={() => setActiveIndex(index)}
                  className={cn(
                    'flex items-center gap-2 px-3 py-2 cursor-pointer text-sm',
                    index === activeIndex
                      ? 'bg-neutral-100 dark:bg-neutral-800'
                      : 'hover:bg-neutral-50 dark:hover:bg-neutral-800/60',
                  )}
                >
                  <Server className="w-4 h-4 text-neutral-400 shrink-0" aria-hidden="true" />
                  <div className="min-w-0 flex-1">
                    <div className="truncate font-medium text-neutral-900 dark:text-white">{node.name}</div>
                    <div className="truncate text-[11px] font-mono text-neutral-500">
                      {node.fqdn || node.ipAddress || node.id}
                    </div>
                  </div>
                  <span className="text-[10px] text-neutral-500 shrink-0">{node.nodeType}</span>
                </li>
              ))}
            </ul>
          )}
        </div>
      )}
    </div>
  );
};

const SystemHealthBadge: React.FC = () => {
  const t = useT();
  const { data, isError } = useDashboard();

  let status: NodeStatus = 'unknown';
  if (data && !isError) {
    if (data.criticalEntities > 0) status = 'critical';
    else if (data.warningEntities > 0) status = 'warning';
    else status = 'healthy';
  }
  const badge = HEALTH_BADGE[status];
  const label = t(badge.key);

  return (
    <div
      className={cn(
        'hidden md:flex items-center gap-2 px-3 py-1.5 rounded-full border text-xs font-medium',
        badge.className,
      )}
      role="status"
    >
      <StatusIndicator status={status} size="sm" label={label} />
      <span>{label}</span>
    </div>
  );
};

export const TopNav: React.FC = () => {
  const t = useT();
  const navigate = useNavigate();
  const user = useAuthStore((state) => state.user);
  const logout = useAuthStore((state) => state.logout);

  const displayName = user?.displayName || user?.username || '—';
  const roleKey = user?.role ? ROLE_KEYS[user.role] : undefined;
  const roleLabel = roleKey ? t(roleKey) : user?.role || t('roleUnknown');

  const handleLogout = () => {
    logout();
    navigate('/login', { replace: true });
  };

  return (
    <header className="h-16 px-6 flex items-center justify-between gap-4 border-b border-neutral-200 dark:border-dark-border bg-white dark:bg-dark-sidebar z-30 transition-colors">
      <GlobalSearch />

      {/* Right Controls */}
      <div className="flex items-center gap-3">
        <SystemHealthBadge />

        <LocaleToggle />
        <ThemeToggle />

        <div className="h-6 w-[1px] bg-neutral-200 dark:bg-dark-border" />

        {/* User Profile */}
        <div className="flex items-center gap-3 pl-1">
          <div
            className="w-8 h-8 rounded-full bg-brand-primary/10 dark:bg-brand-primary-dark/20 text-brand-primary dark:text-brand-primary-dark border border-brand-primary/20 flex items-center justify-center font-bold text-xs"
            aria-hidden="true"
          >
            {getInitials(displayName)}
          </div>
          <div className="hidden lg:flex flex-col text-left">
            <span className="text-xs font-semibold text-neutral-900 dark:text-white leading-tight">
              {displayName}
            </span>
            <span className="text-[10px] text-neutral-500 dark:text-neutral-400 flex items-center gap-1">
              <Shield className="w-3 h-3 text-brand-primary inline" aria-hidden="true" /> {roleLabel}
            </span>
          </div>
          <button
            type="button"
            onClick={handleLogout}
            aria-label={t('logout')}
            title={t('logout')}
            className="inline-flex items-center justify-center w-9 h-9 rounded-lg border border-neutral-200 dark:border-dark-border text-neutral-600 dark:text-dark-text-secondary hover:bg-neutral-100 dark:hover:bg-neutral-800 hover:text-brand-primary dark:hover:text-brand-primary-dark transition-colors focus:outline-none focus:ring-2 focus:ring-brand-primary"
          >
            <LogOut className="w-4 h-4" aria-hidden="true" />
          </button>
        </div>
      </div>
    </header>
  );
};
