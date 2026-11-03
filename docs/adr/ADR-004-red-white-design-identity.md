# ADR-004: Red & White Design Identity & Theme Architecture

## Status
Accepted

## Context
Enterprise operations software frequently suffers from disjointed, visually unappealing, or generic UI templates that reduce operator adoption, increase cognitive fatigue, and fail to project an enterprise-grade standard.

BerMap's design identity must convey precision, reliability, and executive polish, built on a distinct, high-contrast red and white palette. Furthermore, modern 24/7 Network Operations Centers (NOC) and Infrastructure Operations rooms demand high-fidelity Dark Mode support to minimize eye strain.

## Decision
1. **Red & White Color Identity**:
   - Primary Brand Red: `#E30613` (used for primary actions, active navigation indicators, and key metric accents).
   - Primary White: `#FFFFFF` (dominant background in light mode cards and headers).
   - Neutral Dark: `#17191F` (high-contrast typography and accents).
   - Neutral Light: `#F5F6F8` (subtle page canvas background).
   - Dedicated Dark Theme Surface: `#111318` background, `#181B22` sidebar, `#20242D` cards, `#FF3344` accent.
2. **First-Class Dark Theme**:
   - Dark theme is not a simple color invert; it is an intentionally engineered dark surface palette preserving contrast, visual hierarchy, and brand red accentuation.
3. **Design Token Management**:
   - Centralized CSS variable tokens implemented via Tailwind CSS v4 `@theme` directive and `data-theme="dark"` attribute selectors.
4. **Theme Switch Mechanism**:
   - Prominent, smooth Sun/Moon theme toggle button located on the top navigation bar.
   - Zero-flash on initial page load, persisting user preference locally and in user profile.
5. **Brand Independence & Integrity**:
   - The palette is BerMap's own. BerMap is not affiliated with any organisation and uses no third-party trademarks, logos or icons.

## Consequences
### Positive
- Striking, memorable, executive-grade enterprise aesthetic that stands apart from generic dashboards.
- Optimal usability in both bright office environments (Light Mode) and dimly lit NOC/operations rooms (Dark Mode).
- High accessibility meeting WCAG 2.1 AA standards for contrast and visual readability.

### Negative / Tradeoffs
- Every new component, chart, and modal must be meticulously verified in both light and dark themes.
