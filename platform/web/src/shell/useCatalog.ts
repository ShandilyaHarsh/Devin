import { useEffect, useState } from 'react';
import { api, type CatalogEntry } from '@platform/sdk';

export function useCatalog(): { entries: CatalogEntry[] | null; error: unknown } {
  const [entries, setEntries] = useState<CatalogEntry[] | null>(null);
  const [error, setError] = useState<unknown>(null);

  useEffect(() => {
    let cancelled = false;
    api
      .get<CatalogEntry[]>('/api/platform/catalog')
      .then((list) => {
        if (!cancelled) setEntries(list);
      })
      .catch((err: unknown) => {
        if (!cancelled) setError(err);
      });
    return () => {
      cancelled = true;
    };
  }, []);

  return { entries, error };
}
