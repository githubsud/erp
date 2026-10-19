import { HttpClient, HttpErrorResponse, HttpInterceptorFn, HttpRequest } from '@angular/common/http';
import { Injectable, computed, inject, signal } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { Observable, catchError, finalize, map, of, shareReplay, switchMap, tap, throwError } from 'rxjs';
import { BackOfficeService } from './backoffice.service';
import { ConfigService } from './config.service';
import { I18nService } from './i18n/i18n.service';
import { LoginResponse } from './models';

const STORAGE_KEY = 'erp.session';

/**
 * Session held in sessionStorage (cleared when the tab closes). Access tokens are short-lived; the rotating refresh
 * token renews them silently. Two-factor sign-in adds a code step after the password.
 */
@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly http = inject(HttpClient);
  private readonly config = inject(ConfigService);
  private readonly router = inject(Router);
  private readonly i18n = inject(I18nService);
  private refreshing: Observable<boolean> | null = null;

  readonly session = signal<LoginResponse | null>(this.restore());
  readonly user = computed(() => this.session()?.user ?? null);
  readonly isAuthenticated = computed(() => !!this.session()?.refreshToken || this.accessTokenValid());
  private readonly permissions = computed(() => new Set(this.session()?.permissions ?? []));

  /** Employees using self-service hold only "ess." permissions; they get the phone layout under /me. */
  readonly isEmployeeOnly = computed(() => {
    const p = this.session()?.permissions ?? [];
    return p.length > 0 && p.every((x) => x.startsWith('ess.'));
  });
  readonly mustChangePassword = computed(() => !!this.session()?.mustChangePassword);

  /** Where a signed-in user lands. */
  home(): string {
    if (this.mustChangePassword()) return '/change-password';
    return this.isEmployeeOnly() ? '/me' : '/dashboard';
  }

  get token(): string | null {
    return this.session()?.accessToken ?? null;
  }

  /** Completes with the server response; when `mfaRequired` is true call {@link verifyMfa} next. */
  login(tenantCode: string, email: string, password: string): Observable<LoginResponse> {
    return this.http
      .post<LoginResponse>(this.url('login'), { tenantCode, email, password })
      .pipe(tap((r) => { if (!r.mfaRequired) this.store(r, true); }));
  }

  verifyMfa(mfaToken: string, code: string): Observable<LoginResponse> {
    return this.http.post<LoginResponse>(this.url('mfa/verify'), { mfaToken, code }).pipe(tap((r) => this.store(r, true)));
  }

  /** One refresh at a time; concurrent 401s share it. Emits false when the session cannot be renewed. */
  refresh(): Observable<boolean> {
    const refreshToken = this.session()?.refreshToken;
    if (!refreshToken) return of(false);
    this.refreshing ??= this.http.post<LoginResponse>(this.url('refresh'), { refreshToken }).pipe(
      tap((r) => this.store(r, false)),
      map(() => true),
      catchError(() => of(false)),
      finalize(() => (this.refreshing = null)),
      shareReplay(1)
    );
    return this.refreshing;
  }

  logout(): void {
    const refreshToken = this.session()?.refreshToken;
    if (refreshToken) this.http.post(this.url('logout'), { refreshToken }).subscribe({ error: () => undefined });
    this.session.set(null);
    try {
      sessionStorage.removeItem(STORAGE_KEY);
    } catch {
      /* ignore */
    }
    void this.router.navigate(['/login']);
  }

  /** Owner role holds the "*" wildcard. */
  can(permission: string): boolean {
    const set = this.permissions();
    return set.has('*') || set.has(permission);
  }

  changePassword(currentPassword: string, newPassword: string): Observable<void> {
    return this.http.post<void>(this.url('password'), { currentPassword, newPassword }).pipe(
      tap(() => {
        const s = this.session();
        if (s) this.store({ ...s, mustChangePassword: false }, false);
      })
    );
  }

  setMfaEnabled(enabled: boolean): void {
    const s = this.session();
    if (s) this.store({ ...s, mfaEnabled: enabled }, false);
  }

  private store(response: LoginResponse, applyLanguage: boolean): void {
    this.session.set(response);
    try {
      sessionStorage.setItem(STORAGE_KEY, JSON.stringify(response));
    } catch {
      /* ignore */
    }
    if (applyLanguage && response.user) this.i18n.set(response.user.preferredLanguage === 'en' ? 'en' : 'ar');
  }

  private accessTokenValid(): boolean {
    const s = this.session();
    return !!s?.accessToken && !!s.expiresAt && new Date(s.expiresAt).getTime() > Date.now();
  }

  private url(path: string): string {
    return `${this.config.apiBaseUrl}/api/auth/${path}`;
  }

  private restore(): LoginResponse | null {
    try {
      const raw = sessionStorage.getItem(STORAGE_KEY);
      return raw ? (JSON.parse(raw) as LoginResponse) : null;
    } catch {
      return null;
    }
  }
}

