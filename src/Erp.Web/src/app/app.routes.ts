import { Routes } from '@angular/router';
import { authGuard, permissionGuard, selfServiceGuard, sessionGuard } from './core/auth.service';
import { Shell } from './layout/shell';

export const routes: Routes = [
  { path: 'login', loadComponent: () => import('./features/login/login').then((m) => m.LoginPage) },
  {
    path: 'change-password',
    canActivate: [sessionGuard],
    loadComponent: () => import('./features/login/change-password').then((m) => m.ChangePasswordPage)
  },
  {
    path: 'me',
    canActivate: [selfServiceGuard],
    loadChildren: () => import('./features/me/me.routes').then((m) => m.ME_ROUTES)
  },
  {
    path: '',
    component: Shell,
    canActivate: [authGuard],
    children: [
      { path: '', pathMatch: 'full', redirectTo: 'dashboard' },
      { path: 'dashboard', loadComponent: () => import('./features/dashboard/dashboard').then((m) => m.DashboardPage) },
      { path: 'approvals', loadComponent: () => import('./features/approvals/approvals').then((m) => m.ApprovalsPage) },
      {
        path: 'settings/approvals',
        canActivate: [permissionGuard('core.approvals:manage')],
        loadComponent: () => import('./features/approvals/approvals').then((m) => m.ApprovalRulesPage)
      },
      {
        path: 'purchasing/requests',
        canActivate: [permissionGuard('pur.requests:manage')],
        loadComponent: () => import('./features/purchasing/requests').then((m) => m.PurchaseRequestsPage)
      },
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
        path: 'rosters',
        canActivate: [permissionGuard('hr.employees:read')],
        loadComponent: () => import('./features/payroll/rosters').then((m) => m.RostersPage)
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
        path: 'inventory/serials',
        canActivate: [permissionGuard('inv.items:read')],
        loadComponent: () => import('./features/inventory/serials').then((m) => m.SerialLookupPage)
      },
      {
        path: 'inventory/expiry',
        canActivate: [permissionGuard('inv.items:read')],
        loadComponent: () => import('./features/inventory/batches').then((m) => m.ExpiryReportPage)
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
        path: 'purchasing/sourcing',
        canActivate: [permissionGuard('pur.suppliers:read')],
        loadComponent: () => import('./features/purchasing/sourcing').then((m) => m.SourcingPage)
      },
      {
        path: 'purchasing/rfqs/:id',
        canActivate: [permissionGuard('pur.suppliers:read')],
        loadComponent: () => import('./features/purchasing/sourcing').then((m) => m.RfqDetailPage)
      },
      {
        path: 'purchasing/blanket-agreements/:id',
        canActivate: [permissionGuard('pur.suppliers:read')],
        loadComponent: () => import('./features/purchasing/sourcing').then((m) => m.BlanketDetailPage)
      },
      {
        path: 'purchasing/subcontracts',
        canActivate: [permissionGuard('pur.suppliers:read')],
        loadComponent: () => import('./features/purchasing/subcontracts').then((m) => m.SubcontractsPage)
      },
      {
        path: 'purchasing/subcontracts/:id',
        canActivate: [permissionGuard('pur.suppliers:read')],
        loadComponent: () => import('./features/purchasing/subcontracts').then((m) => m.SubcontractDetailPage)
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
        path: 'sales/projects',
        canActivate: [permissionGuard('sal.customers:read')],
        loadComponent: () => import('./features/sales/projects').then((m) => m.ProjectsPage)
      },
      {
        path: 'sales/projects/:id',
        canActivate: [permissionGuard('sal.customers:read')],
        loadComponent: () => import('./features/sales/projects').then((m) => m.ProjectDetailPage)
      },
      {
        path: 'sales/pos',
        canActivate: [permissionGuard('sal.pos:sell')],
        loadComponent: () => import('./features/sales/pos').then((m) => m.PosPage)
      },
      {
        path: 'sales/crm',
        canActivate: [permissionGuard('sal.crm:use')],
        loadComponent: () => import('./features/sales/crm').then((m) => m.CrmPage)
      },
      {
        path: 'sales/customers',
        canActivate: [permissionGuard('sal.customers:read')],
        loadComponent: () => import('./features/sales/customers').then((m) => m.CustomersPage)
      },
      {
        path: 'sales/customers/:id',
        canActivate: [permissionGuard('sal.customers:read')],
        loadComponent: () => import('./features/sales/customers').then((m) => m.CustomerDetailPage)
      },
      {
        path: 'sales/price-lists',
        canActivate: [permissionGuard('sal.customers:read')],
        loadComponent: () => import('./features/sales/price-lists').then((m) => m.PriceListsPage)
      },
      {
        path: 'sales/quotations',
        canActivate: [permissionGuard('sal.customers:read')],
        loadComponent: () => import('./features/sales/orders').then((m) => m.QuotationsPage)
      },
      {
        path: 'sales/quotations/new',
        canActivate: [permissionGuard('sal.orders:manage')],
        data: { kind: 'quote' },
        loadComponent: () => import('./features/sales/orders').then((m) => m.SalesDocFormPage)
      },
      {
        path: 'sales/quotations/:id/edit',
        canActivate: [permissionGuard('sal.orders:manage')],
        data: { kind: 'quote' },
        loadComponent: () => import('./features/sales/orders').then((m) => m.SalesDocFormPage)
      },
      {
        path: 'sales/quotations/:id',
        canActivate: [permissionGuard('sal.customers:read')],
        loadComponent: () => import('./features/sales/orders').then((m) => m.QuotationDetailPage)
      },
      {
        path: 'sales/orders',
        canActivate: [permissionGuard('sal.customers:read')],
        loadComponent: () => import('./features/sales/orders').then((m) => m.SalesOrdersPage)
      },
      {
        path: 'sales/orders/new',
        canActivate: [permissionGuard('sal.orders:manage')],
        data: { kind: 'order' },
        loadComponent: () => import('./features/sales/orders').then((m) => m.SalesDocFormPage)
      },
      {
        path: 'sales/orders/:id/edit',
        canActivate: [permissionGuard('sal.orders:manage')],
        data: { kind: 'order' },
        loadComponent: () => import('./features/sales/orders').then((m) => m.SalesDocFormPage)
      },
      {
        path: 'sales/orders/:id',
        canActivate: [permissionGuard('sal.customers:read')],
        loadComponent: () => import('./features/sales/orders').then((m) => m.SalesOrderDetailPage)
      },
      {
        path: 'sales/deliveries',
        canActivate: [permissionGuard('sal.customers:read')],
        loadComponent: () => import('./features/sales/orders').then((m) => m.DeliveriesPage)
      },
      {
        path: 'sales/invoices',
        canActivate: [permissionGuard('sal.customers:read')],
        loadComponent: () => import('./features/sales/invoices').then((m) => m.SalesInvoicesPage)
      },
      {
        path: 'sales/invoices/new',
        canActivate: [permissionGuard('sal.invoices:post')],
        loadComponent: () => import('./features/sales/invoices').then((m) => m.SalesInvoiceFormPage)
      },
      {
        path: 'sales/invoices/:id',
        canActivate: [permissionGuard('sal.customers:read')],
        loadComponent: () => import('./features/sales/invoices').then((m) => m.SalesInvoiceDetailPage)
      },
      {
        path: 'sales/credit-notes',
        canActivate: [permissionGuard('sal.customers:read')],
        loadComponent: () => import('./features/sales/receipts').then((m) => m.CreditNotesPage)
      },
      {
        path: 'sales/credits',
        canActivate: [permissionGuard('sal.customers:read')],
        loadComponent: () => import('./features/sales/credits').then((m) => m.CustomerCreditsPage)
      },
      {
        path: 'purchasing/credits',
        canActivate: [permissionGuard('pur.suppliers:read')],
        loadComponent: () => import('./features/sales/credits').then((m) => m.SupplierCreditsPage)
      },
      {
        path: 'sales/receipts',
        canActivate: [permissionGuard('sal.customers:read')],
        loadComponent: () => import('./features/sales/receipts').then((m) => m.ReceiptsPage)
      },
      {
        path: 'sales/reports',
        canActivate: [permissionGuard('sal.reports:read')],
        loadComponent: () => import('./features/sales/reports').then((m) => m.SalesReportsPage)
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
        path: 'hr-reports',
        canActivate: [permissionGuard('hr.payroll:read')],
        loadComponent: () => import('./features/payroll/hr-reports').then((m) => m.HrReportsPage)
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
        path: 'finance/statements',
        canActivate: [permissionGuard('fin.reports:read')],
        loadComponent: () => import('./features/finance/statements').then((m) => m.StatementsPage)
      },
      {
        path: 'finance/vouchers',
        canActivate: [permissionGuard('fin.journal:read')],
        loadComponent: () => import('./features/finance/vouchers').then((m) => m.VouchersPage)
      },
      {
        path: 'finance/opening-balances',
        canActivate: [permissionGuard('fin.journal:post')],
        loadComponent: () => import('./features/finance/opening-balances').then((m) => m.OpeningBalancesPage)
      },
      {
        path: 'finance/bank',
        canActivate: [permissionGuard('fin.bank:reconcile')],
        loadComponent: () => import('./features/finance/bank').then((m) => m.BankPage)
      },
      {
        path: 'finance/bank/:id',
        canActivate: [permissionGuard('fin.bank:reconcile')],
        loadComponent: () => import('./features/finance/bank').then((m) => m.BankStatementPage)
      },
      {
        path: 'finance/assets',
        canActivate: [permissionGuard('fin.assets:read')],
        loadComponent: () => import('./features/finance/assets').then((m) => m.AssetsPage)
      },
      {
        path: 'finance/budgets',
        canActivate: [permissionGuard('fin.reports:read')],
        loadComponent: () => import('./features/finance/budgets').then((m) => m.BudgetsPage)
      },
      {
        path: 'finance/budgets/:id',
        canActivate: [permissionGuard('fin.reports:read')],
        loadComponent: () => import('./features/finance/budgets').then((m) => m.BudgetDetailPage)
      },
      {
        path: 'finance/years',
        canActivate: [permissionGuard('fin.accounts:read')],
        loadComponent: () => import('./features/finance/years').then((m) => m.FiscalYearsPage)
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
        path: 'settings/audit',
        canActivate: [permissionGuard('core.audit:read')],
        loadComponent: () => import('./features/settings/platform').then((m) => m.AuditPage)
      },
      { path: 'settings/report-schedules', loadComponent: () => import('./features/settings/report-schedules').then((m) => m.ReportSchedulesPage) },
      { path: 'settings/modules', loadComponent: () => import('./features/settings/platform').then((m) => m.ModulesPage) },
      {
        path: 'settings/users',
        canActivate: [permissionGuard('core.users:read')],
        loadComponent: () => import('./features/settings/users').then((m) => m.UsersPage)
      }
    ]
  },
  { path: '**', redirectTo: '' }
];
