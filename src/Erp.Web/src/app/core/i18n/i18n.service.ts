import { Directionality } from '@angular/cdk/bidi';
import { DOCUMENT, Injectable, Pipe, PipeTransform, computed, inject, signal } from '@angular/core';
import { ar } from './ar';
import { en, TranslationKey } from './en';

export type Lang = 'ar' | 'en';

const STORAGE_KEY = 'erp.lang';
const dictionaries: Record<Lang, Record<TranslationKey, string>> = { ar, en };

/**
 * Arabic-first runtime language switching. Changing the language updates <html lang/dir> and the CDK
 * Directionality signal, so Material components and overlays (dialogs, menus) mirror immediately.
 */
@Injectable({ providedIn: 'root' })
export class I18nService {
  private readonly document = inject(DOCUMENT);
  private readonly directionality = inject(Directionality);

  readonly lang = signal<Lang>(this.initialLang());
  readonly dir = computed(() => (this.lang() === 'ar' ? 'rtl' : 'ltr'));
  readonly isArabic = computed(() => this.lang() === 'ar');

  /** Number formats keep Western digits (common in Qatari business documents) in both languages. */
  private readonly moneyFormat = computed(
    () => new Intl.NumberFormat(this.lang() === 'ar' ? 'ar-QA-u-nu-latn' : 'en-QA', { minimumFractionDigits: 2, maximumFractionDigits: 2 })
  );

  constructor() {
    this.apply(this.lang());
  }

  set(lang: Lang): void {
    this.lang.set(lang);
    try {
      localStorage.setItem(STORAGE_KEY, lang);
    } catch {
      /* storage unavailable: keep the in-memory choice */
    }
    this.apply(lang);
  }

  toggle(): void {
    this.set(this.lang() === 'ar' ? 'en' : 'ar');
  }

  t(key: TranslationKey | string, params?: Record<string, string | number>): string {
    let text: string = dictionaries[this.lang()][key as TranslationKey] ?? dictionaries.en[key as TranslationKey] ?? key;
    if (params) for (const [k, v] of Object.entries(params)) text = text.replace(`{${k}}`, String(v));
    return text;
  }

  /** Picks the Arabic or English variant of a bilingual record: name → nameAr / nameEn. */
  pick(record: object | null | undefined, field = 'name'): string {
    if (!record) return '';
    const r = record as Record<string, unknown>;
    const ar = r[`${field}Ar`] as string | undefined;
    const en = r[`${field}En`] as string | undefined;
    return (this.lang() === 'ar' ? ar || en : en || ar) ?? '';
  }

  money(value: number | null | undefined): string {
    return value == null ? '' : this.moneyFormat().format(value);
  }

  private apply(lang: Lang): void {
    const dir = lang === 'ar' ? 'rtl' : 'ltr';
    this.document.documentElement.lang = lang;
    this.document.documentElement.dir = dir;
    if (this.directionality.value !== dir) {
      this.directionality.valueSignal.set(dir);
      this.directionality.change.emit(dir);
    }
  }

  private initialLang(): Lang {
    try {
      const saved = localStorage.getItem(STORAGE_KEY);
      if (saved === 'ar' || saved === 'en') return saved;
    } catch {
      /* ignore */
    }
    return 'ar';
  }
}

/** `{{ 'nav.dashboard' | t }}` — impure so it re-renders when the language signal changes. */
@Pipe({ name: 't', pure: false })
export class TranslatePipe implements PipeTransform {
  private readonly i18n = inject(I18nService);
  transform(key: string, params?: Record<string, string | number>): string {
    return this.i18n.t(key, params);
  }
}

/** `{{ employee | bi }}` → nameAr or nameEn; `{{ employee | bi:'jobTitle' }}` → jobTitleAr / jobTitleEn. */
@Pipe({ name: 'bi', pure: false })
export class BilingualPipe implements PipeTransform {
  private readonly i18n = inject(I18nService);
  transform(record: object | null | undefined, field = 'name'): string {
    return this.i18n.pick(record, field);
  }
}

/** `{{ amount | money }}` → 1,234.50 */
@Pipe({ name: 'money', pure: false })
export class MoneyPipe implements PipeTransform {
  private readonly i18n = inject(I18nService);
  transform(value: number | null | undefined): string {
    return this.i18n.money(value);
  }
}
