# 10 — UI Design System

## 1. Design Identity

BerMap uses a **red, white and neutral** design identity, adapted for a professional infrastructure management platform. The design system supports **Light** and **Dark** themes with full semantic token coverage.

---

## 2. Color Tokens

### 2.1 Light Theme

| Token | Value | Usage |
|---|---|---|
| `--color-primary` | `#E30613` | Primary brand color, CTAs, active navigation |
| `--color-primary-hover` | `#C80510` | Primary buttons on hover |
| `--color-primary-active` | `#A5040D` | Primary buttons on press |
| `--color-white` | `#FFFFFF` | Page backgrounds, card backgrounds |
| `--color-neutral-dark` | `#17191F` | Primary text, headings |
| `--color-neutral-light` | `#F5F6F8` | Page background, subtle fills |
| `--color-border` | `#E4E6EB` | Card borders, dividers |
| `--color-text-primary` | `#17191F` | Body text |
| `--color-text-secondary` | `#6B7280` | Secondary text, captions |
| `--color-text-disabled` | `#9CA3AF` | Disabled state text |
| `--color-sidebar-bg` | `#FFFFFF` | Sidebar background |
| `--color-sidebar-active` | `#FEE2E2` | Active sidebar item background |
| `--color-card-bg` | `#FFFFFF` | Card / panel backgrounds |

### 2.2 Dark Theme

| Token | Value | Usage |
|---|---|---|
| `--color-background` | `#111318` | Page background |
| `--color-sidebar-bg` | `#181B22` | Sidebar background |
| `--color-card-bg` | `#20242D` | Card / panel backgrounds |
| `--color-primary` | `#FF3344` | Accent red (brighter for dark bg) |
| `--color-primary-hover` | `#FF4D5E` | Accent hover |
| `--color-primary-active` | `#E30613` | Accent press |
| `--color-text-primary` | `#F5F6F8` | Primary text |
| `--color-text-secondary` | `#AEB4C0` | Secondary text |
| `--color-text-disabled` | `#6B7280` | Disabled text |
| `--color-border` | `#343945` | Borders, dividers |
| `--color-sidebar-active` | `#2A1A1D` | Active sidebar item background |

### 2.3 Semantic / Status Colors

| Token | Light Value | Dark Value | Usage |
|---|---|---|---|
| `--color-healthy` | `#16A34A` | `#22C55E` | Healthy / online / success status |
| `--color-warning` | `#D97706` | `#F59E0B` | Warning / degraded status |
| `--color-critical` | `#DC2626` | `#EF4444` | Critical / error / offline status |
| `--color-unknown` | `#6B7280` | `#9CA3AF` | Unknown / no data status |
| `--color-info` | `#2563EB` | `#3B82F6` | Informational highlights |

---

## 3. Typography Scale

| Token | Size | Weight | Line Height | Usage |
|---|---|---|---|---|
| `--text-display` | 30px / 1.875rem | 700 | 1.2 | Page titles, dashboard headers |
| `--text-h1` | 24px / 1.5rem | 700 | 1.3 | Section headings |
| `--text-h2` | 20px / 1.25rem | 600 | 1.35 | Sub-section headings |
| `--text-h3` | 16px / 1rem | 600 | 1.4 | Card titles |
| `--text-body` | 14px / 0.875rem | 400 | 1.5 | Body text (default) |
| `--text-body-sm` | 13px / 0.8125rem | 400 | 1.5 | Dense tables, secondary info |
| `--text-caption` | 12px / 0.75rem | 400 | 1.4 | Labels, timestamps, captions |
| `--text-overline` | 11px / 0.6875rem | 600 | 1.5 | Overlines, section labels (uppercase) |
| `--text-mono` | 13px / 0.8125rem | 400 | 1.6 | Code, IDs, UUIDs, technical values |

**Font Stack**: `Inter, -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif`  
**Monospace Font**: `"JetBrains Mono", "Fira Code", "Cascadia Code", monospace`

> [!NOTE]
> All font files are bundled in the application for offline use. No external font CDN requests.

---

## 4. Spacing Scale

Base unit: **4px**

| Token | Value | Usage |
|---|---|---|
| `--space-0` | 0px | Reset |
| `--space-1` | 4px | Tight inline spacing |
| `--space-2` | 8px | Compact padding |
| `--space-3` | 12px | Form field gaps |
| `--space-4` | 16px | Standard padding |
| `--space-5` | 20px | Card padding |
| `--space-6` | 24px | Section spacing |
| `--space-8` | 32px | Large gaps |
| `--space-10` | 40px | Page margins |
| `--space-12` | 48px | Hero spacing |
| `--space-16` | 64px | Major section breaks |

---

## 5. Border Radius Scale

