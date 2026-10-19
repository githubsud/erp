import { Component, computed, inject, input, OnInit, signal } from '@angular/core';
import { FormControl } from '@angular/forms';
import { forkJoin } from 'rxjs';
import { ApiService } from '../../core/api.service';
import { AuthService } from '../../core/auth.service';
import { todayIso } from '../../core/dates';
import { I18nService } from '../../core/i18n/i18n.service';
import { MeApi, MyAdvances, MyDocument, MyLeave, MyPayslip, MyProfile } from '../../core/me.api';
import { Payslip } from '../../core/models';
import { NotifyService } from '../../core/notify.service';
import { NATIONALITIES, SHARED } from '../../shared/shared';

/** "October 2026" / "أكتوبر 2026" */
function periodLabel(i18n: I18nService, year: number, month: number): string {
  return `${i18n.t('month.' + month)} ${year}`;
}

const ME_STYLES = `
  h2 { font: var(--mat-sys-title-medium); margin: 20px 0 8px; }
  .card { background: var(--mat-sys-surface); border-radius: 16px; padding: 16px; margin-bottom: 12px;
    border: 1px solid var(--mat-sys-outline-variant); }
  .row { display: flex; align-items: center; gap: 12px; }
  .grow { flex: 1; min-width: 0; }
  .muted { color: var(--mat-sys-on-surface-variant); font-size: 13px; }
  .big { font: var(--mat-sys-headline-small); font-weight: 700; color: var(--mat-sys-primary); }
  a.card { display: block; color: inherit; text-decoration: none; }
  a.card.row { display: flex; }
  dd { overflow-wrap: anywhere; }
  .list-item { display: flex; align-items: center; gap: 12px; padding: 12px 0; border-bottom: 1px solid var(--mat-sys-outline-variant); }
  .list-item:last-child { border-bottom: 0; }
  .empty { text-align: center; color: var(--mat-sys-on-surface-variant); padding: 32px 0; }
  .icon-circle { width: 40px; height: 40px; border-radius: 50%; display: grid; place-items: center; flex: none;
    background: var(--mat-sys-secondary-container); color: var(--mat-sys-on-secondary-container); }
`;

@Component({
  selector: 'app-me-home',
  imports: [...SHARED],
  template: `
    @if (loaded()) {
      @if (latest(); as p) {
        <a class="card hero" [routerLink]="['/me/payslips', p.id]">
          <div class="muted">{{ 'me.latestPayslip' | t }} · {{ period(p) }}</div>
          <div class="big ltr">{{ p.net | money }}</div>
          <div class="muted">{{ 'me.netPay' | t }}</div>
        </a>
      } @else {
        <div class="card muted">{{ 'me.noPayslips' | t }}</div>
      }

      <div class="tiles">
        <a class="card tile" routerLink="/me/leave">
          <mat-icon>beach_access</mat-icon>
          <div class="big ltr">{{ leave()?.balance?.balance ?? 0 }}</div>
          <div class="muted">{{ 'me.leaveDaysLeft' | t }}</div>
        </a>
        <a class="card tile" routerLink="/me/documents">
          <mat-icon [class.alert]="alerts() > 0">{{ alerts() > 0 ? 'warning' : 'verified' }}</mat-icon>
          <div class="big ltr">{{ alerts() }}</div>
          <div class="muted">{{ 'me.documentAlerts' | t }}</div>
        </a>
      </div>

      @if (pendingLeave() > 0) {
        <div class="card row"><mat-icon>hourglass_top</mat-icon><span class="grow">{{ 'me.pendingLeave' | t: { count: pendingLeave() } }}</span></div>
      }

      <h2>{{ 'me.quick' | t }}</h2>
      <a class="card row" routerLink="/me/leave" [queryParams]="{ apply: 1 }">
        <span class="icon-circle"><mat-icon>add</mat-icon></span><span class="grow">{{ 'me.applyLeave' | t }}</span>
        <mat-icon class="flip-rtl">chevron_right</mat-icon>
      </a>
      <a class="card row" routerLink="/me/more">
        <span class="icon-circle"><mat-icon>description</mat-icon></span><span class="grow">{{ 'me.salaryCertificate' | t }}</span>
        <mat-icon class="flip-rtl">chevron_right</mat-icon>
      </a>
    } @else {
      <mat-progress-bar mode="indeterminate" />
    }
  `,
  styles: ME_STYLES + `
    .hero { background: color-mix(in srgb, var(--mat-sys-primary) 10%, var(--mat-sys-surface)); border: 0; }
    .tiles { display: grid; grid-template-columns: 1fr 1fr; gap: 12px; }
    .tile mat-icon { color: var(--mat-sys-primary); }
    .tile mat-icon.alert { color: #b3261e; }
  `
})
export class MeHomePage implements OnInit {
  private readonly me = inject(MeApi);
  private readonly i18n = inject(I18nService);
  private readonly notify = inject(NotifyService);
  protected readonly loaded = signal(false);
  protected readonly latest = signal<MyPayslip | null>(null);
  protected readonly leave = signal<MyLeave | null>(null);
  protected readonly documents = signal<MyDocument[]>([]);
  protected readonly alerts = computed(() => this.documents().filter((d) => d.status === 'Expired' || d.status === 'Expiring').length);
  protected readonly pendingLeave = computed(() => this.leave()?.requests.filter((r) => r.status === 'Pending').length ?? 0);

