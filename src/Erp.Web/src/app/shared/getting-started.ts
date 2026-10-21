import { Component, computed, inject, signal } from '@angular/core';
import { catchError, of } from 'rxjs';
import { ApiService } from '../core/api.service';
import { AuthService } from '../core/auth.service';
import { I18nService } from '../core/i18n/i18n.service';
import { ModulesService } from '../core/modules.service';
import { PwaService } from '../core/pwa.service';
import { SHARED } from './shared';

interface Step {
  key: string;
  order: number;
  titleEn: string;
  titleAr: string;
  hintEn: string;
  hintAr: string;
  link: string;
  done: boolean;
}

/** Steps only this device knows about: its receipt printer and whether the app is installed on it. */
function deviceSteps(pos: boolean, installed: boolean): Step[] {
  const steps: Step[] = [];
  let printer = false;
  try {
    printer = !!localStorage.getItem('erp.printer');
  } catch {
    // No storage: shown as not done.
  }
  if (pos)
    steps.push({
      key: 'printer', order: 55, titleEn: 'Set up the receipt printer', titleAr: 'جهّز طابعة الإيصالات',
      hintEn: 'On the till page press the printer button, choose the paper size and print a test receipt.',
      hintAr: 'في صفحة نقطة البيع اضغط زر الطابعة، اختر مقاس الورق واطبع إيصالاً تجريبياً.', link: '/sales/pos', done: printer
    });
  steps.push({
    key: 'app', order: 90, titleEn: 'Install the app on your phone', titleAr: 'ثبّت التطبيق على جوالك',
    hintEn: 'Open the ERP in your phone\'s browser and choose "Install the app" from your account menu.',
    hintAr: 'افتح النظام في متصفح الجوال واختر "تثبيت التطبيق" من قائمة حسابك.', link: '/help/install-app', done: installed
  });
  return steps;
}

/** The dashboard's first-day list: what is left to set up, in order, until everything is done or the owner hides it. */
@Component({
  selector: 'app-getting-started',
  imports: [...SHARED],
  template: `
    @if (visible()) {
      <mat-card appearance="outlined" class="gs">
        <div class="head">
          <mat-icon>flag</mat-icon>
          <div class="grow">
            <b>{{ 'gs.title' | t }}</b>
            <div class="secondary small">{{ 'gs.progress' | t: { done: doneCount(), n: steps().length } }}</div>
          </div>
          @if (canManage() && !itemsDone()) { <a mat-stroked-button routerLink="/settings/setup"><mat-icon>rocket_launch</mat-icon> {{ 'setup.start' | t }}</a> }
          @if (canManage()) { <button mat-button (click)="hide()">{{ 'gs.hide' | t }}</button> }
        </div>
        <mat-progress-bar mode="determinate" [value]="percent()" />
        @if (allDone()) {
          <p class="all"><mat-icon>celebration</mat-icon> {{ 'gs.allDone' | t }}</p>
        }
        <ol class="steps">
          @for (s of steps(); track s.key) {
            <li [class.done]="s.done">
              <mat-icon class="tick">{{ s.done ? 'check_circle' : 'radio_button_unchecked' }}</mat-icon>
              <div class="grow">
                <div class="title">{{ s | bi:'title' }}</div>
                @if (!s.done) { <div class="secondary small">{{ s | bi:'hint' }}</div> }
              </div>
              @if (!s.done) { <a mat-button [routerLink]="s.link">{{ 'gs.go' | t }} <mat-icon iconPositionEnd class="flip-rtl">chevron_right</mat-icon></a> }
            </li>
          }
        </ol>
      </mat-card>
    }
  `,
  styles: `
    .gs { padding: 12px 16px; margin-bottom: 16px; }
    .head { display: flex; gap: 10px; align-items: center; flex-wrap: wrap; margin-bottom: 8px; }
    .head > mat-icon { color: var(--mat-sys-primary); }
    .grow { flex: 1; min-width: 0; }
    .secondary { color: var(--mat-sys-on-surface-variant); }
    .small { font-size: 12px; }
    .steps { list-style: none; padding: 0; margin: 8px 0 0; display: flex; flex-direction: column; gap: 4px; }
    .steps li { display: flex; gap: 10px; align-items: center; padding: 6px 0; border-block-end: 1px solid var(--mat-sys-outline-variant); }
    .steps li:last-child { border-block-end: none; }
    .tick { color: var(--mat-sys-outline); flex: none; }
    .done .tick { color: #1b6e3a; }
    .done .title { color: var(--mat-sys-on-surface-variant); text-decoration: line-through; }
    .all { display: flex; gap: 8px; align-items: center; color: #1b6e3a; margin: 8px 0 0; }
  `
})
export class GettingStartedCard {
  private readonly api = inject(ApiService);
  private readonly auth = inject(AuthService);
  private readonly modules = inject(ModulesService);
  private readonly pwa = inject(PwaService);
  protected readonly i18n = inject(I18nService);
  private readonly server = signal<Step[]>([]);
  private readonly dismissed = signal(true);
  protected readonly canManage = () => this.auth.can('core.company:manage');

  protected readonly steps = computed(() => {
    if (!this.server().length) return [];
    const pos = !this.modules.disabled().has('pos') && this.auth.can('sal.pos:manage');
    return [...this.server(), ...deviceSteps(pos, this.pwa.standalone)].sort((a, b) => a.order - b.order);
  });
  protected readonly doneCount = computed(() => this.steps().filter((s) => s.done).length);
  protected readonly allDone = computed(() => this.steps().length > 0 && this.doneCount() === this.steps().length);
  protected readonly percent = computed(() => (this.steps().length ? (100 * this.doneCount()) / this.steps().length : 0));
  protected readonly itemsDone = computed(() => this.steps().find((s) => s.key === 'items')?.done ?? true);
  protected readonly visible = computed(() => !this.dismissed() && this.steps().length > 0);

  constructor() {
    this.api.get<{ dismissed: boolean; steps: Step[] }>('core/getting-started').pipe(catchError(() => of(null))).subscribe((r) => {
      if (!r) return;
      this.server.set(r.steps);
      this.dismissed.set(r.dismissed);
    });
  }

  protected hide(): void {
    this.dismissed.set(true);
    this.api.post('core/getting-started/hide').subscribe({ error: () => this.dismissed.set(false) });
  }
}
