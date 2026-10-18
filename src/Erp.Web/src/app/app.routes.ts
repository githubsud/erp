import { Routes } from '@angular/router';
import { authGuard, permissionGuard } from './core/auth.service';
import { Shell } from './layout/shell';

export const routes: Routes = [
  { path: 'login', loadComponent: () => import('./features/login/login').then((m) => m.LoginPage) },
  {
    path: '',
    component: Shell,
    canActivate: [authGuard],
    children: [
      { path: '', pathMatch: 'full', redirectTo: 'dashboard' },
      { path: 'dashboard', loadComponent: () => import('./features/dashboard/dashboard').then((m) => m.DashboardPage) },
      {
        path: 'employees',
        canActivate: [permissionGuard('hr.employees:read')],
        loadComponent: () => import('./features/employees/employee-list').then((m) => m.EmployeeListPage)
      },
      {
        path: 'employees/new',
        canActivate: [permissionGuard('hr.employees:manage')],
        loadComponent: () => import('./features/employees/employee-form').then((m) => m.EmployeeFormPage)
      },
      {
        path: 'employees/import',
        canActivate: [permissionGuard('hr.employees:manage')],
        loadComponent: () => import('./features/employees/employee-import').then((m) => m.EmployeeImportPage)
      },
      {
        path: 'employees/:id/edit',
        canActivate: [permissionGuard('hr.employees:manage')],
        loadComponent: () => import('./features/employees/employee-form').then((m) => m.EmployeeFormPage)
      },
      {
        path: 'employees/:id',
        canActivate: [permissionGuard('hr.employees:read')],
        loadComponent: () => import('./features/employees/employee-detail').then((m) => m.EmployeeDetailPage)
      },
      {
        path: 'payroll',
        canActivate: [permissionGuard('hr.payroll:read')],
        loadComponent: () => import('./features/payroll/payroll-list').then((m) => m.PayrollListPage)
      },
      {
        path: 'payroll/:id',
        canActivate: [permissionGuard('hr.payroll:read')],
        loadComponent: () => import('./features/payroll/payroll-detail').then((m) => m.PayrollDetailPage)
      },
      {
        path: 'payroll-inputs',
        canActivate: [permissionGuard('hr.payroll:read')],
        loadComponent: () => import('./features/payroll/payroll-inputs').then((m) => m.PayrollInputsPage)
      },
      {
        path: 'attendance',
        canActivate: [permissionGuard('hr.payroll:read')],
        loadComponent: () => import('./features/payroll/attendance').then((m) => m.AttendancePage)
      },
      {
        path: 'inventory/items',
        canActivate: [permissionGuard('inv.items:read')],
        loadComponent: () => import('./features/inventory/items').then((m) => m.ItemsPage)
      },
      {
        path: 'inventory/items/:id',
        canActivate: [permissionGuard('inv.items:read')],
        loadComponent: () => import('./features/inventory/item-detail').then((m) => m.ItemDetailPage)
      },
      {
        path: 'inventory/stock',
        canActivate: [permissionGuard('inv.items:read')],
        loadComponent: () => import('./features/inventory/stock').then((m) => m.StockPage)
      },
      {
        path: 'inventory/adjustments',
        canActivate: [permissionGuard('inv.items:read')],
        loadComponent: () => import('./features/inventory/stock-documents').then((m) => m.AdjustmentsPage)
      },
      {
        path: 'inventory/transfers',
        canActivate: [permissionGuard('inv.items:read')],
        loadComponent: () => import('./features/inventory/stock-documents').then((m) => m.TransfersPage)
      },
      {
        path: 'inventory/counts',
        canActivate: [permissionGuard('inv.items:read')],
        loadComponent: () => import('./features/inventory/counts').then((m) => m.CountsPage)
      },
      {
        path: 'inventory/counts/:id',
        canActivate: [permissionGuard('inv.items:read')],
        loadComponent: () => import('./features/inventory/counts').then((m) => m.CountDetailPage)
      },
      {
        path: 'purchasing/suppliers',
        canActivate: [permissionGuard('pur.suppliers:read')],
        loadComponent: () => import('./features/purchasing/suppliers').then((m) => m.SuppliersPage)
      },
      {
        path: 'purchasing/orders',
        canActivate: [permissionGuard('pur.suppliers:read')],
        loadComponent: () => import('./features/purchasing/orders').then((m) => m.OrdersPage)
      },
      {
        path: 'purchasing/orders/new',
        canActivate: [permissionGuard('pur.orders:manage')],
        loadComponent: () => import('./features/purchasing/orders').then((m) => m.OrderFormPage)
      },
      {
        path: 'purchasing/orders/:id/edit',
        canActivate: [permissionGuard('pur.orders:manage')],
        loadComponent: () => import('./features/purchasing/orders').then((m) => m.OrderFormPage)
      },
      {
        path: 'purchasing/orders/:id',
        canActivate: [permissionGuard('pur.suppliers:read')],
        loadComponent: () => import('./features/purchasing/orders').then((m) => m.OrderDetailPage)
      },
      {
        path: 'purchasing/invoices',
        canActivate: [permissionGuard('pur.suppliers:read')],
        loadComponent: () => import('./features/purchasing/invoices').then((m) => m.InvoicesPage)
      },
      {
        path: 'purchasing/invoices/new',
        canActivate: [permissionGuard('pur.invoices:post')],
        loadComponent: () => import('./features/purchasing/invoices').then((m) => m.InvoiceFormPage)
      },
      {
        path: 'purchasing/invoices/:id',
        canActivate: [permissionGuard('pur.suppliers:read')],
        loadComponent: () => import('./features/purchasing/invoices').then((m) => m.InvoiceDetailPage)
      },
      {
        path: 'purchasing/payments',
        canActivate: [permissionGuard('pur.suppliers:read')],
        loadComponent: () => import('./features/purchasing/payables').then((m) => m.PaymentsPage)
      },
      {
        path: 'purchasing/landed-costs',
        canActivate: [permissionGuard('pur.suppliers:read')],
        loadComponent: () => import('./features/purchasing/payables').then((m) => m.LandedCostsPage)
      },
      {
        path: 'purchasing/reports',
        canActivate: [permissionGuard('pur.reports:read')],
        loadComponent: () => import('./features/purchasing/reports').then((m) => m.PurchasingReportsPage)
      },
      {
        path: 'settings/trade',
        canActivate: [permissionGuard('inv.items:read')],
        loadComponent: () => import('./features/settings/trade-settings').then((m) => m.TradeSettingsPage)
      },
      {
        path: 'leave',
        canActivate: [permissionGuard('hr.employees:read')],
        loadComponent: () => import('./features/leave/leave-list').then((m) => m.LeaveListPage)
      },
      {
        path: 'documents',
        canActivate: [permissionGuard('hr.employees:read')],
        loadComponent: () => import('./features/documents/documents').then((m) => m.DocumentsPage)
      },
      {
        path: 'settlements',
        canActivate: [permissionGuard('hr.payroll:read')],
        loadComponent: () => import('./features/settlements/settlement-list').then((m) => m.SettlementListPage)
      },
      {
        path: 'gratuity',
        canActivate: [permissionGuard('hr.payroll:read')],
        loadComponent: () => import('./features/payroll/gratuity-report').then((m) => m.GratuityReportPage)
      },
      {
        path: 'finance/accounts',
        canActivate: [permissionGuard('fin.accounts:read')],
        loadComponent: () => import('./features/finance/accounts').then((m) => m.AccountsPage)
      },
      {
        path: 'finance/trial-balance',
        canActivate: [permissionGuard('fin.reports:read')],
        loadComponent: () => import('./features/finance/trial-balance').then((m) => m.TrialBalancePage)
      },
      {
        path: 'finance/journal',
        canActivate: [permissionGuard('fin.journal:read')],
        loadComponent: () => import('./features/finance/journal').then((m) => m.JournalPage)
      },
      {
        path: 'settings/payroll',
        canActivate: [permissionGuard('hr.payroll:read')],
        loadComponent: () => import('./features/settings/hr-settings').then((m) => m.HrSettingsPage)
      },
      { path: 'settings/security', loadComponent: () => import('./features/settings/security').then((m) => m.SecurityPage) },
      { path: 'settings/company', loadComponent: () => import('./features/settings/company').then((m) => m.CompanyPage) },
      {
        path: 'settings/users',
        canActivate: [permissionGuard('core.users:read')],
        loadComponent: () => import('./features/settings/users').then((m) => m.UsersPage)
      }
    ]
  },
  { path: '**', redirectTo: '' }
];
