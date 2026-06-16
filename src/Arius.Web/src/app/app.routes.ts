import { Routes } from '@angular/router';

export const routes: Routes = [
  { path: '', pathMatch: 'full', redirectTo: 'overview' },
  {
    path: 'overview',
    loadComponent: () => import('./features/overview/overview.component').then(m => m.OverviewComponent),
  },
  {
    path: 'repos',
    pathMatch: 'full',
    loadComponent: () => import('./features/repos/repos.component').then(m => m.ReposComponent),
  },
  {
    path: 'repos/:repoId',
    loadComponent: () => import('./features/repo/repo-detail.component').then(m => m.RepoDetailComponent),
    children: [
      { path: '', pathMatch: 'full', redirectTo: 'files' },
      { path: 'files', loadComponent: () => import('./features/repo/files/files-tab.component').then(m => m.FilesTabComponent) },
      { path: 'statistics', loadComponent: () => import('./features/repo/statistics/statistics-tab.component').then(m => m.StatisticsTabComponent) },
      { path: 'properties', loadComponent: () => import('./features/repo/properties/properties-tab.component').then(m => m.PropertiesTabComponent) },
    ],
  },
  {
    path: 'jobs',
    loadComponent: () => import('./features/jobs/jobs.component').then(m => m.JobsComponent),
  },
  {
    path: 'settings',
    loadComponent: () => import('./features/settings/settings.component').then(m => m.SettingsComponent),
  },
  { path: '**', redirectTo: 'overview' },
];
