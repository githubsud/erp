import { HttpErrorResponse } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { MatSnackBar } from '@angular/material/snack-bar';
import { I18nService } from './i18n/i18n.service';
import { Problem } from './models';

/** Toasts for success and for API problems, showing the Arabic or English server message. */
@Injectable({ providedIn: 'root' })
export class NotifyService {
  private readonly snack = inject(MatSnackBar);
  private readonly i18n = inject(I18nService);

  success(key = 'app.saved'): void {
    this.snack.open(this.i18n.t(key), this.i18n.t('app.close'), { duration: 3000, panelClass: 'snack-success' });
  }

  /** Toast with already-translated text. */
  info(text: string): void {
    this.snack.open(text, this.i18n.t('app.close'), { duration: 5000, panelClass: 'snack-success' });
  }

  error(error: unknown): void {
    this.snack.open(this.message(error), this.i18n.t('app.close'), { duration: 8000, panelClass: 'snack-error' });
  }

  /** Bilingual message from RFC 7807 problem details. */
  message(error: unknown): string {
    const problem = problemOf(error);
    if (!problem) return this.i18n.t(errorKey(error));
    return (this.i18n.isArabic() && problem.message_ar) || problem.title || this.i18n.t('app.errorTitle');
  }
}

/** No server message: say whether the server could not be reached or is down, instead of a bare "something went wrong". */
export function errorKey(error: unknown): string {
  if (!(error instanceof HttpErrorResponse)) return 'app.errorTitle';
  if (error.status === 0) return 'app.errorOffline';
  if (error.status === 502 || error.status === 503 || error.status === 504) return 'app.errorServerDown';
  return 'app.errorTitle';
}

export function problemOf(error: unknown): Problem | null {
  if (error instanceof HttpErrorResponse && error.error && typeof error.error === 'object') return error.error as Problem;
  return null;
}

/** Detailed validation messages (e.g. every employee that blocks a payroll calculation). */
export function problemErrors(error: unknown): string[] {
  const errors = problemOf(error)?.details?.errors ?? [];
  return errors.map((e) => (typeof e === 'string' ? e : `${e.record ? `#${e.record} · ` : ''}${e.field}: ${e.message}`));
}
