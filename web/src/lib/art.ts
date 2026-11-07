import { apiFetchBlob, toSameOriginPath } from "../api/client";

/**
 * Card artwork sits behind the bearer token, so <img src> cannot fetch it.
 * Fetch through the single client, turn the response into a blob URL, and cache
 * one object URL per same-origin art path for the life of the session.
 */

const objectUrls = new Map<string, Promise<string>>();

export function artPath(artUrl: string): string {
  return toSameOriginPath(artUrl);
}

export function loadCardArt(artUrl: string): Promise<string> {
  const path = artPath(artUrl);
  const cached = objectUrls.get(path);
  if (cached) return cached;

  const pending = apiFetchBlob(path)
    .then((blob) => URL.createObjectURL(blob))
    .catch((error: unknown) => {
      objectUrls.delete(path);
      throw error;
    });

  objectUrls.set(path, pending);
  return pending;
}

export function clearCardArtCache(): void {
  for (const pending of objectUrls.values()) {
    pending
      .then((url) => URL.revokeObjectURL(url))
      .catch(() => {
        // Failed loads have no object URL to revoke.
      });
  }
  objectUrls.clear();
}

if (typeof window !== "undefined") {
  window.addEventListener("beforeunload", clearCardArtCache);
}
