/**
 * Makes a photo smaller in the browser before it is uploaded: at most 1600 pixels on its long side, as JPEG. Phone photos of
 * 4–10 MB become about 200–400 KB, so uploads are quick on mobile data and the shop loads fast. Transparent parts turn white.
 * When the browser cannot read the photo (an unusual format), the original goes up and the server decides.
 */
export async function shrinkPhoto(file: File, maxSide = 1600, quality = 0.85): Promise<Blob> {
  try {
    const bitmap = await createImageBitmap(file, { imageOrientation: 'from-image' });
    const scale = Math.min(1, maxSide / Math.max(bitmap.width, bitmap.height));
    const width = Math.max(1, Math.round(bitmap.width * scale));
    const height = Math.max(1, Math.round(bitmap.height * scale));
    const canvas = document.createElement('canvas');
    canvas.width = width;
    canvas.height = height;
    const context = canvas.getContext('2d');
    if (!context) return file;
    context.fillStyle = '#fff';
    context.fillRect(0, 0, width, height);
    context.drawImage(bitmap, 0, 0, width, height);
    bitmap.close();
    const blob = await new Promise<Blob | null>((resolve) => canvas.toBlob(resolve, 'image/jpeg', quality));
    return blob && (blob.size < file.size || file.type !== 'image/jpeg') ? blob : file;
  } catch {
    return file;
  }
}
