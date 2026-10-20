import { Component, inject } from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { I18nService } from './core/i18n/i18n.service';
import { PwaService } from './core/pwa.service';

@Component({
  selector: 'app-root',
  imports: [RouterOutlet],
  template: `<router-outlet />`
})
export class App {
  // Instantiated at start-up so <html lang/dir> are set before the first render.
  protected readonly i18n = inject(I18nService);
  private readonly pwa = inject(PwaService);
}
