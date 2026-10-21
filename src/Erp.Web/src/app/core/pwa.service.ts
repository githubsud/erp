import { Injectable, inject, signal } from '@angular/core';
import { MatSnackBar } from '@angular/material/snack-bar';
import { SwUpdate } from '@angular/service-worker';
import { filter } from 'rxjs';
import { I18nService } from './i18n/i18n.service';

interface InstallPromptEvent extends Event {
  prompt(): Promise<void>;
  userChoice: Promise<{ outcome: 'accepted' | 'dismissed' }>;
}

/**
 * The installed app: offers "install on this device", tells the user when a new version is ready, and tracks the
 * connection so screens can say they need the internet (the till keeps selling without it).
 */
@Injectable({ providedIn: 'root' })
export class PwaService {
  private readonly snack = inject(MatSnackBar);
  private readonly i18n = inject(I18nService);
  private readonly updates = inject(SwUpdate);
  private installPrompt: InstallPromptEvent | null = null;

  readonly online = signal(navigator.onLine);
  readonly canInstall = signal(false);
  /** Running as the installed app (from the home screen), not in a browser tab. */
  readonly standalone = window.matchMedia('(display-mode: standalone)').matches || (navigator as { standalone?: boolean }).standalone === true;
  /** iPhone and iPad have no install prompt: the user adds the app from the Share menu. */
  readonly iosHint = signal(false);

  constructor() {
    window.addEventListener('online', () => this.online.set(true));
    window.addEventListener('offline', () => this.online.set(false));
    window.addEventListener('beforeinstallprompt', (e) => {
      e.preventDefault();
      this.installPrompt = e as InstallPromptEvent;
      this.canInstall.set(true);
    });
    window.addEventListener('appinstalled', () => {
      this.installPrompt = null;
      this.canInstall.set(false);
    });
    this.iosHint.set(!this.standalone && /iPhone|iPad|iPod/.test(navigator.userAgent));

    if (!this.updates.isEnabled) return;
    this.updates.versionUpdates.pipe(filter((e) => e.type === 'VERSION_READY')).subscribe(() =>
      this.snack
        .open(this.i18n.t('pwa.updateReady'), this.i18n.t('pwa.reload'), { duration: 0 })
        .onAction()
        .subscribe(() => document.location.reload())
    );
    this.updates.unrecoverable.subscribe(() => document.location.reload());
    // Look for a new version every 30 minutes on screens that stay open all day (the till).
    setInterval(() => void this.updates.checkForUpdate().catch(() => undefined), 30 * 60_000);
  }

  install(): void {
    if (this.installPrompt) {
      void this.installPrompt.prompt();
      this.installPrompt.userChoice.then(() => {
        this.installPrompt = null;
        this.canInstall.set(false);
      });
    } else if (this.iosHint()) {
      this.snack.open(this.i18n.t('pwa.iosHint'), this.i18n.t('app.close'), { duration: 12_000 });
    }
  }
}
