/**
 * Saves rows as a CSV file that Excel opens directly (UTF-8 with BOM so Arabic shows correctly; values quoted as needed).
 */
export function downloadCsv(fileName: string, headers: string[], rows: (string | number | boolean | null | undefined)[][]): void {
  const cell = (v: string | number | boolean | null | undefined) => {
    const s = v === null || v === undefined ? '' : String(v);
    return /[",\n\r]/.test(s) ? `"${s.replace(/"/g, '""')}"` : s;
  };
  const text = [headers, ...rows].map((r) => r.map(cell).join(',')).join('\r\n');
  const blob = new Blob(['﻿' + text], { type: 'text/csv;charset=utf-8' });
  const href = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = href;
  a.download = fileName;
  a.click();
  setTimeout(() => URL.revokeObjectURL(href), 1000);
}
