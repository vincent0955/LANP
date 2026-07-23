import { useState } from "react";
import { gameArt, gameImages, gameInitials } from "@/lib/gameArt";

interface Props {
  /** Container image tag used to resolve the game's art. */
  imageTag: string;
  /** Display name — drives the gradient + initials fallback. */
  name: string;
  /** "banner" fills a wide cover slot; "icon" fills a small square slot. */
  variant: "banner" | "icon";
}

/**
 * Absolutely-positioned fill (inset-0) that renders a game's real cover art when
 * available and gracefully degrades to a centered logo badge, then to the classic
 * gradient + initials tile — including when a remote image fails to load. The
 * caller owns the sizing container (which must be `relative overflow-hidden`) and
 * any overlays stacked on top.
 */
export function GameArt({ imageTag, name, variant }: Props) {
  const { banner, icon, logo } = gameImages(imageTag);
  const [failed, setFailed] = useState(false);

  const gradient = (
    <div
      className="absolute inset-0 flex items-center justify-center"
      style={{ background: gameArt(name) }}
    >
      <span
        className={
          variant === "icon"
            ? "text-2xl font-extrabold text-white/90"
            : "text-5xl font-extrabold tracking-[-0.02em] text-white/90"
        }
      >
        {gameInitials(name)}
      </span>
    </div>
  );

  if (variant === "banner") {
    // Wide cover art fills the whole banner.
    if (banner && !failed) {
      return (
        <img
          src={banner}
          alt=""
          aria-hidden
          loading="lazy"
          onError={() => setFailed(true)}
          className="absolute inset-0 size-full object-cover"
        />
      );
    }
    // No wide art (non-Steam games): show the logo badge on the gradient.
    if (icon && !failed) {
      return (
        <div
          className="absolute inset-0 flex items-center justify-center"
          style={{ background: gameArt(name) }}
        >
          <img
            src={icon}
            alt=""
            aria-hidden
            loading="lazy"
            onError={() => setFailed(true)}
            className="max-h-[58%] max-w-[58%] object-contain drop-shadow-[0_2px_8px_rgba(0,0,0,0.35)]"
          />
        </div>
      );
    }
    return gradient;
  }

  // Icon slot.
  if (icon && !failed) {
    // Logos sit contained on the gradient; photographic box art fills the square.
    if (logo) {
      return (
        <div
          className="absolute inset-0 flex items-center justify-center p-1.5"
          style={{ background: gameArt(name) }}
        >
          <img
            src={icon}
            alt=""
            aria-hidden
            loading="lazy"
            onError={() => setFailed(true)}
            className="size-full object-contain"
          />
        </div>
      );
    }
    return (
      <img
        src={icon}
        alt=""
        aria-hidden
        loading="lazy"
        onError={() => setFailed(true)}
        className="absolute inset-0 size-full object-cover"
      />
    );
  }
  return gradient;
}
