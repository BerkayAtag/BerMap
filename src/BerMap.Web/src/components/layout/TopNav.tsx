import React, { useEffect, useId, useRef, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import { ChevronDown, KeyRound, Loader2, LogOut, Menu, Search, Server, Shield, X } from 'lucide-react';
import { ThemeToggle } from './ThemeToggle';
import { LocaleToggle } from './LocaleToggle';
import { SIDEBAR_ID } from './Sidebar';
import { StatusIndicator } from '@/components/ui/StatusIndicator';
import { ChangePasswordDialog } from '@/components/users/ChangePasswordDialog';
import { useT, type TranslationKey } from '@/lib/i18n-runtime';
import { useDashboard } from '@/hooks/useDashboard';
import { useIsMobile } from '@/hooks/useMediaQuery';
import { useAuthStore } from '@/stores/authStore';
import { useSidebarStore } from '@/stores/sidebarStore';
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

const ICON_BUTTON =
  'inline-flex items-center justify-center w-9 h-9 shrink-0 rounded-lg border border-neutral-200 dark:border-dark-border text-neutral-600 dark:text-dark-text-secondary hover:bg-neutral-100 dark:hover:bg-neutral-800 hover:text-brand-primary dark:hover:text-brand-primary-dark transition-colors focus:outline-none focus:ring-2 focus:ring-brand-primary';

interface GlobalSearchProps {
  /** Small screens: the field is collapsed to an icon until expanded. */
  compact: boolean;
  expanded: boolean;
  onExpandedChange: (expanded: boolean) => void;
}

const GlobalSearch: React.FC<GlobalSearchProps> = ({ compact, expanded, onExpandedChange }) => {
  const t = useT();
  const navigate = useNavigate();
  const [input, setInput] = useState('');
  const [term, setTerm] = useState('');
  const [open, setOpen] = useState(false);
  const [activeIndex, setActiveIndex] = useState(-1);
  const inputRef = useRef<HTMLInputElement>(null);
  const containerRef = useRef<HTMLDivElement>(null);
  const listId = useId();
  const showField = !compact || expanded;

  useEffect(() => {
    const timer = window.setTimeout(() => setTerm(input.trim()), 250);
    return () => window.clearTimeout(timer);
  }, [input]);

  // ⌘K / Ctrl+K focuses the search box, as advertised in the placeholder.
  useEffect(() => {
    const onKeyDown = (event: KeyboardEvent) => {
      if ((event.metaKey || event.ctrlKey) && event.key.toLowerCase() === 'k') {
        event.preventDefault();
        if (compact) onExpandedChange(true);
        inputRef.current?.focus();
        setOpen(true);
      }
    };
    window.addEventListener('keydown', onKeyDown);
    return () => window.removeEventListener('keydown', onKeyDown);
  }, [compact, onExpandedChange]);

  // The expanding field takes focus as soon as it appears.
  useEffect(() => {
    if (compact && expanded) {
      inputRef.current?.focus();
      setOpen(true);
    }
  }, [compact, expanded]);

  useEffect(() => {
    if (!open && !(compact && expanded)) return;
    const onPointerDown = (event: PointerEvent) => {
      if (containerRef.current?.contains(event.target as Node)) return;
      setOpen(false);
      if (compact && expanded && input.trim() === '') onExpandedChange(false);
    };
    document.addEventListener('pointerdown', onPointerDown);
    return () => document.removeEventListener('pointerdown', onPointerDown);
  }, [open, compact, expanded, input, onExpandedChange]);

  const searchQuery = useQuery<InfrastructureNode[]>({
    queryKey: ['graph', 'search', term],
    queryFn: () => api.get<InfrastructureNode[]>(`/graph/search?q=${encodeURIComponent(term)}`),
    enabled: term.length >= 2,
    staleTime: 15_000,
  });

  const typed = input.trim();
  // Results are only selectable while they belong to what is in the box; during
  // the debounce window an older term's results must not be picked by Enter.
  const inSync = typed === term && !searchQuery.isFetching;
  const results = term.length >= 2 && inSync ? (searchQuery.data ?? []).slice(0, 10) : [];
  const showDropdown = open && typed.length >= 2;

  const finish = () => {
    setOpen(false);
    setInput('');
    setTerm('');
    setActiveIndex(-1);
    inputRef.current?.blur();
    onExpandedChange(false);
  };

  const goToNode = (node: InfrastructureNode) => {
    finish();
    navigate(`/inventory?node=${encodeURIComponent(node.id)}`);
  };

  const goToInventorySearch = () => {
    if (!typed) return;
    finish();
    navigate(`/inventory?q=${encodeURIComponent(typed)}`);
  };

  const onKeyDown = (event: React.KeyboardEvent<HTMLInputElement>) => {
    if (event.key === 'ArrowDown') {
      event.preventDefault();
      setOpen(true);
      if (results.length > 0) setActiveIndex((index) => Math.min(results.length - 1, index + 1));
    } else if (event.key === 'ArrowUp') {
      event.preventDefault();
      setActiveIndex((index) => Math.max(-1, index - 1));
    } else if (event.key === 'Enter') {
      event.preventDefault();
      if (activeIndex >= 0 && results[activeIndex]) goToNode(results[activeIndex]);
      else goToInventorySearch();
    } else if (event.key === 'Escape') {
      if (showDropdown) {
        // Consumed here: page-level Escape handlers (open panels) must ignore it.
        event.preventDefault();
        setOpen(false);
        setActiveIndex(-1);
      } else {
        inputRef.current?.blur();
        if (compact) onExpandedChange(false);
      }
    }
  };

  if (!showField) {
    return (
      <button
        type="button"
        onClick={() => onExpandedChange(true)}
        aria-label={t('openSearch')}
        title={t('openSearch')}
        className={ICON_BUTTON}
      >
        <Search className="w-4 h-4" aria-hidden="true" />
      </button>
    );
  }

  const activeInSync = activeIndex >= 0 && activeIndex < results.length;

  return (
    <div
      ref={containerRef}
      className={cn(
        'flex items-center relative min-w-0',
        compact
          ? 'absolute inset-x-0 top-0 h-full z-50 gap-2 px-3 bg-white dark:bg-dark-sidebar'
          : 'flex-1 max-w-96',
      )}
    >
      <div className="relative flex items-center flex-1 min-w-0">
        <Search className="w-4 h-4 absolute left-3 text-neutral-400 dark:text-neutral-500 pointer-events-none" aria-hidden="true" />
        <input
          ref={inputRef}
          type="search"
          role="combobox"
          aria-label={t('searchLabel')}
          aria-expanded={showDropdown}
          aria-controls={listId}
          aria-autocomplete="list"
          aria-activedescendant={activeInSync ? `${listId}-${activeIndex}` : undefined}
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
            {searchQuery.isError && inSync ? (
              <div className="px-3 py-3 text-xs text-rose-600 dark:text-rose-400">{t('searchFailed')}</div>
            ) : !inSync || (searchQuery.isFetching && results.length === 0) ? (
              <div className="flex items-center gap-2 px-3 py-3 text-xs text-neutral-500">
                <Loader2 className="w-3.5 h-3.5 animate-spin" />
                {t('searching')}
              </div>
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
                    <span lang="en" className="text-[10px] text-neutral-500 shrink-0">{node.nodeType}</span>
                  </li>
                ))}
              </ul>
            )}
          </div>
        )}
      </div>

      {compact && (
        <button
          type="button"
          onClick={finish}
          aria-label={t('closeSearch')}
          title={t('closeSearch')}
          className={ICON_BUTTON}
        >
          <X className="w-4 h-4" aria-hidden="true" />
        </button>
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
        'hidden lg:flex items-center gap-2 px-3 py-1.5 rounded-full border text-xs font-medium whitespace-nowrap',
        badge.className,
      )}
      role="status"
    >
      <StatusIndicator status={status} size="sm" label={label} />
      <span>{label}</span>
    </div>
  );
};

