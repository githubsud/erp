import { Injectable, inject } from '@angular/core';
import { ApiService } from './api.service';
import { Advance, LeaveBalance, Payslip } from './models';

export interface MySalaryComponent {
  code: string;
  nameEn: string;
  nameAr: string;
  amount: number;
}

export interface MyProfile {
  employeeId: string;
  empNo: string;
  nameEn: string;
  nameAr: string;
  jobTitleEn: string | null;
  jobTitleAr: string | null;
  departmentEn: string | null;
  departmentAr: string | null;
  nationality: string;
  joiningDate: string;
  mobile: string | null;
  email: string | null;
  qid: string | null;
  passportNo: string | null;
  bankShortName: string | null;
  iban: string | null;
  salary: MySalaryComponent[];
  monthlySalary: number;
  annualLeaveDays: number | null;
}

export interface MyPayslip {
  id: string;
  runId: string;
  year: number;
  month: number;
  paidDays: number;
  gross: number;
  deductions: number;
  net: number;
  paymentDate: string | null;
}

export interface MyLeaveRequest {
  id: string;
  leaveTypeCode: string;
  leaveTypeEn: string;
  leaveTypeAr: string;
  fromDate: string;
  toDate: string;
  days: number;
  status: 'Pending' | 'Approved' | 'Rejected' | 'Cancelled';
  reason: string | null;
}

export interface MyLeaveType {
  code: string;
  nameEn: string;
  nameAr: string;
  isPaid: boolean;
}

export interface MyLeave {
  balance: LeaveBalance;
  requests: MyLeaveRequest[];
  types: MyLeaveType[];
}

export interface MyDocument {
  id: string;
  docType: string;
  docNo: string | null;
  expiryDate: string | null;
  status: 'Valid' | 'Expiring' | 'Expired' | 'NoExpiry';
  daysLeft: number | null;
}

export interface MyAdvances {
  outstanding: number;
  advances: Advance[];
}

/** The signed-in employee's own records (server resolves the employee from the token). */
@Injectable({ providedIn: 'root' })
export class MeApi {
  private readonly api = inject(ApiService);

  profile = () => this.api.get<MyProfile>('me/profile');
  payslips = () => this.api.get<MyPayslip[]>('me/payslips');
  payslip = (id: string) => this.api.get<Payslip>(`me/payslips/${id}`);
  payslipPdf = (id: string) => this.api.download(`me/payslips/${id}/pdf`, undefined, 'payslip.pdf');
  leave = () => this.api.get<MyLeave>('me/leave');
  applyLeave = (body: { leaveTypeCode: string; fromDate: string; toDate: string; reason: string | null }) =>
    this.api.post<{ id: string; status: string }>('me/leave', body);
  cancelLeave = (id: string) => this.api.post<void>(`me/leave/${id}/cancel`);
  documents = () => this.api.get<MyDocument[]>('me/documents');
  advances = () => this.api.get<MyAdvances>('me/advances');
  salaryCertificate = (addressedTo: string | null) =>
    this.api.download('me/salary-certificate.pdf', { addressedTo }, 'salary_certificate.pdf');
}
