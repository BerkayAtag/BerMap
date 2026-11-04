import { create } from 'zustand';

const COLLAPSED_STORAGE_KEY = 'bermap-sidebar-collapsed';

interface SidebarState {
  /** Desktop (md and up) icon-only mode; persisted per browser. */
  isCollapsed: boolean;
  /** Small-screen off-canvas drawer; never persisted. */
  isMobileOpen: boolean;
  toggle: () => void;
  setCollapsed: (collapsed: boolean) => void;
  openMobile: () => void;
  closeMobile: () => void;
  toggleMobile: () => void;
}

function readCollapsed(): boolean {
  try {
    return localStorage.getItem(COLLAPSED_STORAGE_KEY) === 'true';
  } catch {
    return false;
  }
}

function writeCollapsed(collapsed: boolean): void {
  try {
    localStorage.setItem(COLLAPSED_STORAGE_KEY, String(collapsed));
  } catch {
    // Storage unavailable (private mode); the preference simply will not persist.
  }
}

export const useSidebarStore = create<SidebarState>((set) => ({
  isCollapsed: readCollapsed(),
  isMobileOpen: false,
  toggle: () =>
    set((state) => {
      const next = !state.isCollapsed;
      writeCollapsed(next);
      return { isCollapsed: next };
    }),
  setCollapsed: (collapsed) => {
    writeCollapsed(collapsed);
    set({ isCollapsed: collapsed });
  },
  openMobile: () => set({ isMobileOpen: true }),
  closeMobile: () => set({ isMobileOpen: false }),
  toggleMobile: () => set((state) => ({ isMobileOpen: !state.isMobileOpen })),
}));
