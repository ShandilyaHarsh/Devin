import { ApiError } from '@platform/sdk';

export const SLUG = 'kyc-case-reviewer';
export const API = `/api/apps/${SLUG}`;
export const APP_ROOT = `/apps/${SLUG}`;

export interface CaseSummary {
  id: string;
  caseRef: string;
  customerName: string;
  riskScore: number;
  status: string;
  assignedAnalystId: string | null;
  preparedBy: string | null;
  canAssign: boolean;
}

export interface PermissionHint {
  allowed: boolean;
  reason: string;
}

export interface DocumentMeta {
  name: string;
  type: string;
  uploadedAt: string;
}

export interface NoteDto {
  id: string;
  authorId: string;
  text: string;
  createdAt: string;
}

export interface DecisionDto {
  id: string;
  deciderId: string;
  decision: string;
  rationale: string;
  createdAt: string;
}

export interface CaseDetail {
  id: string;
  caseRef: string;
  customerName: string;
  dateOfBirth: string;
  ssnLast4: string;
  riskScore: number;
  status: string;
  assignedAnalystId: string | null;
  preparedBy: string | null;
  recommendation: string | null;
  recommendationRationale: string | null;
  documents: DocumentMeta[];
  notes: NoteDto[];
  decisions: DecisionDto[];
  permissions: {
    reveal: PermissionHint;
    assign: PermissionHint;
    addNote: PermissionHint;
    recommend: PermissionHint;
    decide: PermissionHint;
  } | null;
}

export interface RevealedPii {
  customerName: string;
  dateOfBirth: string | null;
  ssnLast4: string;
}

const REASON_TEXT: Record<string, string> = {
  'maker-checker': 'Maker-checker: you prepared this recommendation, so a different senior reviewer must decide.',
  'no-matching-rule': 'Your role is not allowed to do this on this case (deny by default).',
};

export function explainReason(reason: string | null | undefined): string | null {
  if (!reason) return null;
  return REASON_TEXT[reason] ?? reason;
}

/** Human-readable error, surfacing the platform's policy reason (e.g. maker-checker) when present. */
export function describeError(err: unknown): string {
  if (err instanceof ApiError) {
    const problem = err.problem;
    const reason = explainReason(problem.reason);
    const title = problem.title ?? 'Request failed';
    return reason ? `${err.status} ${title} — ${reason}` : `${err.status} ${title}`;
  }
  return err instanceof Error ? err.message : 'Unexpected error';
}

export function statusColor(status: string): 'informative' | 'warning' | 'success' | 'danger' | 'brand' {
  switch (status) {
    case 'new':
      return 'informative';
    case 'in-review':
      return 'brand';
    case 'pending-senior-review':
      return 'warning';
    case 'approved':
      return 'success';
    default:
      return 'danger';
  }
}
