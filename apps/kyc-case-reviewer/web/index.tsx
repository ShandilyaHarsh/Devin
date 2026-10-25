import type { AppModule } from '@platform/sdk';
import { AuditTrail } from './AuditTrail';
import { CaseDetailPage } from './CaseDetail';
import { CaseQueue } from './CaseQueue';
import { SLUG } from './model';

const kycApp: AppModule = {
  slug: SLUG,
  displayName: 'KYC Case Reviewer',
  routes: [
    { path: '', element: <CaseQueue />, navLabel: 'Case queue' },
    { path: 'cases/:id', element: <CaseDetailPage /> },
    { path: 'audit', element: <AuditTrail />, navLabel: 'Audit trail' },
  ],
};

export default kycApp;