  ngOnInit(): void {
    forkJoin({ payslips: this.me.payslips(), leave: this.me.leave(), documents: this.me.documents() }).subscribe({
      next: (r) => {
        this.latest.set(r.payslips[0] ?? null);
        this.leave.set(r.leave);
        this.documents.set(r.documents);
        this.loaded.set(true);
      },
      error: (e) => this.notify.error(e)
    });
  }

  protected period(p: MyPayslip): string {
    return periodLabel(this.i18n, p.year, p.month);
  }
}

@Component({
  selector: 'app-me-payslips',
  imports: [...SHARED],
  template: `
    <h2>{{ 'me.nav.payslips' | t }}</h2>
    @if (payslips(); as list) {
      <div class="card">
        @for (p of list; track p.id) {
          <a class="list-item link" [routerLink]="['/me/payslips', p.id]">
            <span class="icon-circle"><mat-icon>receipt_long</mat-icon></span>
            <div class="grow">
              <div>{{ period(p) }}</div>
              <div class="muted">{{ 'me.gross' | t }} <span class="ltr">{{ p.gross | money }}</span></div>
            </div>
            <strong class="ltr">{{ p.net | money }}</strong>
            <mat-icon class="flip-rtl">chevron_right</mat-icon>
          </a>
        } @empty {
          <div class="empty">{{ 'me.noPayslips' | t }}</div>
        }
      </div>
    } @else {
      <mat-progress-bar mode="indeterminate" />
    }
  `,
  styles: ME_STYLES + ` a.link { color: inherit; text-decoration: none; }`
})
export class MePayslipsPage implements OnInit {
  private readonly me = inject(MeApi);
  private readonly i18n = inject(I18nService);
  private readonly notify = inject(NotifyService);
  protected readonly payslips = signal<MyPayslip[] | null>(null);

  ngOnInit(): void {
    this.me.payslips().subscribe({ next: (p) => this.payslips.set(p), error: (e) => this.notify.error(e) });
  }

  protected period(p: MyPayslip): string {
    return periodLabel(this.i18n, p.year, p.month);
  }
}