| Token | Value | Usage |
|---|---|---|
| `--radius-none` | 0px | Sharp corners |
| `--radius-sm` | 4px | Inputs, small buttons |
| `--radius-md` | 8px | Cards, panels |
| `--radius-lg` | 12px | Modals, large cards |
| `--radius-xl` | 16px | Feature cards |
| `--radius-full` | 9999px | Chips, avatars, status pills |

---

## 6. Shadow / Elevation Scale

| Token | Value | Usage |
|---|---|---|
| `--shadow-xs` | `0 1px 2px rgba(0,0,0,0.05)` | Subtle lift (inputs) |
| `--shadow-sm` | `0 1px 3px rgba(0,0,0,0.1), 0 1px 2px rgba(0,0,0,0.06)` | Cards |
| `--shadow-md` | `0 4px 6px rgba(0,0,0,0.1), 0 2px 4px rgba(0,0,0,0.06)` | Dropdowns |
| `--shadow-lg` | `0 10px 15px rgba(0,0,0,0.1), 0 4px 6px rgba(0,0,0,0.05)` | Modals |
| `--shadow-xl` | `0 20px 25px rgba(0,0,0,0.1), 0 10px 10px rgba(0,0,0,0.04)` | Floating panels |

> [!TIP]
> In dark theme, shadows use `rgba(0,0,0,0.3)` base for visibility against dark backgrounds.

---

## 7. Component State Tokens

| State | Background | Border | Text | Opacity |
|---|---|---|---|---|
| **Default** | `--color-card-bg` | `--color-border` | `--color-text-primary` | 1.0 |
| **Hover** | `+4% darken` / `+4% lighten` | `--color-primary` | `--color-text-primary` | 1.0 |
| **Active / Pressed** | `--color-primary` | `--color-primary-active` | `#FFFFFF` | 1.0 |
| **Disabled** | `--color-neutral-light` | `--color-border` | `--color-text-disabled` | 0.5 |
| **Focus** | Default | `--color-primary` + `2px ring` | `--color-text-primary` | 1.0 |
| **Error** | `--color-critical/10%` | `--color-critical` | `--color-critical` | 1.0 |

---

## 8. Layout Architecture

```
┌─────────────────────────────────────────────────────────────────────┐
│ Top Navigation Bar (56px)                          [Theme] [User]  │
├──────────┬──────────────────────────────────────────┬───────────────┤
│          │                                          │               │
│  Left    │           Main Content Area              │   Optional    │
│ Sidebar  │                                          │   Right       │
│  (260px) │           (flex-1)                       │   Panel       │
│          │                                          │   (360px)     │
│          │                                          │               │
│  Nav     │                                          │   Details /   │
│  Items   │                                          │   Properties  │
│          │                                          │               │
│          │                                          │               │
└──────────┴──────────────────────────────────────────┴───────────────┘
```

### Layout Specifications

| Element | Size | Position | Behavior |
|---|---|---|---|
| **Top Nav** | Height: 56px | Fixed top | Always visible; contains logo, breadcrumbs, theme toggle, user menu |
| **Left Sidebar** | Width: 260px (expanded), 64px (collapsed) | Fixed left | Collapsible; contains navigation items |
| **Main Content** | `flex: 1` | Center | Scrollable; hosts page content |
| **Right Panel** | Width: 360px | Conditional right | Opens for entity details, properties, simulation parameters |

---

## 9. Navigation Items

| # | Label | Icon | Route | Description |
|---|---|---|---|---|
| 1 | **Dashboard** | `LayoutDashboard` | `/` | Overview with key metrics and status summary |
| 2 | **Topology Map** | `Network` | `/topology` | Interactive graph visualization (React Flow) |
| 3 | **Infrastructure** | `Server` | `/infrastructure` | Entity inventory browser with filters |
| 4 | **Dependencies** | `GitFork` | `/dependencies` | Dependency graph explorer |
| 5 | **Impact Analysis** | `Zap` | `/impact` | Blast radius calculator |
| 6 | **Simulation Studio** | `FlaskConical` | `/simulations` | BerTwin simulation workspace |
| 7 | **Capacity** | `BarChart3` | `/capacity` | Capacity metrics and forecasting |
| 8 | **Integrations** | `Plug` | `/integrations` | Integration adapter management |
| 9 | **Collectors** | `Radio` | `/collectors` | Collector status and management |
| 10 | **Events** | `Bell` | `/events` | Event timeline and correlation |
| 11 | **Reports** | `FileText` | `/reports` | Report generation and export |
| 12 | **Audit Log** | `Shield` | `/audit` | Security audit trail |
| 13 | **Settings** | `Settings` | `/settings` | System configuration and user management |

---

## 10. Theme Toggle