const UserMenu: React.FC<{ onChangePassword: () => void }> = ({ onChangePassword }) => {
  const t = useT();
  const navigate = useNavigate();
  const user = useAuthStore((state) => state.user);
  const logout = useAuthStore((state) => state.logout);
  const [open, setOpen] = useState(false);
  const containerRef = useRef<HTMLDivElement>(null);
  const buttonRef = useRef<HTMLButtonElement>(null);
  const firstItemRef = useRef<HTMLButtonElement>(null);
  const menuId = useId();

  const displayName = user?.displayName || user?.username || '—';
  const roleKey = user?.role ? ROLE_KEYS[user.role] : undefined;
  const roleLabel = roleKey ? t(roleKey) : user?.role || t('roleUnknown');

  useEffect(() => {
    if (!open) return;
    firstItemRef.current?.focus();
    const onPointerDown = (event: PointerEvent) => {
      if (!containerRef.current?.contains(event.target as Node)) setOpen(false);
    };
    document.addEventListener('pointerdown', onPointerDown);
    return () => document.removeEventListener('pointerdown', onPointerDown);
  }, [open]);

  const close = () => {
    setOpen(false);
    buttonRef.current?.focus();
  };

  const onMenuKeyDown = (event: React.KeyboardEvent<HTMLDivElement>) => {
    const items = Array.from(
      containerRef.current?.querySelectorAll<HTMLButtonElement>('[role="menuitem"]') ?? [],
    );
    const index = items.indexOf(document.activeElement as HTMLButtonElement);
    if (event.key === 'Escape') {
      event.preventDefault();
      close();
    } else if (event.key === 'ArrowDown') {
      event.preventDefault();
      items[(index + 1) % items.length]?.focus();
    } else if (event.key === 'ArrowUp') {
      event.preventDefault();
      items[(index - 1 + items.length) % items.length]?.focus();
    } else if (event.key === 'Tab') {
      setOpen(false);
    }
  };

  const handleLogout = () => {
    setOpen(false);
    logout();
    navigate('/login', { replace: true });
  };

  return (
    <div className="relative shrink-0" ref={containerRef}>
      <button
        ref={buttonRef}
        type="button"
        onClick={() => setOpen((value) => !value)}
        onKeyDown={(event) => {
          if (event.key === 'ArrowDown') {
            event.preventDefault();
            setOpen(true);
          }
        }}
        aria-haspopup="menu"
        aria-expanded={open}
        aria-controls={menuId}
        aria-label={`${t('userMenu')}: ${displayName}`}
        className="flex items-center gap-2 rounded-lg p-0.5 lg:pr-2 hover:bg-neutral-100 dark:hover:bg-neutral-800 focus:outline-none focus:ring-2 focus:ring-brand-primary transition-colors"
      >
        <span
          className="w-8 h-8 rounded-full bg-brand-primary/10 dark:bg-brand-primary-dark/20 text-brand-primary dark:text-brand-primary-dark border border-brand-primary/20 flex items-center justify-center font-bold text-xs"
          aria-hidden="true"
        >
          {getInitials(displayName)}
        </span>
        <span className="hidden lg:flex flex-col text-left max-w-40">
          <span className="text-xs font-semibold text-neutral-900 dark:text-white leading-tight truncate">
            {displayName}
          </span>
          <span className="text-[10px] text-neutral-500 dark:text-neutral-400 flex items-center gap-1 truncate">
            <Shield className="w-3 h-3 text-brand-primary inline shrink-0" aria-hidden="true" /> {roleLabel}
          </span>
        </span>
        <ChevronDown className="hidden lg:block w-3.5 h-3.5 text-neutral-400" aria-hidden="true" />
      </button>

      {open && (
        <div
          id={menuId}
          role="menu"
          aria-label={t('userMenu')}
          onKeyDown={onMenuKeyDown}
          className="absolute right-0 top-full mt-1 z-50 w-60 rounded-lg border shadow-xl bg-white dark:bg-dark-card border-neutral-200 dark:border-dark-border"
        >
          <div className="px-3 py-2.5 border-b border-neutral-200 dark:border-dark-border">
            <div className="text-sm font-semibold text-neutral-900 dark:text-white truncate">{displayName}</div>
            {user?.username && user.username !== displayName && (
              <div className="text-[11px] font-mono text-neutral-500 truncate">{user.username}</div>
            )}
            <div className="text-[11px] text-neutral-500 flex items-center gap-1 mt-0.5">
              <Shield className="w-3 h-3 text-brand-primary" aria-hidden="true" /> {roleLabel}
            </div>
          </div>
          <div className="p-1">
            <button
              ref={firstItemRef}
              type="button"
              role="menuitem"
              onClick={() => {
                // Focus the menu button first: the dialog returns focus to it on close.
                close();
                onChangePassword();
              }}
              className="w-full flex items-center gap-2 px-3 py-2 rounded-md text-sm text-neutral-700 dark:text-neutral-200 hover:bg-neutral-100 dark:hover:bg-neutral-800 focus:outline-none focus-visible:ring-2 focus-visible:ring-brand-primary"
            >
              <KeyRound className="w-4 h-4 opacity-70" aria-hidden="true" />
              {t('changePassword')}
            </button>
            <button
              type="button"
              role="menuitem"
              onClick={handleLogout}
              className="w-full flex items-center gap-2 px-3 py-2 rounded-md text-sm text-rose-600 dark:text-rose-400 hover:bg-rose-50 dark:hover:bg-rose-950/30 focus:outline-none focus-visible:ring-2 focus-visible:ring-brand-primary"
            >
              <LogOut className="w-4 h-4" aria-hidden="true" />
              {t('logout')}
            </button>
          </div>
        </div>
      )}
    </div>
  );
};

