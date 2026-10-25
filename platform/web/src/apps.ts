import type { AppModule } from '@platform/sdk';

// App discovery (plan 5.1): every apps/<slug>/web/index.tsx default-exports an AppModule. Zero apps is valid.
const modules = import.meta.glob<{ default: AppModule }>('../../../apps/*/web/index.tsx', { eager: true });

export const appModules: AppModule[] = Object.values(modules)
  .map((m) => m.default)
  .filter((m): m is AppModule => Boolean(m && typeof m.slug === 'string' && Array.isArray(m.routes)))
  .sort((a, b) => a.displayName.localeCompare(b.displayName));

export function findAppModule(slug: string): AppModule | undefined {
  return appModules.find((m) => m.slug === slug);
}
