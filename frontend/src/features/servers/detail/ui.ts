// Shared class strings for the design-handoff button styles used across the
// server detail page and the connection wizard.

/** Accent primary: 700 weight, uppercase, 0.07em tracking (design tokens). */
export const primaryBtn =
  "inline-flex cursor-pointer items-center justify-center gap-2 rounded-[6px] bg-primary px-6 py-3 text-[13px] font-bold uppercase tracking-[0.07em] text-primary-foreground transition hover:brightness-105 disabled:cursor-default disabled:bg-secondary disabled:text-[#8795a3] disabled:hover:brightness-100";

/** Grey secondary (the Stop state of the Start button). */
export const secondaryBtn =
  "inline-flex cursor-pointer items-center justify-center gap-2 rounded-[6px] bg-secondary px-6 py-3 text-[13px] font-bold uppercase tracking-[0.07em] text-secondary-foreground transition hover:brightness-105 disabled:cursor-default";

/** White outline button; hover picks up the accent border. */
export const outlineBtn =
  "inline-flex cursor-pointer items-center justify-center gap-[7px] rounded-[6px] border border-input bg-white px-4 py-2 text-[13px] font-semibold text-[#45596b] transition-colors hover:border-primary hover:text-primary-foreground disabled:cursor-default disabled:opacity-50";

/** 36px square icon button (backups row, secrets row). */
export const iconBtn =
  "flex size-9 cursor-pointer items-center justify-center rounded-[6px] text-[#8795a3] transition-colors hover:bg-secondary hover:text-foreground disabled:cursor-default disabled:opacity-50";
