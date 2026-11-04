# Contributing to BerMap

Thank you for contributing to the **BerMap** platform.

## Development Standards & Principles

1. **Air-Gapped First**: Every feature must work 100% offline. Never introduce external HTTP/HTTPS dependencies for scripts, fonts, analytics, telemetry, or external verification.
2. **Clean Architecture & Module Boundaries**:
   - `BerMap.Domain` must remain free of framework dependencies.
   - Modules must not reach directly into another module's database tables. All cross-module communication flows through `BerMap.Application` or `BerMap.Contracts`.
3. **Read-Only Safety**: Integration adapters and discovery modules must strictly adhere to read-only semantics. Write-back or mutating operations against client infrastructure are strictly forbidden.
4. **Deterministic Simulation**: All algorithms inside `BerMap.Simulation` (BerSim) must be deterministic and testable.
5. **Design System Adherence**: All frontend views must use design tokens defined in `src/index.css` and verify compatibility with both Light and Dark themes.
6. **Testing**: All business logic must be accompanied by comprehensive unit tests.

## Coding Style
- C#: Follow Microsoft C# Conventions and nullable reference types (`<Nullable>enable</Nullable>`).
- TypeScript: Strict type checking (`strict: true`), avoid `any`.
- Asynchronous programming: All I/O operations must be `async/await` and propagate `CancellationToken`.
