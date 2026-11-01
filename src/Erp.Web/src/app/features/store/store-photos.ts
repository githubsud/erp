import { Component, inject, OnInit, signal } from '@angular/core';
import { MAT_DIALOG_DATA } from '@angular/material/dialog';
import { firstValueFrom, Observable } from 'rxjs';
import { CommerceApi, ListingImage } from '../../core/commerce.api';
import { NotifyService } from '../../core/notify.service';
import { shrinkPhoto } from '../../core/photos';
import { AuthImg } from '../../shared/auth-img';
import { SHARED } from '../../shared/shared';

export interface StorePhotosData {
  listingId: string;
  name: string;
}

export const MAX_PHOTOS = 8;

/** A product's photos in the online store: add (from the phone camera or files), choose which is shown first, delete. */
@Component({
  selector: 'app-store-photos',
  imports: [...SHARED, AuthImg],
  template: `
    <h2 mat-dialog-title>{{ 'store.photosOf' | t: { name: data.name } }}</h2>
    <mat-dialog-content>
      <p class="secondary">{{ 'store.photosHint' | t: { n: max } }}</p>
      @if (busy()) { <mat-progress-bar mode="indeterminate" /> }
      @if (loaded() && !images().length) { <p class="secondary">{{ 'store.noPhotos' | t }}</p> }
      <div class="grid">
        @for (img of images(); track img.id; let i = $index) {
          <figure [class.main]="i === 0" [attr.data-photo]="img.id">
            <app-auth-img class="pic" [source]="source(img.id)" [alt]="data.name" />
            <figcaption>
              @if (i === 0) {
                <span class="first"><mat-icon>star</mat-icon> {{ 'store.firstPhoto' | t }}</span>
              } @else {
                <button mat-icon-button [disabled]="busy()" (click)="makeFirst(img.id)" [matTooltip]="'store.makeFirst' | t" [attr.aria-label]="'store.makeFirst' | t">
                  <mat-icon>star_outline</mat-icon></button>
              }
              <span class="spacer"></span>
              <button mat-icon-button [disabled]="busy()" (click)="remove(img.id)" [matTooltip]="'store.deletePhoto' | t" [attr.aria-label]="'store.deletePhoto' | t">
                <mat-icon>delete</mat-icon></button>
            </figcaption>
          </figure>
        }
      </div>
    </mat-dialog-content>
    <mat-dialog-actions>
      <input #picker type="file" accept="image/*" multiple hidden (change)="add(picker)" data-testid="photo-input" />
      <button mat-stroked-button [disabled]="busy() || images().length >= max" (click)="picker.click()">
        <mat-icon>add_a_photo</mat-icon> {{ 'store.addPhotos' | t }}</button>
      <span class="spacer"></span>
      <button mat-button [mat-dialog-close]="changed">{{ 'app.close' | t }}</button>
    </mat-dialog-actions>
  `,
  styles: `
    .secondary { color: var(--mat-sys-on-surface-variant); font-size: 13px; }
    .spacer { flex: 1; }
    .grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(140px, 1fr)); gap: 12px; margin-block-start: 8px; }
    figure { margin: 0; border: 1px solid var(--mat-sys-outline-variant); border-radius: 8px; overflow: hidden; }
    figure.main { border: 2px solid var(--mat-sys-primary); }
    .pic { aspect-ratio: 1; }
    figcaption { display: flex; align-items: center; padding-inline: 4px; min-height: 40px; }
    .first { display: inline-flex; align-items: center; gap: 4px; font-size: 13px; color: var(--mat-sys-primary); padding-inline-start: 8px; }
    .first mat-icon { font-size: 18px; width: 18px; height: 18px; }
  `
})
export class StorePhotosDialog implements OnInit {
  protected readonly data = inject<StorePhotosData>(MAT_DIALOG_DATA);
  private readonly commerce = inject(CommerceApi);
  private readonly notify = inject(NotifyService);
  protected readonly max = MAX_PHOTOS;
  protected readonly images = signal<ListingImage[]>([]);
  protected readonly busy = signal(false);
  protected readonly loaded = signal(false);
  protected changed = false;
  private readonly sources = new Map<string, () => Observable<Blob>>();

  ngOnInit(): void {
    this.load();
  }

  /** One function per photo, so each image is fetched once. */
  protected source(id: string): () => Observable<Blob> {
    let f = this.sources.get(id);
    if (!f) this.sources.set(id, (f = () => this.commerce.imageFile(id)));
    return f;
  }

  protected async add(picker: HTMLInputElement): Promise<void> {
    const files = Array.from(picker.files ?? []).slice(0, Math.max(0, this.max - this.images().length));
    picker.value = '';
    if (!files.length) return;
    this.busy.set(true);
    try {
      for (const file of files) {
        const photo = await shrinkPhoto(file);
        await firstValueFrom(this.commerce.addImage(this.data.listingId, photo, photo === file ? file.name : 'photo.jpg'));
        this.changed = true;
      }
    } catch (e) {
      this.notify.error(e);
    } finally {
      this.load();
    }
  }

  protected makeFirst(id: string): void {
    const ids = [id, ...this.images().map((i) => i.id).filter((x) => x !== id)];
    this.run(this.commerce.orderImages(this.data.listingId, ids));
  }

  protected remove(id: string): void {
    this.run(this.commerce.deleteImage(this.data.listingId, id));
  }

  private run(call: Observable<unknown>): void {
    this.busy.set(true);
    call.subscribe({
      next: () => {
        this.changed = true;
        this.load();
      },
      error: (e) => {
        this.notify.error(e);
        this.load();
      }
    });
  }

  private load(): void {
    this.busy.set(true);
    this.commerce.images(this.data.listingId).subscribe({
      next: (list) => {
        this.images.set(list);
        this.loaded.set(true);
        this.busy.set(false);
      },
      error: (e) => {
        this.busy.set(false);
        this.notify.error(e);
      }
    });
  }
}