@Component({
  selector: 'app-me-payslip',
  imports: [...SHARED],
  template: `
    <div class="row head">
      <a mat-icon-button routerLink="/me/payslips"><mat-icon class="flip-rtl">arrow_back</mat-icon></a>
      <h2 class="grow">{{ 'me.payslip' | t }}</h2>
    </div>
    @if (payslip(); as p) {
      <div class="card hero">
        <div class="muted">{{ 'me.netPay' | t }}</div>
        <div class="big ltr">{{ p.summary.net | money }}</div>
        <div class="muted">{{ 'payroll.paidDays' | t }}: <span class="ltr">{{ p.summary.paidDays }}</span></div>
      </div>
      <div class="card">
        <h3>{{ 'me.earnings' | t }}</h3>
        @for (l of earnings(); track l.lineNo) {
          <div class="list-item"><span class="grow">{{ l | bi }}</span><span class="ltr">{{ l.amount | money }}</span></div>
        }
        <div class="list-item total"><span class="grow">{{ 'me.gross' | t }}</span><span class="ltr">{{ p.summary.gross | money }}</span></div>
      </div>
      @if (deductions().length) {
        <div class="card">
          <h3>{{ 'me.deductions' | t }}</h3>
          @for (l of deductions(); track l.lineNo) {
            <div class="list-item"><span class="grow">{{ l | bi }}</span><span class="ltr">{{ l.amount | money }}</span></div>
          }
          <div class="list-item total"><span class="grow">{{ 'app.total' | t }}</span><span class="ltr">{{ p.summary.deductions | money }}</span></div>
        </div>
      }
      <button mat-flat-button class="full" (click)="pdf()" [disabled]="busy()"><mat-icon>picture_as_pdf</mat-icon> {{ 'me.downloadPdf' | t }}</button>
    } @else {
      <mat-progress-bar mode="indeterminate" />
    }
  `,
  styles: ME_STYLES + `
    .head h2 { margin: 0; }
    .hero { background: color-mix(in srgb, var(--mat-sys-primary) 10%, var(--mat-sys-surface)); border: 0; }
    h3 { font: var(--mat-sys-title-small); margin: 0 0 4px; }
    .total { font-weight: 700; }
    .full { width: 100%; height: 48px; }
  `
})
export class MePayslipPage implements OnInit {
  readonly id = input.required<string>();
  private readonly me = inject(MeApi);
  private readonly notify = inject(NotifyService);
  protected readonly payslip = signal<Payslip | null>(null);
  protected readonly busy = signal(false);
  protected readonly earnings = computed(() => this.payslip()?.lines.filter((l) => l.kind === 'Earning') ?? []);
  protected readonly deductions = computed(() => this.payslip()?.lines.filter((l) => l.kind === 'Deduction') ?? []);

  ngOnInit(): void {
    this.me.payslip(this.id()).subscribe({ next: (p) => this.payslip.set(p), error: (e) => this.notify.error(e) });
  }

  pdf(): void {
    this.busy.set(true);
    this.me.payslipPdf(this.id()).subscribe({ next: () => this.busy.set(false), error: (e) => { this.busy.set(false); this.notify.error(e); } });
  }
}

@Component({
  selector: 'app-me-documents',
  imports: [...SHARED],
  template: `
    <h2>{{ 'me.nav.documents' | t }}</h2>
    @if (documents(); as list) {
      <div class="card">
        @for (d of list; track d.id) {
          <div class="list-item">
            <span class="icon-circle"><mat-icon>{{ icon(d.docType) }}</mat-icon></span>
            <div class="grow">
              <div>{{ 'docType.' + d.docType | t }}</div>
              <div class="muted"><span class="ltr">{{ d.docNo }}</span>
                @if (d.expiryDate) { · {{ 'me.expires' | t }} <span class="ltr">{{ d.expiryDate | d }}</span> }
              </div>
            </div>
            <span class="chip" [class.ok]="d.status === 'Valid'" [class.warn]="d.status === 'Expiring'" [class.bad]="d.status === 'Expired'">
              {{ 'me.docStatus.' + d.status | t: { days: d.daysLeft ?? 0 } }}
            </span>
          </div>
        } @empty {
          <div class="empty">{{ 'me.noDocuments' | t }}</div>
        }
      </div>
      <p class="muted">{{ 'me.documentsHint' | t }}</p>
    } @else {
      <mat-progress-bar mode="indeterminate" />
    }
  `,
  styles: ME_STYLES
})
export class MeDocumentsPage implements OnInit {
  private readonly me = inject(MeApi);
  private readonly notify = inject(NotifyService);
  protected readonly documents = signal<MyDocument[] | null>(null);

  ngOnInit(): void {
    this.me.documents().subscribe({ next: (d) => this.documents.set(d), error: (e) => this.notify.error(e) });
  }

