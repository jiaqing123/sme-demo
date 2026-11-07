import { Link } from "react-router-dom";
import type { Card } from "../api/types";
import { CardFace } from "./CardFace";

export function CardTile({
  card,
  onNavigate,
}: {
  card: Card;
  /** Lets the wallet persist its scroll offset before this link unmounts it. */
  onNavigate?: () => void;
}) {
  return (
    <Link
      to={`/wallet/cards/${card.id}`}
      onClick={onNavigate}
      className="block min-h-[44px] rounded-2xl transition-transform hover:scale-[1.01] focus-visible:outline-2 focus-visible:outline-offset-4 focus-visible:outline-sky-700"
    >
      {/* CardFace owns the accessible name, so the link announces the card once. */}
      <CardFace card={card} />
    </Link>
  );
}