export const TopNav: React.FC = () => {
  const t = useT();
  const isMobile = useIsMobile();
  const isMobileOpen = useSidebarStore((state) => state.isMobileOpen);
  const toggleMobile = useSidebarStore((state) => state.toggleMobile);
  const [searchExpanded, setSearchExpanded] = useState(false);
  const [passwordOpen, setPasswordOpen] = useState(false);

  return (
    <>
      <header className="relative h-16 px-3 md:px-6 flex items-center justify-between gap-2 md:gap-4 border-b border-neutral-200 dark:border-dark-border bg-white dark:bg-dark-sidebar z-30 transition-colors">
        <div className="flex items-center gap-2 min-w-0 flex-1">
          {isMobile && (
            <button
              type="button"
              onClick={toggleMobile}
              aria-label={isMobileOpen ? t('closeNavigation') : t('openNavigation')}
              aria-expanded={isMobileOpen}
              aria-controls={SIDEBAR_ID}
              className={ICON_BUTTON}
            >
              <Menu className="w-5 h-5" aria-hidden="true" />
            </button>
          )}
          {isMobile && (
            <span className="font-bold text-base tracking-tight text-neutral-900 dark:text-white truncate">
              Ber<span className="text-brand-primary dark:text-brand-primary-dark">Map</span>
            </span>
          )}
          {!isMobile && (
            <GlobalSearch compact={false} expanded={false} onExpandedChange={setSearchExpanded} />
          )}
        </div>

        {/* Right Controls */}
        <div className="flex items-center gap-2 md:gap-3 shrink-0">
          {isMobile && (
            <GlobalSearch compact expanded={searchExpanded} onExpandedChange={setSearchExpanded} />
          )}
          <SystemHealthBadge />

          <LocaleToggle />
          <ThemeToggle />

          <div className="hidden sm:block h-6 w-[1px] bg-neutral-200 dark:bg-dark-border" />

          <UserMenu onChangePassword={() => setPasswordOpen(true)} />
        </div>
      </header>

      {/* Outside the header: its z-30 stacking context would put the dialog under the sidebar. */}
      <ChangePasswordDialog open={passwordOpen} onClose={() => setPasswordOpen(false)} />
    </>
  );
};
