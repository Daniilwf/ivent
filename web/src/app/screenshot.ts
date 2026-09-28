// The bug report's screenshot (D-121): a picture of the page as the user sees it, without what must not leave it.

/**
 * What the screenshot draws: never a password field (its dots tell the length, a «show» toggle the password) and never
 * anything marked `data-private`.
 */
export function shown(node: Node): boolean {
  if (node instanceof HTMLInputElement && node.type === 'password') return false;
  return !(node instanceof Element && node.closest('[data-private]'));
}

/** A picture of the page as it is now, before the form covers it; null when the browser cannot draw it. */
export async function captureScreenshot(): Promise<Blob | null> {
  try {
    // Loaded on the first report only: most visits never need it
    const { toBlob } = await import('html-to-image');
    return await toBlob(document.body, { pixelRatio: 1, cacheBust: true, filter: shown });
  } catch {
    return null;
  }
}
