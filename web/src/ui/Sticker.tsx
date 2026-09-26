import type { Player } from '../board/types';
import { playerToken } from '../design/players';

/** A player's sticker: the avatar, or the first letter on the token colour in a readable ink */
export function Sticker({
  player,
  size = 36,
}: {
  player: Pick<Player, 'name' | 'token' | 'avatar'>;
  size?: number;
}) {
  const token = playerToken(player.token);
  return (
    <span
      className="inline-grid shrink-0 place-items-center overflow-hidden rounded-full border-3 border-card font-display font-heavy ring-2 ring-ink"
      style={{
        width: size,
        height: size,
        background: token.fill,
        color: token.ink,
        fontSize: size * 0.42,
      }}
      aria-hidden
    >
      {player.avatar ? (
        <img
          src={player.avatar}
          alt=""
          width={size}
          height={size}
          className="size-full object-cover"
        />
      ) : (
        player.name.slice(0, 1)
      )}
    </span>
  );
}