| Aspect | Specification |
|---|---|
| Location | Top navigation bar, right side |
| Icon | Sun (light mode) / Moon (dark mode) |
| Transition | 200ms ease-in-out on all color properties |
| Persistence | Stored in `localStorage` (`bermap-theme: "light" | "dark"`) |
| Default | `light` (follows system preference on first visit via `prefers-color-scheme`) |
| Implementation | CSS custom properties toggled via `data-theme` attribute on `<html>` |

---

## 11. WCAG AA Compliance

| Requirement | Implementation |
|---|---|
| **Color Contrast** | All text meets 4.5:1 ratio (normal text) or 3:1 (large text) against background |
| **Focus Indicators** | Visible focus ring (`2px solid var(--color-primary)` with `2px offset`) on all interactive elements |
| **Keyboard Navigation** | Full keyboard navigation support; logical tab order |
| **ARIA Labels** | All interactive elements have appropriate `aria-label` or `aria-labelledby` |
| **Screen Reader** | Semantic HTML (`<nav>`, `<main>`, `<aside>`, headings) for screen reader navigation |
| **Motion** | Respect `prefers-reduced-motion`; disable animations when set |
| **Text Scaling** | UI remains usable at 200% text zoom |

---

## 12. Responsive Breakpoints

| Breakpoint | Width | Layout Behavior |
|---|---|---|
| `xs` | < 640px | Not officially supported (minimum screen: 1024px) |
| `sm` | 640–767px | Not officially supported |
| `md` | 768–1023px | Not officially supported |
| `lg` | 1024–1279px | Sidebar collapsed by default; right panel hidden |
| `xl` | 1280–1535px | Full layout; right panel opens on demand |
| `2xl` | ≥ 1536px | Full layout with expanded sidebar and right panel |

> [!NOTE]
> BerMap is a desktop-first enterprise application. The minimum supported viewport width is **1024px**.

---

## 13. Tailwind CSS Theme Configuration

```css
/* tailwind.css — @theme directive for BerMap design tokens */

@theme {
  /* Colors - Light theme defaults */
  --color-primary: #E30613;
  --color-primary-hover: #C80510;
  --color-primary-active: #A5040D;
  --color-accent: #E30613;

  --color-background: #F5F6F8;
  --color-surface: #FFFFFF;
  --color-sidebar: #FFFFFF;
  --color-card: #FFFFFF;
  --color-border: #E4E6EB;

  --color-text: #17191F;
  --color-text-secondary: #6B7280;
  --color-text-disabled: #9CA3AF;
  --color-text-inverse: #FFFFFF;

  /* Status colors */
  --color-healthy: #16A34A;
  --color-warning: #D97706;
  --color-critical: #DC2626;
  --color-unknown: #6B7280;
  --color-info: #2563EB;

  /* Typography */
  --font-sans: 'Inter', -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif;
  --font-mono: 'JetBrains Mono', 'Fira Code', 'Cascadia Code', monospace;

  /* Border radius */
  --radius-sm: 4px;
  --radius-md: 8px;
  --radius-lg: 12px;
  --radius-xl: 16px;
  --radius-full: 9999px;

  /* Shadows */
  --shadow-card: 0 1px 3px rgba(0, 0, 0, 0.1), 0 1px 2px rgba(0, 0, 0, 0.06);
  --shadow-dropdown: 0 4px 6px rgba(0, 0, 0, 0.1), 0 2px 4px rgba(0, 0, 0, 0.06);
  --shadow-modal: 0 10px 15px rgba(0, 0, 0, 0.1), 0 4px 6px rgba(0, 0, 0, 0.05);
}

/* Dark theme overrides */
[data-theme="dark"] {
  --color-primary: #FF3344;
  --color-primary-hover: #FF4D5E;
  --color-primary-active: #E30613;

  --color-background: #111318;
  --color-surface: #20242D;
  --color-sidebar: #181B22;
  --color-card: #20242D;
  --color-border: #343945;

  --color-text: #F5F6F8;
  --color-text-secondary: #AEB4C0;
  --color-text-disabled: #6B7280;

  --color-healthy: #22C55E;
  --color-warning: #F59E0B;
  --color-critical: #EF4444;
  --color-unknown: #9CA3AF;
  --color-info: #3B82F6;

  --shadow-card: 0 1px 3px rgba(0, 0, 0, 0.3), 0 1px 2px rgba(0, 0, 0, 0.2);
  --shadow-dropdown: 0 4px 6px rgba(0, 0, 0, 0.3), 0 2px 4px rgba(0, 0, 0, 0.2);
  --shadow-modal: 0 10px 15px rgba(0, 0, 0, 0.3), 0 4px 6px rgba(0, 0, 0, 0.2);
}
```
