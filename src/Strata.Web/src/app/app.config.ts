import { ApplicationConfig, provideZoneChangeDetection } from '@angular/core';
import { provideHttpClient, withFetch } from '@angular/common/http';
import { provideRouter, withInMemoryScrolling } from '@angular/router';

import { routes } from './app.routes';

export const appConfig: ApplicationConfig = {
  providers: [
    // Coalescing, because ECharts fires mousemove per pixel and a chart hover should not cost one
    // change-detection pass per event even after the directive runs it outside the zone.
    provideZoneChangeDetection({ eventCoalescing: true }),

    provideRouter(routes, withInMemoryScrolling({ scrollPositionRestoration: 'top' })),

    // fetch rather than XHR, so /tree can be read as a stream later without changing the client.
    provideHttpClient(withFetch()),
  ],
};
