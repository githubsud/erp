import { Directionality } from '@angular/cdk/bidi';
import { TestBed } from '@angular/core/testing';
import { ar } from './ar';
import { en } from './en';
import { I18nService } from './i18n.service';

describe('I18nService', () => {
  beforeEach(() => {
    localStorage.clear();
    TestBed.configureTestingModule({});
  });

  it('starts in Arabic, right-to-left', () => {
    const i18n = TestBed.inject(I18nService);
    expect(i18n.lang()).toBe('ar');
    expect(document.documentElement.dir).toBe('rtl');
    expect(TestBed.inject(Directionality).value).toBe('rtl');
    expect(i18n.t('nav.payroll')).toBe('الرواتب');
  });

  it('switches to English and mirrors the layout direction', () => {
    const i18n = TestBed.inject(I18nService);
    i18n.toggle();
    expect(i18n.lang()).toBe('en');
    expect(document.documentElement.dir).toBe('ltr');
    expect(TestBed.inject(Directionality).value).toBe('ltr');
    expect(localStorage.getItem('erp.lang')).toBe('en');
    expect(i18n.t('nav.payroll')).toBe('Payroll');
  });

  it('interpolates parameters and falls back to the key', () => {
    const i18n = TestBed.inject(I18nService);
    i18n.set('en');
    expect(i18n.t('dashboard.daysLeft', { n: 5 })).toBe('5 days left');
    expect(i18n.t('missing.key')).toBe('missing.key');
  });

  it('picks the bilingual field for the current language with fallback', () => {
    const i18n = TestBed.inject(I18nService);
    const record = { nameEn: 'Basic Salary', nameAr: 'الراتب الأساسي', titleEn: 'Clerk', titleAr: '' };
    i18n.set('ar');
    expect(i18n.pick(record)).toBe('الراتب الأساسي');
    expect(i18n.pick(record, 'title')).toBe('Clerk');
    i18n.set('en');
    expect(i18n.pick(record)).toBe('Basic Salary');
  });

  it('formats money with Western digits in both languages', () => {
    const i18n = TestBed.inject(I18nService);
    i18n.set('ar');
    expect(i18n.money(4656.25)).toBe('4,656.25');
    i18n.set('en');
    expect(i18n.money(1000)).toBe('1,000.00');
  });

  it('has an Arabic translation for every English key', () => {
    const missing = Object.keys(en).filter((k) => !(ar as Record<string, string>)[k]);
    expect(missing).toEqual([]);
  });
});
