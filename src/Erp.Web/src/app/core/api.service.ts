import { HttpClient, HttpParams, HttpResponse } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, map } from 'rxjs';
import { ConfigService } from './config.service';
import {
  Account, Advance, AttendanceSummary, AppUser, Company, CompanyDocument, Contract, Department, EmailPayslipsResult, Employee, EmployeeDocument, EmployeeRequest,
  ExpiringDocument, GratuityRow, HrSettings, JournalEntryDetail, JournalEntrySummary, LeaveBalance, LeaveRequest, LeaveType,
  Paged, PayComponent, PayrollInput, PayrollRun, PayrollRunDetail, Payslip, Role, Settlement, TrialBalance, WpsFile
} from './models';

type Params = Record<string, string | number | boolean | null | undefined>;

/** Thin, typed client for the ERP REST API. */
@Injectable({ providedIn: 'root' })
export class ApiService {
  private readonly http = inject(HttpClient);
  private readonly config = inject(ConfigService);

  private url(path: string): string {
    return `${this.config.apiBaseUrl}/api/${path}`;
  }

  private params(values?: Params): HttpParams {
    let p = new HttpParams();
    for (const [k, v] of Object.entries(values ?? {})) if (v !== null && v !== undefined && v !== '') p = p.set(k, String(v));
    return p;
  }

  get<T>(path: string, params?: Params): Observable<T> {
    return this.http.get<T>(this.url(path), { params: this.params(params) });
  }

  post<T>(path: string, body: unknown = {}): Observable<T> {
    return this.http.post<T>(this.url(path), body);
  }

  put<T>(path: string, body: unknown): Observable<T> {
    return this.http.put<T>(this.url(path), body);
  }

  delete(path: string): Observable<void> {
    return this.http.delete<void>(this.url(path));
  }