  protected icon(type: string): string {
    return type === 'Passport' ? 'flight' : type === 'HealthCard' ? 'health_and_safety' : type === 'DrivingLicence' ? 'directions_car' : 'badge';
  }
}

@Component({
  selector: 'app-me-more',
  imports: [...SHARED],
  template: `
    <h2>{{ 'me.salaryCertificate' | t }}</h2>
    <div class="card">
      <p class="muted">{{ 'me.certificateHint' | t }}</p>
      <mat-form-field class="full">
        <mat-label>{{ 'me.addressedTo' | t }}</mat-label>
        <input matInput [formControl]="addressedTo" [placeholder]="'me.addressedToHint' | t" />
      </mat-form-field>
      <button mat-flat-button class="full btn" (click)="certificate()" [disabled]="busy()">
        <mat-icon>download</mat-icon> {{ 'me.downloadCertificate' | t }}
      </button>
    </div>

    @if (profile(); as p) {
      <h2>{{ 'me.profile' | t }}</h2>
      <div class="card">
        <dl>
          <dt>{{ 'employee.empNo' | t }}</dt><dd class="ltr">{{ p.empNo }}</dd>
          <dt>{{ 'employee.jobTitle' | t }}</dt><dd>{{ p | bi:'jobTitle' }}</dd>
          <dt>{{ 'employee.department' | t }}</dt><dd>{{ p | bi:'department' }}</dd>
          <dt>{{ 'employee.nationality' | t }}</dt><dd>{{ nationality(p.nationality) }}</dd>
          <dt>{{ 'employee.joiningDate' | t }}</dt><dd class="ltr">{{ p.joiningDate | d }}</dd>
          <dt>{{ 'employee.qid' | t }}</dt><dd class="ltr">{{ p.qid || '—' }}</dd>
          <dt>{{ 'employee.passport' | t }}</dt><dd class="ltr">{{ p.passportNo || '—' }}</dd>
          <dt>{{ 'employee.iban' | t }}</dt><dd class="ltr">{{ p.bankShortName }} {{ p.iban || '—' }}</dd>
          <dt>{{ 'employee.mobile' | t }}</dt><dd class="ltr">{{ p.mobile || '—' }}</dd>
        </dl>
        <p class="muted">{{ 'me.profileHint' | t }}</p>
      </div>

      <h2>{{ 'me.salary' | t }}</h2>
      <div class="card">
        @for (s of p.salary; track s.code) {
          <div class="list-item"><span class="grow">{{ s | bi }}</span><span class="ltr">{{ s.amount | money }}</span></div>
        }
        <div class="list-item total"><span class="grow">{{ 'app.total' | t }}</span><span class="ltr">{{ p.monthlySalary | money }}</span></div>
      </div>
    }

    @if (advances(); as a) {
      @if (a.advances.length) {
        <h2>{{ 'advance.title' | t }}</h2>
        <div class="card">
          <div class="list-item total"><span class="grow">{{ 'advance.outstanding' | t }}</span><span class="ltr">{{ a.outstanding | money }}</span></div>
          @for (x of a.advances; track x.id) {
            <div class="list-item">
              <div class="grow"><span class="ltr">{{ x.advanceNo }}</span><div class="muted ltr">{{ x.advanceDate | d }}</div></div>
              <span class="ltr">{{ x.amount | money }}</span>
            </div>
          }
        </div>
      }
    }

    <h2>{{ 'me.account' | t }}</h2>
    <div class="card">
      <a class="list-item link" routerLink="/me/punch"><mat-icon>where_to_vote</mat-icon><span class="grow">{{ 'me.nav.punch' | t }}</span></a>
      <a class="list-item link" routerLink="/me/expenses"><mat-icon>receipt_long</mat-icon><span class="grow">{{ 'me.nav.expenses' | t }}</span></a>
      <a class="list-item link" routerLink="/me/roster"><mat-icon>calendar_view_week</mat-icon><span class="grow">{{ 'me.nav.roster' | t }}</span></a>
      <a class="list-item link" routerLink="/change-password"><mat-icon>password</mat-icon><span class="grow">{{ 'password.change' | t }}</span></a>
      <button class="list-item link plain" (click)="i18n.toggle()"><mat-icon>translate</mat-icon><span class="grow">{{ 'app.language' | t }}</span></button>
      <button class="list-item link plain" (click)="auth.logout()"><mat-icon>logout</mat-icon><span class="grow">{{ 'app.logout' | t }}</span></button>
    </div>
    <p class="muted install">{{ 'me.installHint' | t }}</p>
  `,
  styles: ME_STYLES + `
    .full { width: 100%; }
    .btn { height: 48px; }
    dl { display: grid; grid-template-columns: auto 1fr; gap: 8px 16px; margin: 0 0 8px; }
    dt { color: var(--mat-sys-on-surface-variant); }
    dd { margin: 0; text-align: end; }
    .total { font-weight: 700; }
    a.link, button.plain { color: inherit; text-decoration: none; width: 100%; background: none; border: 0; font: inherit; cursor: pointer;
      text-align: start; }
    .install { text-align: center; }
  `
})
export class MeMorePage implements OnInit {
  protected readonly auth = inject(AuthService);
  protected readonly i18n = inject(I18nService);
  private readonly me = inject(MeApi);
  private readonly notify = inject(NotifyService);
  protected readonly profile = signal<MyProfile | null>(null);
  protected readonly advances = signal<MyAdvances | null>(null);
  protected readonly busy = signal(false);
  protected readonly addressedTo = new FormControl('', { nonNullable: true });

