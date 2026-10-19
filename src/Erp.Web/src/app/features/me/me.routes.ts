import { Routes } from '@angular/router';
import { MeShell } from './me-shell';

export const ME_ROUTES: Routes = [
  {
    path: '',
    component: MeShell,
    children: [
      { path: '', loadComponent: () => import('./me-pages').then((m) => m.MeHomePage) },
      { path: 'payslips', loadComponent: () => import('./me-pages').then((m) => m.MePayslipsPage) },
      { path: 'payslips/:id', loadComponent: () => import('./me-pages').then((m) => m.MePayslipPage) },
      { path: 'leave', loadComponent: () => import('./me-leave').then((m) => m.MeLeavePage) },
      { path: 'documents', loadComponent: () => import('./me-pages').then((m) => m.MeDocumentsPage) },
      { path: 'punch', loadComponent: () => import('./me-field').then((m) => m.MePunchPage) },
      { path: 'expenses', loadComponent: () => import('./me-field').then((m) => m.MeExpensesPage) },
      { path: 'roster', loadComponent: () => import('./me-pages').then((m) => m.MeRosterPage) },
      { path: 'more', loadComponent: () => import('./me-pages').then((m) => m.MeMorePage) }
    ]
  }
];
