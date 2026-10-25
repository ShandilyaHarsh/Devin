import { ApiError } from '@platform/sdk';
import type { AccessMode, CatalogEntry, DataClassification, Stage } from '@platform/sdk';

export const BASE = '/api/apps/barebone-builder';
export const STAGES: Stage[] = ['builder-only', 'demo', 'live'];
export const CLASSIFICATIONS: DataClassification[] = ['public', 'internal', 'confidential', 'restricted'];
export const ACCESS_MODES: AccessMode[] = ['read-only', 'write'];

export interface Roi {
  annualHoursSaved: number;
  annualValueUsd: number;
  buildHours: number;
  buildCostUsd: number;
  annualRunCostUsd: number;
  paybackMonths: number | null;
  recommendation: 'build' | 'review';
}

export interface IntakeCreate {
  title: string;
  appSlug: string;
  problem: string;
  users: string;
  dataTouched: string;
  dataClassification: DataClassification;
  access: AccessMode;
  hoursSavedPerUserPerWeek: number;
  numberOfUsers: number;
}

export interface Intake extends IntakeCreate {
  id: string;
  status: 'submitted' | 'approved' | 'rejected';
  requesterId: string;
  decidedBy: string | null;
  decidedAt: string | null;
  rejectReason: string | null;
  roi: Roi;
  generatedManifestYaml: string | null;
  createdAt: string;
  seededSyntheticData?: boolean | null;
}

export interface DirectoryUser {
  id: string;
  displayName: string;
  roles: string[];
}

export interface Capability {
  id: string;
  name: string;
  description: string;
  howToSee: string;
}

export type { CatalogEntry };

export const usd = (n: number): string =>
  n.toLocaleString('en-US', { style: 'currency', currency: 'USD', maximumFractionDigits: 0 });

export function describeError(err: unknown): string {
  if (err instanceof ApiError) {
    const p = err.problem;
    const reason = p?.reason ? ` (reason: ${p.reason})` : '';
    const detail = p?.detail ? ` — ${p.detail}` : '';
    return `${err.status} ${p?.title ?? 'Request failed'}${reason}${detail}`;
  }
  return err instanceof Error ? err.message : 'Unexpected error';
}

export function validationErrors(err: unknown): Record<string, string[]> {
  if (err instanceof ApiError && err.status === 400) {
    const errors = (err.problem as { errors?: Record<string, string[]> } | undefined)?.errors;
    return errors ?? {};
  }
  return {};
}
