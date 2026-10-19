import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, computed, inject, signal } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { Observable, tap } from 'rxjs';
import { ConfigService } from './config.service';

const STORAGE_KEY = 'erp.backoffice';

export interface BackOfficeSession {
  accessToken: string;
  expiresAt: string;
  adminId: string;
  email: string;
  displayName: string;
}

export interface CustomerRow {
  id: string;
  code: string;
  nameEn: string;
  nameAr: string;
  tenantStatus: string;
  businessType: string | null;
  contactName: string | null;
  contactPhone: string | null;
  area: string | null;
  planCode: string | null;
  subscriptionStatus: string | null;
  billingCycle: string | null;
  price: number;
  monthlyValue: number;
  balance: number;
  overdue: number;
  oldestOverdueDue: string | null;
  nextInvoiceOn: string | null;
  trialEndsOn: string | null;
  createdAt: string;
}

export interface BoInvoice {
  id: string;
  tenantId: string;
  customerCode: string | null;
  customerNameEn: string | null;
  customerNameAr: string | null;
  invoiceNo: string;
  periodStart: string;
  periodEnd: string;
  description: string;
  amount: number;
  paidAmount: number;
  balance: number;
  issueDate: string;
  dueDate: string;
  status: 'Open' | 'Paid' | 'Void';
  overdue: boolean;
}

export interface BoPayment {
  id: string;
  invoiceId: string | null;
  invoiceNo: string | null;
  paidOn: string;
  amount: number;
  method: string;
  reference: string | null;
  recordedByName: string | null;
  notes: string | null;
}

export interface BoSubscription {
  id: string;
  planId: string;
  planCode: string;
  modules: string[];
  billingCycle: 'Monthly' | 'Annual';
  price: number;
  status: string;
  startDate: string;
  trialEndsOn: string | null;
  nextInvoiceOn: string;
  autoSuspend: boolean;
  graceDays: number;
  maxUsers: number | null;
  maxEmployees: number | null;
  notes: string | null;
}

export interface BoModule {
  code: string;
  nameEn: string;
  nameAr: string;
  enabled: boolean;
  requires: string[];
}

export interface CustomerDetail {
  customer: CustomerRow;
  notes: string | null;
  subscription: BoSubscription | null;
  invoices: BoInvoice[];
  payments: BoPayment[];
  usage: { users: number; activeUsers: number; lastLoginAt: string | null; ownerEmail: string | null };
  modules: BoModule[];
}

export interface BoPlan {
  id: string;
  code: string;
  nameEn: string;
  nameAr: string;
  monthlyPrice: number;
  annualPrice: number;
  modules: string[];
  maxUsers: number | null;
  maxEmployees: number | null;
  isActive: boolean;
  customers: number;
}

export interface BoSummary {
  customers: number;
  active: number;
  trial: number;
  pastDue: number;
  suspended: number;
  closed: number;
  monthlyRecurring: number;
  openBalance: number;
  overdue: number;
  collectedThisMonth: number;
  invoicesDueNext7Days: number;
  byBusinessType: { businessType: string; count: number }[];
}

export const BUSINESS_TYPES = ['tailoring', 'salon', 'grocery', 'retail', 'restaurant', 'furniture', 'workshop', 'typing_office', 'contracting', 'trading',
  'services', 'other'];

/** The vendor back-office: its own sign-in and token, kept apart from any customer session. */
@Injectable({ providedIn: 'root' })
export class BackOfficeService {
  private readonly http = inject(HttpClient);
  private readonly config = inject(ConfigService);
  private readonly router = inject(Router);

  readonly session = signal<BackOfficeSession | null>(this.restore());
  readonly signedIn = computed(() => {
    const s = this.session();
    return !!s && new Date(s.expiresAt).getTime() > Date.now();
  });

  get token(): string | null {
    return this.session()?.accessToken ?? null;
  }

  private url(path: string): string {
    return `${this.config.apiBaseUrl}/api/backoffice/${path}`;
  }

  login(email: string, password: string): Observable<BackOfficeSession> {
    return this.http.post<BackOfficeSession>(this.url('auth/login'), { email, password }).pipe(
      tap((s) => {
        this.session.set(s);
        try {
          sessionStorage.setItem(STORAGE_KEY, JSON.stringify(s));
        } catch {
          // Private mode: the session lasts for this page only.
        }
      })
    );
  }

  logout(): void {
    this.session.set(null);
    try {
      sessionStorage.removeItem(STORAGE_KEY);
    } catch {
      // Nothing stored.
    }
    void this.router.navigate(['/backoffice/login']);
  }

  get<T>(path: string, params?: Record<string, string | number | boolean | null | undefined>): Observable<T> {
    let p = new HttpParams();
    for (const [k, v] of Object.entries(params ?? {})) if (v !== null && v !== undefined && v !== '') p = p.set(k, String(v));
    return this.http.get<T>(this.url(path), { params: p });
  }

  post<T>(path: string, body: unknown = {}): Observable<T> {
    return this.http.post<T>(this.url(path), body);
  }

  put<T>(path: string, body: unknown): Observable<T> {
    return this.http.put<T>(this.url(path), body);
  }

  private restore(): BackOfficeSession | null {
    try {
      const raw = sessionStorage.getItem(STORAGE_KEY);
      return raw ? (JSON.parse(raw) as BackOfficeSession) : null;
    } catch {
      return null;
    }
  }
}

export const backOfficeGuard: CanActivateFn = () =>
  inject(BackOfficeService).signedIn() ? true : inject(Router).createUrlTree(['/backoffice/login']);
