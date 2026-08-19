import { Routes } from '@angular/router';

/**
 * Five screens, lazily loaded.
 *
 * Lazy because Explorer and Timeline are the only two that pull in ECharts, and someone who opens
 * the tray icon to check Settings should not download a charting library to do it.
 */
export const routes: Routes = [
  { path: '', pathMatch: 'full', redirectTo: 'overview' },

  {
    path: 'overview',
    title: 'Strata — Overview',
    loadComponent: () => import('./pages/overview/overview').then((m) => m.Overview),
  },
  {
    path: 'timeline',
    title: 'Strata — Timeline',
    loadComponent: () => import('./pages/timeline/timeline').then((m) => m.Timeline),
  },
  {
    path: 'explorer',
    title: 'Strata — Explorer',
    loadComponent: () => import('./pages/explorer/explorer').then((m) => m.Explorer),
  },
  {
    path: 'compare',
    title: 'Strata — Compare',
    loadComponent: () => import('./pages/compare/compare').then((m) => m.Compare),
  },
  {
    path: 'settings',
    title: 'Strata — Settings',
    loadComponent: () => import('./pages/settings/settings').then((m) => m.SettingsPage),
  },

  { path: '**', redirectTo: 'overview' },
];
