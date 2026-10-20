import { DecimalPipe } from '@angular/common';
import { Component, inject, OnInit, signal } from '@angular/core';
import { FormControl } from '@angular/forms';
import { Router } from '@angular/router';
import { AiService } from '../../core/ai.service';
import { ApiService } from '../../core/api.service';
import { I18nService } from '../../core/i18n/i18n.service';
import { NotifyService } from '../../core/notify.service';
import { SHARED } from '../../shared/shared';

interface SeasonEvent {
  key: string;
  nameEn: string;
  nameAr: string;
  date: string;
}

interface Row {
  itemId: string;
  sku: string;
  nameEn: string;
  nameAr: string;
  onHand: number;
  dailyRecent: number;
  lastYearWindow: number;
  factor: number;
  hasHistory: boolean;
  forecast: number;
  suggestedExtra: number;
}

interface Report {
  from: string;
  to: string;
  event: SeasonEvent | null;
  upcoming: SeasonEvent[];
  rows: Row[];
}

/** Season planning: what will sell in the coming weeks (Ramadan, Eid, National Day, back to school) and what to buy beyond stock on hand. */
@Component({
  selector: 'app-season',
  imports: [...SHARED, DecimalPipe],
  template: `
    <div class="page-header">
      <h1>{{ 'season.title' | t }}</h1>
      <span class="spacer"></span>
      <mat-form-field subscriptSizing="dynamic"><mat-label>{{ 'season.horizon' | t }}</mat-label>
        <mat-select [formControl]="horizon" (selectionChange)="load()">
          @for (d of [14, 30, 45, 60]; track d) { <mat-option [value]="d">{{ 'season.days' | t: { n: d } }}</mat-option> }
        </mat-select></mat-form-field>
      @if (aiOn) { <button mat-stroked-button (click)="ask()"><mat-icon>auto_awesome</mat-icon> {{ 'season.askAi' | t }}</button> }
    </div>
    <p class="secondary">{{ 'season.hint' | t }}</p>
    @if (report(); as r) {
      <div class="events">
        @for (e of r.upcoming; track e.key + e.date) {
          <span class="chip" [class.ok]="e === r.event || (r.event && e.key === r.event.key && e.date === r.event.date)">
            <mat-icon>event</mat-icon> {{ e | bi:'name' }} · <span class="ltr">{{ e.date | d }}</span></span>
        }
        @if (!r.upcoming.length) { <span class="secondary">{{ 'season.noEvent' | t }}</span> }
      </div>
      <div class="table-wrap">
        <table class="line-editor">
          <thead><tr><th>{{ 'po.item' | t }}</th><th class="num">{{ 'season.onHand' | t }}</th><th class="num">{{ 'season.perDay' | t }}</th>
            <th class="num">{{ 'season.factor' | t }}</th><th class="num">{{ 'season.forecast' | t }}</th><th class="num">{{ 'season.buy' | t }}</th></tr></thead>
          <tbody>
            @for (x of r.rows; track x.itemId) {
              <tr [class.buy]="x.suggestedExtra > 0">
                <td>{{ x | bi }} <span class="secondary ltr">{{ x.sku }}</span></td>
                <td class="num ltr">{{ x.onHand | number: '1.0-2' }}</td>
                <td class="num ltr">{{ x.dailyRecent | number: '1.0-2' }}</td>
                <td class="num">@if (x.hasHistory) { <span class="ltr" [class.up]="x.factor > 1">× {{ x.factor | number: '1.0-2' }}</span> }
                  @else { <span class="secondary">{{ 'season.noHistory' | t }}</span> }</td>
                <td class="num ltr">{{ x.forecast | number: '1.0-1' }}</td>
                <td class="num ltr"><b>{{ x.suggestedExtra ? (x.suggestedExtra | number: '1.0-0') : '—' }}</b></td>
              </tr>
            }
          </tbody>
        </table>
        @if (!r.rows.length) { <p class="secondary">{{ 'season.noSales' | t }}</p> }
      </div>
    }
  `,
  styles: `
    .spacer { flex: 1; }
    .page-header { display: flex; align-items: center; gap: 12px; flex-wrap: wrap; }
    .secondary { color: var(--mat-sys-on-surface-variant); font-size: 13px; }
    .events { display: flex; flex-wrap: wrap; gap: 6px; margin-block: 8px 12px; }
    .events mat-icon { font-size: 16px; width: 16px; height: 16px; vertical-align: text-bottom; }
    .num { text-align: end; }
    tr.buy td:first-child { font-weight: 600; }
    .up { color: #1b6e3a; font-weight: 600; }
  `
})
export class SeasonPage implements OnInit {
  private readonly api = inject(ApiService);
  private readonly notify = inject(NotifyService);
  private readonly router = inject(Router);
  private readonly i18n = inject(I18nService);
  protected readonly aiOn = inject(AiService).enabled();
  protected readonly horizon = new FormControl(30, { nonNullable: true });
  protected readonly report = signal<Report | null>(null);

  ngOnInit(): void {
    this.load();
  }

  protected load(): void {
    this.api.get<Report>('inventory/reports/seasonal-demand', { horizonDays: this.horizon.value }).subscribe({
      next: (r) => this.report.set(r),
      error: (e) => this.notify.error(e)
    });
  }

  protected ask(): void {
    void this.router.navigate(['/ai'], { queryParams: { q: this.i18n.t('season.question', { n: this.horizon.value }) } });
  }
}
