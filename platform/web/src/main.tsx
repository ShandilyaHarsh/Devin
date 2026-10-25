import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { BrowserRouter } from 'react-router';
import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { SessionProvider } from '@platform/sdk';
import { AppRoutes } from './shell/Routing';

const container = document.getElementById('root');
if (!container) {
  throw new Error('Missing #root element');
}

createRoot(container).render(
  <StrictMode>
    <FluentProvider theme={webLightTheme}>
      <SessionProvider>
        <BrowserRouter>
          <AppRoutes />
        </BrowserRouter>
      </SessionProvider>
    </FluentProvider>
  </StrictMode>,
);
