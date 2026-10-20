import { Component, inject, OnInit, signal } from '@angular/core';
import { FormControl } from '@angular/forms';
import { Router } from '@angular/router';
import { AiService } from '../../core/ai.service';
import { ApiService } from '../../core/api.service';
import { I18nService } from '../../core/i18n/i18n.service';
import { NotifyService } from '../../core/notify.service';
import { SHARED } from '../../shared/shared';

interface Finding {
  kind: string;
  severity: 'warn' | 'bad';
  person: string | null;
  amount: number;
  count: number;
  percent: number | null;
  en: string;
  ar: string;
}

interface Report {
  from: string;
  to: string;
  findings: Finding[];
  amountAtRisk: number;
}

const ICON: Record<string, string> = {
  cash_shortage: 'point_of_sale', refunds: 'undo', discounts: 'percent', below_cost: 'trending_down', invoice_discounts: 'receipt_long'
};

/** Possible money leaks: till shortages, refunds and discounts by cashier, sales below cost. Signals to check, not proof. */
@Component({
  selector: 'app-leaks',
  imports: [...SHARED],
  template: `
    <div class="page-header">
      <h1>{{ 'leak.title' | t }}</h1>
      <span class="spacer"></span>
      <mat-form-field subscriptSizing="dynamic"><mat-label>{{ 'leak.period' | t }}</mat-label>
        <mat-select [formControl]="days" (selectionChange)="load()">
          @for (d of [7, 30, 90]; track d) { <mat-option [value]="d">{{ 'leak.days' | t: { n: d } }}</mat-option> }
        </mat-select></mat-form-field>
    </div>
    <p class="secondary">{{ 'leak.hint' | t }}</p>
    @if (report(); as r) {
      @if (!r.findings.length) {
        <mat-card appearance="outlined" class="ok"><mat-icon>verified</mat-icon> {{ 'leak.none' | t }}</mat-card>
      } @else {
        <div class="summary">
          <span class="chip bad">{{ 'leak.atRisk' | t }} <b class="ltr">{{ r.amountAtRisk | money }}</b></span>
          @if (aiOn) { <button mat-stroked-button (click)="explain()"><mat-icon>auto_awesome</mat-icon> {{ 'leak.explain' | t }}</button> }
        </div>
        <div class="cards">
          @for (f of r.findings; track $index) {
            <mat-card appearance="outlined" class="finding" [class.bad]="f.severity === 'bad'">
              <div class="head"><mat-icon>{{ icon(f.kind) }}</mat-icon> <b>{{ 'leak.kind.' + f.kind | t }}</b>
                @if (f.person) { <span class="chip">{{ f.person }}</span> }
                <span class="spacer"></span><b class="ltr">{{ f.amount | money }}</b></div>
              <p>{{ i18n.isArabic() ? f.ar : f.en }}</p>
              <p class="secondary">{{ 'leak.what.' + f.kind | t }}</p>
            </mat-card>
          }
        </div>
      }
    }
  `,
  styles: `
    .spacer { flex: 1; }
    .page-header { display: flex; align-items: center; gap: 12px; flex-wrap: wrap; }
    .secondary { color: var(--mat-sys-on-surface-variant); font-size: 13px; }
    .summary { display: flex; align-items: center; gap: 12px; flex-wrap: wrap; margin-block: 8px 12px; }
    .cards { display: grid; grid-template-columns: repeat(auto-fill, minmax(320px, 1fr)); gap: 12px; }
    .finding { padding: 12px 16px; border-inline-start: 4px solid #b26a00; }
    .finding.bad { border-inline-start-color: #8c1d18; }
    .head { display: flex; align-items: center; gap: 8px; flex-wrap: wrap; }
    .ok { padding: 16px; display: flex; gap: 8px; align-items: center; }
    .ok mat-icon { color: #1b6e3a; }
  `
})
export class LeaksPage implements OnInit {
  private readonly api = inject(ApiService);
  private readonly notify = inject(NotifyService);
  private readonly router = inject(Router);
  protected readonly i18n = inject(I18nService);
  protected readonly aiOn = inject(AiService).enabled();
  protected readonly days = new FormControl(30, { nonNullable: true });
  protected readonly report = signal<Report | null>(null);
  protected readonly icon = (k: string) => ICON[k] ?? 'warning';

  ngOnInit(): void {
    this.load();
  }

  protected load(): void {
    this.api.get<Report>('sales/reports/leakage', { days: this.days.value }).subscribe({ next: (r) => this.report.set(r), error: (e) => this.notify.error(e) });
  }

  protected explain(): void {
    void this.router.navigate(['/ai'], { queryParams: { q: this.i18n.t('leak.question', { n: this.days.value }) } });
  }
}
