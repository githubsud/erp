import { Pipe, PipeTransform } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatChipsModule } from '@angular/material/chips';
import { MatDialogModule } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatMenuModule } from '@angular/material/menu';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSelectModule } from '@angular/material/select';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { MatTableModule } from '@angular/material/table';
import { MatTabsModule } from '@angular/material/tabs';
import { MatTooltipModule } from '@angular/material/tooltip';
import { ReactiveFormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { BilingualPipe, MoneyPipe, TranslatePipe } from '../core/i18n/i18n.service';
import { displayDate } from '../core/dates';

/** `{{ '2026-10-31' | d }}` → 31/10/2026 */
@Pipe({ name: 'd' })
export class DisplayDatePipe implements PipeTransform {
  transform(value: string | null | undefined): string {
    return displayDate(value);
  }
}

/** The building blocks most feature pages use. */
export const SHARED = [
  ReactiveFormsModule,
  RouterLink,
  MatButtonModule,
  MatCardModule,
  MatCheckboxModule,
  MatChipsModule,
  MatDialogModule,
  MatFormFieldModule,
  MatIconModule,
  MatInputModule,
  MatMenuModule,
  MatProgressBarModule,
  MatSelectModule,
  MatSlideToggleModule,
  MatTableModule,
  MatTabsModule,
  MatTooltipModule,
  TranslatePipe,
  BilingualPipe,
  MoneyPipe,
  DisplayDatePipe
] as const;

/** Common nationalities in Qatar's workforce first, then others (ISO 3166-1 alpha-2). */
export const NATIONALITIES: { code: string; en: string; ar: string }[] = [
  { code: 'QA', en: 'Qatar', ar: 'قطر' },
  { code: 'IN', en: 'India', ar: 'الهند' },
  { code: 'NP', en: 'Nepal', ar: 'نيبال' },
  { code: 'BD', en: 'Bangladesh', ar: 'بنغلاديش' },
  { code: 'PH', en: 'Philippines', ar: 'الفلبين' },
  { code: 'PK', en: 'Pakistan', ar: 'باكستان' },
  { code: 'LK', en: 'Sri Lanka', ar: 'سريلانكا' },
  { code: 'EG', en: 'Egypt', ar: 'مصر' },
  { code: 'KE', en: 'Kenya', ar: 'كينيا' },
  { code: 'UG', en: 'Uganda', ar: 'أوغندا' },
  { code: 'SD', en: 'Sudan', ar: 'السودان' },
  { code: 'JO', en: 'Jordan', ar: 'الأردن' },
  { code: 'SY', en: 'Syria', ar: 'سوريا' },
  { code: 'LB', en: 'Lebanon', ar: 'لبنان' },
  { code: 'PS', en: 'Palestine', ar: 'فلسطين' },
  { code: 'TN', en: 'Tunisia', ar: 'تونس' },
  { code: 'MA', en: 'Morocco', ar: 'المغرب' },
  { code: 'DZ', en: 'Algeria', ar: 'الجزائر' },
  { code: 'YE', en: 'Yemen', ar: 'اليمن' },
  { code: 'SA', en: 'Saudi Arabia', ar: 'السعودية' },
  { code: 'AE', en: 'United Arab Emirates', ar: 'الإمارات' },
  { code: 'KW', en: 'Kuwait', ar: 'الكويت' },
  { code: 'BH', en: 'Bahrain', ar: 'البحرين' },
  { code: 'OM', en: 'Oman', ar: 'عمان' },
  { code: 'ID', en: 'Indonesia', ar: 'إندونيسيا' },
  { code: 'GH', en: 'Ghana', ar: 'غانا' },
  { code: 'ET', en: 'Ethiopia', ar: 'إثيوبيا' },
  { code: 'GB', en: 'United Kingdom', ar: 'المملكة المتحدة' },
  { code: 'US', en: 'United States', ar: 'الولايات المتحدة' }
];
