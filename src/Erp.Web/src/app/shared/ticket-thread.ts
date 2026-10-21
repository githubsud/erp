import { Component, input, output } from '@angular/core';
import { TicketFile, TicketMessage } from '../core/support.service';
import { SHARED } from './shared';

/** dd/MM/yyyy HH:mm in the device's time zone. */
export function dateTime(iso: string): string {
  const d = new Date(iso);
  const p = (n: number) => String(n).padStart(2, '0');
  return `${p(d.getDate())}/${p(d.getMonth() + 1)}/${d.getFullYear()} ${p(d.getHours())}:${p(d.getMinutes())}`;
}

/** A support conversation: the reader's own messages on one side, the other party's on the other, internal notes marked. */
@Component({
  selector: 'app-ticket-thread',
  imports: [...SHARED],
  template: `
    @for (m of messages(); track m.id) {
      <div class="msg" [class.mine]="m.authorKind === mine()" [class.note]="m.isInternal">
        <div class="meta">
          <b>{{ m.authorName }}</b>
          @if (m.authorKind === 'Admin' && mine() === 'Customer') { <span class="chip ok">{{ 'help.supportTeam' | t }}</span> }
          @if (m.isInternal) { <span class="chip">{{ 'help.internalNote' | t }}</span> }
          <span class="time ltr">{{ when(m.createdAt) }}</span>
        </div>
        @if (m.body) { <div class="body" dir="auto">{{ m.body }}</div> }
        @if (m.attachments.length) {
          <div class="files">
            @for (f of m.attachments; track f.id) {
              <button mat-stroked-button type="button" (click)="openFile.emit(f)">
                <mat-icon>{{ f.contentType.startsWith('image/') ? 'image' : 'attach_file' }}</mat-icon>
                <span class="ltr">{{ f.fileName }}</span> <span class="size ltr">{{ size(f.sizeBytes) }}</span>
              </button>
            }
          </div>
        }
      </div>
    }
  `,
  styles: `
    :host { display: flex; flex-direction: column; gap: 10px; }
    .msg { max-width: min(680px, 92%); padding: 10px 14px; border-radius: 14px; background: var(--mat-sys-surface-container); align-self: flex-start; }
    .msg.mine { align-self: flex-end; background: var(--mat-sys-primary-container); }
    .msg.note { background: #fff4d6; border: 1px dashed #c99a1a; }
    .meta { display: flex; gap: 8px; align-items: center; flex-wrap: wrap; font-size: 13px; margin-block-end: 4px; }
    .time { color: var(--mat-sys-on-surface-variant); margin-inline-start: auto; }
    .body { white-space: pre-wrap; overflow-wrap: anywhere; line-height: 1.6; }
    .files { display: flex; flex-wrap: wrap; gap: 6px; margin-block-start: 8px; }
    .size { color: var(--mat-sys-on-surface-variant); font-size: 12px; }
  `
})
export class TicketThread {
  readonly messages = input.required<TicketMessage[]>();
  /** Whose messages are "mine" (shown on the reader's side). */
  readonly mine = input.required<'Customer' | 'Admin'>();
  readonly openFile = output<TicketFile>();
  protected readonly when = dateTime;

  protected size(bytes: number): string {
    return bytes >= 1024 * 1024 ? `${(bytes / 1024 / 1024).toFixed(1)} MB` : `${Math.max(1, Math.round(bytes / 1024))} KB`;
  }
}
