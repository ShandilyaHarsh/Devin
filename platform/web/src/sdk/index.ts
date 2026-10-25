// Public frontend SDK for apps. Implementations live in this folder (owned by platform-web).
// Contract (see docs/prototype-plan.md section 5):
//   api.get<T>(path), api.post<T>(path, body?, { idempotencyKey? }), api.put<T>(...), api.del<T>(...)
//     -> adds X-CSRF-Token, adds Idempotency-Key (fresh per call unless provided), credentials 'same-origin',
//        throws ApiError { status, problem } on non-2xx. Response header Idempotency-Replayed is exposed via
//        api.postWithMeta<T>() -> { data: T, replayed: boolean }.
//   useSession() -> { session: SessionInfo | null, loading, refresh(), login(userId), logout() }
//   newIdempotencyKey() -> string
//   Components: Page, StageBadge, AccessDenied, MaskedValue, AuditViewer, VerifyAuditButton
export type * from './types';