  ngOnInit(): void {
    this.me.profile().subscribe({ next: (p) => this.profile.set(p), error: (e) => this.notify.error(e) });
    this.me.advances().subscribe({ next: (a) => this.advances.set(a), error: () => undefined });
  }

  certificate(): void {
    this.busy.set(true);
    this.me.salaryCertificate(this.addressedTo.value.trim() || null).subscribe({
      next: () => this.busy.set(false),
      error: (e) => {
        this.busy.set(false);
        this.notify.error(e);
      }
    });
  }

  protected nationality(code: string): string {
    const n = NATIONALITIES.find((x) => x.code === code);
    return n ? (this.i18n.isArabic() ? n.ar : n.en) : code;
  }
}

interface MyRosterDay {
  workDate: string;
  off: boolean;
  shiftCode: string | null;
  shiftNameEn: string | null;
  shiftNameAr: string | null;
  startTime: string | null;
  endTime: string | null;
}

@Component({
  selector: 'app-me-roster',
  imports: [...SHARED],
  template: `
    <h2>{{ 'me.nav.roster' | t }}</h2>
    @if (days(); as list) {
      <div class="card">
        @for (d of list; track d.workDate) {
          <div class="list-item">
            <span class="icon-circle"><mat-icon>{{ d.off ? 'weekend' : 'schedule' }}</mat-icon></span>
            <div class="grow">
              <div class="ltr">{{ d.workDate | d }}</div>
              <div class="muted">
                @if (d.off) { {{ 'roster.off' | t }} } @else { {{ i18n.isArabic() ? d.shiftNameAr : d.shiftNameEn }} ·
                  <span class="ltr">{{ d.startTime?.slice(0, 5) }}–{{ d.endTime?.slice(0, 5) }}</span> }
              </div>
            </div>
            @if (d.workDate === today) { <span class="chip ok">{{ 'me.today' | t }}</span> }
          </div>
        } @empty {
          <div class="empty">{{ 'me.noRoster' | t }}</div>
        }
      </div>
    } @else {
      <mat-progress-bar mode="indeterminate" />
    }
  `,
  styles: ME_STYLES
})
export class MeRosterPage implements OnInit {
  private readonly api = inject(ApiService);
  private readonly notify = inject(NotifyService);
  protected readonly i18n = inject(I18nService);
  protected readonly today = todayIso();
  protected readonly days = signal<MyRosterDay[] | null>(null);

  ngOnInit(): void {
    this.api.get<MyRosterDay[]>('me/roster').subscribe({ next: (d) => this.days.set(d), error: (e) => this.notify.error(e) });
  }
}
