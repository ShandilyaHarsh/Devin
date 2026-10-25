import { useCallback, useEffect, useState } from 'react';
import { api } from '@platform/sdk';
import { describeError } from '../model';

interface LoadState<T> {
  key: string | null;
  data: T | null;
  error: string | null;
}

export function useLoad<T>(path: string | null): {
  data: T | null;
  error: string | null;
  loading: boolean;
  reload: () => void;
} {
  const [tick, setTick] = useState(0);
  const [state, setState] = useState<LoadState<T>>({ key: null, data: null, error: null });
  const key = path === null ? null : `${path}#${tick}`;

  useEffect(() => {
    if (path === null || key === null) return;
    let cancelled = false;
    api
      .get<T>(path)
      .then((d) => {
        if (!cancelled) setState({ key, data: d, error: null });
      })
      .catch((e: unknown) => {
        if (!cancelled) setState((s) => ({ key, data: s.data, error: describeError(e) }));
      });
    return () => {
      cancelled = true;
    };
  }, [path, key]);

  const reload = useCallback(() => setTick((t) => t + 1), []);
  return { data: state.data, error: state.error, loading: key !== null && state.key !== key, reload };
}