const isAuthCall = (req: HttpRequest<unknown>) => /\/api\/auth\/(login|refresh|logout|mfa\/verify)$/.test(req.url);

/** Adds the bearer token and language; on 401 renews the session once and retries, otherwise signs out. */
export const authInterceptor: HttpInterceptorFn = (req, next) => {
  const i18n = inject(I18nService);
  // The vendor back-office has its own token and never sees a customer's session (and vice versa).
  if (req.url.includes('/api/backoffice/')) {
    const backOffice = inject(BackOfficeService);
    const headers: Record<string, string> = { 'Accept-Language': i18n.lang() };
    if (backOffice.token && !req.url.endsWith('/api/backoffice/auth/login')) headers['Authorization'] = `Bearer ${backOffice.token}`;
    return next(req.clone({ setHeaders: headers })).pipe(
      catchError((error: unknown) => {
        if (error instanceof HttpErrorResponse && error.status === 401 && !req.url.endsWith('/auth/login')) backOffice.logout();
        return throwError(() => error);
      })
    );
  }
  const auth = inject(AuthService);
  const withToken = (r: HttpRequest<unknown>) => {
    const headers: Record<string, string> = { 'Accept-Language': i18n.lang() };
    if (auth.token && !isAuthCall(r)) headers['Authorization'] = `Bearer ${auth.token}`;
    return r.clone({ setHeaders: headers });
  };

  return next(withToken(req)).pipe(
    catchError((error: unknown) => {
      if (!(error instanceof HttpErrorResponse) || error.status !== 401 || isAuthCall(req) || !auth.session()) return throwError(() => error);
      return auth.refresh().pipe(
        switchMap((ok) => {
          if (!ok) {
            auth.logout();
            return throwError(() => error);
          }
          return next(withToken(req));
        })
      );
    })
  );
};

/** Staff screens: signed in, password chosen, and not a self-service-only employee. */
export const authGuard: CanActivateFn = () => {
  const auth = inject(AuthService);
  if (!auth.isAuthenticated()) return inject(Router).createUrlTree(['/login']);
  return auth.mustChangePassword() || auth.isEmployeeOnly() ? inject(Router).createUrlTree([auth.home()]) : true;
};

/** Self-service screens under /me. */
export const selfServiceGuard: CanActivateFn = () => {
  const auth = inject(AuthService);
  if (!auth.isAuthenticated()) return inject(Router).createUrlTree(['/login']);
  if (auth.mustChangePassword()) return inject(Router).createUrlTree(['/change-password']);
  return auth.can('ess.self:use') ? true : inject(Router).createUrlTree(['/']);
};

/** Any signed-in user (the change-password page). */
export const sessionGuard: CanActivateFn = () => (inject(AuthService).isAuthenticated() ? true : inject(Router).createUrlTree(['/login']));

export function permissionGuard(permission: string): CanActivateFn {
  return () => {
    const auth = inject(AuthService);
    return auth.can(permission) ? true : inject(Router).createUrlTree(['/']);
  };
}
