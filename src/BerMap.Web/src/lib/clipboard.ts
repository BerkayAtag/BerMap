/**
 * Copies text to the clipboard, also on plain-HTTP installs.
 *
 * navigator.clipboard exists only in a secure context (HTTPS or localhost); the
 * console is served over plain HTTP by default, so the copy falls back to a
 * hidden textarea and document.execCommand('copy'). Resolves to false when
 * neither works (the caller then shows the text for manual copying) and never
 * rejects. Kept free of React and path aliases so `npm test` covers it.
 */

export interface ClipboardEnvironment {
  isSecureContext?: boolean;
  clipboard?: { writeText(text: string): Promise<void> } | undefined;
  document?: Document;
}

function defaultEnvironment(): ClipboardEnvironment {
  return {
    isSecureContext: typeof window !== 'undefined' ? window.isSecureContext : false,
    clipboard: typeof navigator !== 'undefined' ? navigator.clipboard : undefined,
    document: typeof document !== 'undefined' ? document : undefined,
  };
}

function copyWithTextarea(doc: Document, text: string): boolean {
  const previousFocus = doc.activeElement as HTMLElement | null;
  const textarea = doc.createElement('textarea');
  textarea.value = text;
  textarea.setAttribute('readonly', '');
  textarea.setAttribute('aria-hidden', 'true');
  textarea.style.position = 'fixed';
  textarea.style.top = '0';
  textarea.style.left = '0';
  textarea.style.opacity = '0';
  textarea.style.pointerEvents = 'none';
  doc.body.appendChild(textarea);
  try {
    textarea.focus({ preventScroll: true });
    textarea.select();
    textarea.setSelectionRange(0, text.length);
    return doc.execCommand('copy');
  } catch {
    return false;
  } finally {
    textarea.remove();
    previousFocus?.focus?.({ preventScroll: true });
  }
}

export async function copyText(text: string, environment: ClipboardEnvironment = defaultEnvironment()): Promise<boolean> {
  if (environment.isSecureContext && environment.clipboard?.writeText) {
    try {
      await environment.clipboard.writeText(text);
      return true;
    } catch {
      // Permission denied or document not focused: try the legacy path.
    }
  }
  if (!environment.document?.body) return false;
  return copyWithTextarea(environment.document, text);
}
