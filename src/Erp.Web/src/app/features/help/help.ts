import { Component, computed, inject, input } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { FormControl } from '@angular/forms';
import { MatDialog } from '@angular/material/dialog';
import { Router } from '@angular/router';
import { HELP_ARTICLES, searchHelp } from '../../core/help-articles';
import { I18nService } from '../../core/i18n/i18n.service';
import { SHARED } from '../../shared/shared';

/** How-to articles with a search box, and a way to ask support when the answer is not there. */
@Component({
  selector: 'app-help',
  imports: [...SHARED],
  template: `
    <div class="page-header"><h1>{{ 'help.articles' | t }}</h1></div>
    <mat-form-field class="search"><mat-label>{{ 'help.search' | t }}</mat-label>
      <mat-icon matPrefix>search</mat-icon><input matInput [formControl]="query" dir="auto" /></mat-form-field>
    <div class="grid">
      @for (a of shown(); track a.id) {
        <a class="article" [routerLink]="['/help', a.id]">
          <mat-icon>{{ a.icon }}</mat-icon>
          <div><b>{{ a | bi:'title' }}</b><div class="secondary small">{{ first(a) }}</div></div>
        </a>
      }
    </div>
    @if (!shown().length) { <p class="secondary">{{ 'help.noMatch' | t }}</p> }
    <mat-card appearance="outlined" class="ask">
      <mat-icon>support_agent</mat-icon><span>{{ 'help.notFound' | t }}</span>
      <button mat-flat-button (click)="ask()">{{ 'help.new' | t }}</button>
    </mat-card>
  `,
  styles: `
    .search { width: 100%; max-width: 520px; }
    .grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(280px, 1fr)); gap: 10px; }
    .article { display: flex; gap: 12px; padding: 12px 14px; border: 1px solid var(--mat-sys-outline-variant); border-radius: 12px; color: inherit;
      text-decoration: none; background: var(--mat-sys-surface); }
    .article mat-icon { color: var(--mat-sys-primary); flex: none; }
    .secondary { color: var(--mat-sys-on-surface-variant); }
    .small { font-size: 12px; margin-top: 4px; display: -webkit-box; -webkit-line-clamp: 2; -webkit-box-orient: vertical; overflow: hidden; }
    .ask { display: flex; gap: 12px; align-items: center; flex-wrap: wrap; padding: 14px 16px; margin-top: 20px; }
    .ask span { flex: 1; min-width: 200px; }
  `
})
export class HelpPage {
  private readonly i18n = inject(I18nService);
  private readonly dialog = inject(MatDialog);
  private readonly router = inject(Router);
  protected readonly query = new FormControl('', { nonNullable: true });
  private readonly text = toSignal(this.query.valueChanges, { initialValue: '' });
  protected readonly shown = computed(() => (this.text().trim().length >= 3 ? searchHelp(this.text(), 20) : HELP_ARTICLES));

  protected first(a: (typeof HELP_ARTICLES)[number]): string {
    return (this.i18n.isArabic() ? a.ar : a.en)[0];
  }

  protected ask(): void {
    void import('../support/support').then((m) => m.openNewRequest(this.dialog, this.router, 'Question'));
  }
}

@Component({
  selector: 'app-help-article',
  imports: [...SHARED],
  template: `
    @if (article(); as a) {
      <div class="page-header">
        <a mat-icon-button routerLink="/help" [attr.aria-label]="'app.back' | t"><mat-icon class="flip-rtl">arrow_back</mat-icon></a>
        <h1>{{ a | bi:'title' }}</h1>
      </div>
      <mat-card appearance="outlined" class="body">
        @for (p of paragraphs(); track $index) {
          @if (step(p); as s) { <div class="step"><span class="num">{{ s.n }}</span><span>{{ s.text }}</span></div> }
          @else { <p>{{ p }}</p> }
        }
      </mat-card>
      <p class="secondary">{{ 'help.stillStuck' | t }} <a routerLink="/support">{{ 'help.mine' | t }}</a></p>
    } @else {
      <p>{{ 'help.noMatch' | t }}</p>
    }
  `,
  styles: `
    .body { padding: 16px 20px; line-height: 1.7; max-width: 760px; }
    .step { display: flex; gap: 10px; margin: 8px 0; }
    .num { flex: none; width: 24px; height: 24px; border-radius: 50%; background: var(--mat-sys-secondary-container); display: grid; place-items: center;
      font-size: 13px; font-weight: 600; }
    .secondary { color: var(--mat-sys-on-surface-variant); margin-top: 16px; }
  `
})
export class HelpArticlePage {
  readonly id = input.required<string>();
  private readonly i18n = inject(I18nService);
  protected readonly article = computed(() => HELP_ARTICLES.find((a) => a.id === this.id()) ?? null);
  protected readonly paragraphs = computed(() => {
    const a = this.article();
    return a ? (this.i18n.isArabic() ? a.ar : a.en) : [];
  });

  protected step(p: string): { n: string; text: string } | null {
    const m = /^(\d+)\.\s+(.*)$/.exec(p);
    return m ? { n: m[1], text: m[2] } : null;
  }
}
