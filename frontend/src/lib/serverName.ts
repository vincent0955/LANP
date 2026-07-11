// Mirrors the backend's ServerNameValidator (DNS-1123 label): lowercase
// alphanumerics or '-', starting and ending alphanumeric, 1-63 chars.
const DNS1123_LABEL = /^[a-z0-9]([-a-z0-9]*[a-z0-9])?$/;

export const SERVER_NAME_RULES =
  "1–63 characters, lowercase letters, digits or '-', starting and ending with a letter or digit.";

export function isValidServerName(name: string): boolean {
  return name.length > 0 && name.length <= 63 && DNS1123_LABEL.test(name);
}

/** Suggest a valid server name from a game display name, e.g. "Counter-Strike 2" → "counter-strike-2". */
export function suggestServerName(displayName: string): string {
  return (
    displayName
      .toLowerCase()
      .replace(/[^a-z0-9]+/g, "-")
      .replace(/^-+|-+$/g, "")
      .slice(0, 63)
      .replace(/-+$/g, "") || "server"
  );
}