  /** Downloads a file (PDF, CSV, SIF) and saves it with the server-provided name. */
  download(path: string, params?: Params, fallbackName = 'download'): Observable<void> {
    return this.http.get(this.url(path), { params: this.params(params), responseType: 'blob', observe: 'response' }).pipe(
      map((response: HttpResponse<Blob>) => {
        const disposition = response.headers.get('content-disposition') ?? '';
        const match = /filename\*?=(?:UTF-8'')?"?([^";]+)"?/i.exec(disposition);
        const name = match ? decodeURIComponent(match[1]) : fallbackName;
        const href = URL.createObjectURL(response.body!);
        const a = document.createElement('a');
        a.href = href;
        a.download = name;
        a.click();
        setTimeout(() => URL.revokeObjectURL(href), 1000);
      })
    );
  }

  // Core ------------------------------------------------------------------------------------------
  company = () => this.get<Company>('core/company');
  updateCompany = (body: Partial<Company>) => this.put<Company>('core/company', body);
  users = () => this.get<AppUser[]>('core/users');
  createUser = (body: unknown) => this.post<AppUser>('core/users', body);
  setUserRoles = (id: string, roles: string[]) => this.put<void>(`core/users/${id}/roles`, { roles });
  roles = () => this.get<Role[]>('core/roles');

  // HR masters ------------------------------------------------------------------------------------
  hrSettings = () => this.get<HrSettings>('hr/settings');
  updateHrSettings = (body: HrSettings) => this.put<HrSettings>('hr/settings', body);
  departments = () => this.get<Department[]>('hr/departments');
  payComponents = () => this.get<PayComponent[]>('hr/pay-components');
  leaveTypes = () => this.get<LeaveType[]>('hr/leave-types');

  employees = (params: Params) => this.get<Paged<Employee>>('hr/employees', params);
  employee = (id: string) => this.get<Employee>(`hr/employees/${id}`);
  createEmployee = (body: EmployeeRequest) => this.post<Employee>('hr/employees', body);
  updateEmployee = (id: string, body: EmployeeRequest) => this.put<Employee>(`hr/employees/${id}`, body);

  contracts = (employeeId: string) => this.get<Contract[]>(`hr/employees/${employeeId}/contracts`);
  createContract = (employeeId: string, body: unknown) => this.post<Contract>(`hr/employees/${employeeId}/contracts`, body);

  employeeDocuments = (employeeId: string) => this.get<EmployeeDocument[]>(`hr/employees/${employeeId}/documents`);
  addEmployeeDocument = (employeeId: string, body: unknown) => this.post<EmployeeDocument>(`hr/employees/${employeeId}/documents`, body);
  companyDocuments = () => this.get<CompanyDocument[]>('hr/company-documents');
  saveCompanyDocument = (id: string | null, body: unknown) =>
    id ? this.put<CompanyDocument>(`hr/company-documents/${id}`, body) : this.post<CompanyDocument>('hr/company-documents', body);
  expiringDocuments = (withinDays: number) => this.get<ExpiringDocument[]>('hr/documents/expiring', { withinDays });

  leaveRequests = (params: Params) => this.get<LeaveRequest[]>('hr/leave-requests', params);
  createLeave = (body: unknown) => this.post<LeaveRequest>('hr/leave-requests', body);
  decideLeave = (id: string, action: 'approve' | 'reject' | 'cancel') => this.post<LeaveRequest>(`hr/leave-requests/${id}/${action}`);
  leaveBalance = (employeeId: string) => this.get<LeaveBalance>(`hr/employees/${employeeId}/leave-balance`);

  advances = (employeeId: string) => this.get<Advance[]>(`hr/employees/${employeeId}/advances`);
  createAdvance = (employeeId: string, body: unknown) => this.post<Advance>(`hr/employees/${employeeId}/advances`, body);
  advanceBalance = (employeeId: string) => this.get<{ outstanding: number }>(`hr/employees/${employeeId}/advance-balance`);

  // Payroll ---------------------------------------------------------------------------------------
  payrollInputs = (year: number, month: number) => this.get<PayrollInput[]>('hr/payroll-inputs', { year, month });
  createPayrollInput = (body: unknown) => this.post<PayrollInput>('hr/payroll-inputs', body);
  deletePayrollInput = (id: string) => this.delete(`hr/payroll-inputs/${id}`);

  payrollRuns = () => this.get<PayrollRun[]>('hr/payroll-runs');
  payrollRun = (id: string) => this.get<PayrollRunDetail>(`hr/payroll-runs/${id}`);
  createPayrollRun = (year: number, month: number) => this.post<PayrollRun>('hr/payroll-runs', { year, month });
  payrollAction = (id: string, action: 'calculate' | 'approve' | 'post') => this.post<PayrollRun>(`hr/payroll-runs/${id}/${action}`);
  markPayrollPaid = (id: string, paymentAccountId: string, paymentDate: string) =>
    this.post<PayrollRun>(`hr/payroll-runs/${id}/mark-paid`, { paymentAccountId, paymentDate });
  reversePayroll = (id: string, date: string, reason: string) => this.post<PayrollRun>(`hr/payroll-runs/${id}/reverse`, { date, reason });
  payslip = (runId: string, payslipId: string) => this.get<Payslip>(`hr/payroll-runs/${runId}/payslips/${payslipId}`);
  attendance = (year: number, month: number) => this.get<AttendanceSummary[]>('hr/attendance', { year, month });
  emailPayslips = (runId: string, resend = false) =>
    this.post<EmailPayslipsResult>(`hr/payroll-runs/${runId}/email-payslips${resend ? '?resend=true' : ''}`);
  wpsFiles = (runId: string) => this.get<WpsFile[]>(`hr/payroll-runs/${runId}/wps-files`);
  generateWps = (runId: string) => this.post<WpsFile>(`hr/payroll-runs/${runId}/wps-files`);

  settlements = () => this.get<Settlement[]>('hr/final-settlements');
  settlement = (id: string) => this.get<Settlement>(`hr/final-settlements/${id}`);
  createSettlement = (employeeId: string, body: unknown) => this.post<Settlement>(`hr/employees/${employeeId}/final-settlements`, body);
  settlementAction = (id: string, action: 'post') => this.post<Settlement>(`hr/final-settlements/${id}/${action}`);
  markSettlementPaid = (id: string, paymentAccountId: string, paymentDate: string) =>
    this.post<Settlement>(`hr/final-settlements/${id}/mark-paid`, { paymentAccountId, paymentDate });
  reverseSettlement = (id: string, date: string, reason: string) => this.post<Settlement>(`hr/final-settlements/${id}/reverse`, { date, reason });
  deleteSettlement = (id: string) => this.delete(`hr/final-settlements/${id}`);

  gratuityLiability = (asOf?: string) => this.get<GratuityRow[]>('hr/reports/gratuity-liability', { asOf });

  // Finance ---------------------------------------------------------------------------------------
  accounts = () => this.get<Account[]>('finance/accounts');
  trialBalance = (from: string, to: string) => this.get<TrialBalance>('finance/reports/trial-balance', { from, to });
  journalEntries = (params: Params) => this.get<Paged<JournalEntrySummary>>('finance/journal-entries', params);
  journalEntry = (id: string) => this.get<JournalEntryDetail>(`finance/journal-entries/${id}`);
}
