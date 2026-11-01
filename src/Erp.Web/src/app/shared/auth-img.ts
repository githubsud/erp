import { Component, DestroyRef, effect, inject, input, signal } from '@angular/core';
import { Observable } from 'rxjs';

/**
 * An image that needs the sign-in token (back-office photos): fetched as a blob and shown from a local address, which is
 * released when the image changes or goes away.
 */
@Component({
  selector: 'app-auth-img',
  template: `@if (url(); as u) { <img [src]="u" [alt]="alt()" /> }`,
  styles: `
    :host { display: block; overflow: hidden; background: var(--mat-sys-surface-container); }
    img { width: 100%; height: 100%; object-fit: contain; display: block; }
  `
})
export class AuthImg {
  readonly source = input.required<() => Observable<Blob>>();
  readonly alt = input('');
  protected readonly url = signal<string | null>(null);

  constructor() {
    effect((onCleanup) => {
      const sub = this.source()().subscribe({ next: (blob) => this.show(URL.createObjectURL(blob)), error: () => this.show(null) });
      onCleanup(() => sub.unsubscribe());
    });
    inject(DestroyRef).onDestroy(() => this.show(null));
  }

  private show(url: string | null): void {
    const old = this.url();
    if (old) URL.revokeObjectURL(old);
    this.url.set(url);
  }
}
