import { Injectable, effect, inject } from '@angular/core';
import { MatPaginatorIntl } from '@angular/material/paginator';
import { I18nService } from './i18n.service';

/** Paginator labels in the current language; re-emits when the language changes. */
@Injectable()
export class LocalizedPaginatorIntl extends MatPaginatorIntl {
  private readonly i18n = inject(I18nService);

  constructor() {
    super();
    effect(() => {
      this.i18n.lang();
      this.itemsPerPageLabel = this.i18n.t('paginator.itemsPerPage');
      this.nextPageLabel = this.i18n.t('paginator.next');
      this.previousPageLabel = this.i18n.t('paginator.previous');
      this.firstPageLabel = this.i18n.t('paginator.first');
      this.lastPageLabel = this.i18n.t('paginator.last');
      this.changes.next();
    });
  }

  override getRangeLabel = (page: number, pageSize: number, length: number): string => {
    if (length === 0) return `0 ${this.i18n.t('paginator.of')} 0`;
    const start = page * pageSize + 1;
    const end = Math.min(length, (page + 1) * pageSize);
    return `${start} – ${end} ${this.i18n.t('paginator.of')} ${length}`;
  };
}
