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
    path: 'repos/add',
    loadComponent: () => import('./features/wizards/add/add-repo-wizard.component').then(m => m.AddRepoWizardComponent),
  },
  {
    path: 'repos/create',
    loadComponent: () => import('./features/wizards/create/create-repo-wizard.component').then(m => m.CreateRepoWizardComponent),
  },
  {
    path: 'repos/:repoId',
    loadComponent: () => import('./features/repo/repo-detail.component').then(m => m.RepoDetailComponent),
    children: [
      { path: '', pathMatch: 'full', redirectTo: 'files' },
      { path: 'files', loadComponent: () => import('./features/repo/files/files-tab.component').then(m => m.FilesTabComponent) },
      { path: 'statistics', loadComponent: () => import('./features/repo/statistics/statistics-tab.component').then(m => m.StatisticsTabComponent) },
    ],
  },
  {
    path: 'jobs/:id',
    loadComponent: () => import('./features/jobs/job-detail.component').then(m => m.JobDetailComponent),
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
