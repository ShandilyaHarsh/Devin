import type { AppModule } from '@platform/sdk';
import { CatalogPage } from './pages/CatalogPage';
import { RequestPage } from './pages/RequestPage';
import { QueuePage } from './pages/QueuePage';
import { ManagePage } from './pages/ManagePage';
import { CapabilitiesPage } from './pages/CapabilitiesPage';

const builderApp: AppModule = {
  slug: 'barebone-builder',
  displayName: 'Barebone Builder',
  routes: [
    { path: '', element: <CatalogPage />, navLabel: 'Catalog & manifests' },
    { path: 'request', element: <RequestPage />, navLabel: 'Request a tool' },
    { path: 'intakes', element: <QueuePage />, navLabel: 'Intake queue' },
    { path: 'intakes/:id', element: <QueuePage /> },
    { path: 'manage', element: <ManagePage />, navLabel: 'Manage app' },
    { path: 'capabilities', element: <CapabilitiesPage />, navLabel: 'Platform capabilities' },
  ],
};

export default builderApp;
