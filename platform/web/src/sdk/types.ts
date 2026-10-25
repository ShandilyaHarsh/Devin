import type { ReactNode } from 'react';

export type Stage = 'builder-only' | 'demo' | 'live';
export type DataClassification = 'public' | 'internal' | 'confidential' | 'restricted';
export type AccessMode = 'read-only' | 'write';

export interface SessionUser {
  id: string;
  displayName: string;
  email: string;
  roles: string[];
}

export interface SessionInfo {
  authenticated: boolean;
  user: SessionUser | null;
  csrfToken: string;
  expiresAt: string | null;
}

export interface CatalogEntry {
  slug: string;
  displayName: string;
  description: string;
  owner: { team: string; email: string };
  businessOwner: { name: string; email: string };
  dataClassification: DataClassification;
  access: AccessMode;
  stage: Stage;
  accessList: string[];
  requesterId: string | null;
  approvedBy: string | null;
  registeredAt: string;
  hasCode: boolean;
  manifestYaml: string;
}

export interface AuditEvent {
  id: number;
  timestamp: string;
  appSlug: string;
  actorId: string;
  action: string;
  resourceType: string;
  resourceId: string | null;
  outcome: 'success' | 'denied' | 'read';
  reason: string | null;
  before: string | null;
  after: string | null;
  correlationId: string;
  prevHash: string;
  hash: string;
}

export interface AuditVerifyResult {
  valid: boolean;
  checked: number;
  firstBrokenId: number | null;
  reason: string | null;
}

export interface ProblemDetails {
  type?: string;
  title?: string;
  status?: number;
  detail?: string;
  correlationId?: string;
  reason?: string;
}

export interface AppRoute {
  /** Relative to /apps/{slug}/ ; '' is the app home. */
  path: string;
  element: ReactNode;
  /** If set, shown in the app's side navigation. */
  navLabel?: string;
}

/** Default export of apps/<slug>/web/index.tsx */
export interface AppModule {
  slug: string;
  displayName: string;
  routes: AppRoute[];
}
