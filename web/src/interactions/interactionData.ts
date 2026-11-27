import { api, rejectionCode, type Schemas } from '../api/client';
import { newCommandId } from '../api/commands';
import { answerOf, type Answer } from '../app/useLoaded';

export type Invitation = Schemas['InteractionView'];
export type Coop = Schemas['CoopView'];
export type Duel = Schemas['DuelView'];

export type PartnerRequest = Schemas['PartnerRequestView'];
type Features = Schemas['InteractionFeaturesView'];
type Season = Schemas['SeasonView'];

export type InteractionSnapshot = {
  season: Season;
  features: Features;
  invitations: Invitation[];
  coop: Coop | null;
  board: PartnerRequest[];
  duels: Duel[];
};

type ListAnswer<T> = { data?: T; error?: unknown; response: Response };

function optionalFeatureList<T>(result: ListAnswer<T[]>, empty: T[]): Answer<T[]> {
  if (result.response.status === 409 && rejectionCode(result.error) === 'feature.disabled') {
    return { kind: 'ready', value: empty };
  }
  return answerOf(result);
}

export async function readInteractions(
  seasonId: string,
  role: Schemas['CurrentUser']['role'],
  signal: AbortSignal,
): Promise<Answer<InteractionSnapshot>> {
  const options = { params: { path: { seasonId } }, signal };
  const answer = answerOf<Season>(await api.GET('/api/seasons/{seasonId}', options));
  if (answer.kind !== 'ready') return answer;

  const season = role === 'player' ? answer.value : { ...answer.value, me: null };
  const features: Features = season.interactionFeatures ?? {
    coop: false,
    duels: false,
    partnerBoard: false,
  };
  const privateActions = !!season.me?.hasInteractionActions;
  const [invitationResult, coopResult, boardResult, duelsResult] = await Promise.all([
    season.me && (features.coop || features.duels || privateActions)
      ? api.GET('/api/seasons/{seasonId}/interactions', options)
      : null,
    season.me && (features.coop || privateActions)
      ? api.GET('/api/seasons/{seasonId}/coop', options)
      : null,
    features.partnerBoard ? api.GET('/api/seasons/{seasonId}/partner-requests', options) : null,
    features.duels ? api.GET('/api/seasons/{seasonId}/duels', options) : null,
  ]);

  const invitations = invitationResult
    ? answerOf(invitationResult)
    : ({ kind: 'ready', value: [] } as const);
  const coop = coopResult ? answerOf(coopResult) : ({ kind: 'ready', value: null } as const);
  const board = boardResult
    ? optionalFeatureList(boardResult, [])
    : ({ kind: 'ready', value: [] } as const);
  const duels = duelsResult
    ? optionalFeatureList(duelsResult, [])
    : ({ kind: 'ready', value: [] } as const);

  if (invitations.kind !== 'ready') return invitations;
  if (coop.kind !== 'ready') return coop;
  if (board.kind !== 'ready') return board;
  if (duels.kind !== 'ready') return duels;

  return {
    kind: 'ready',
    value: {
      season,
      features: {
        ...features,
        partnerBoard: features.partnerBoard && boardResult?.response.ok === true,
        duels: features.duels && duelsResult?.response.ok === true,
      },
      invitations: [...invitations.value],
      coop: coop.value,
      board: [...board.value],
      duels: [...duels.value],
    },
  };
}

export type InteractionAction =
  | { kind: 'invite' | 'challenge'; playerId: string }
  | { kind: 'accept' | 'decline' | 'withdraw' | 'respond' | 'close'; id: string }
  | { kind: 'leave' | 'publish' };

export function sendInteractionAction(
  seasonId: string,
  action: InteractionAction,
  signal: AbortSignal,
) {
  const commandId = newCommandId();
  const season = { params: { path: { seasonId } }, signal };
  switch (action.kind) {
    case 'invite':
      return api.POST('/api/seasons/{seasonId}/coop/invite', {
        ...season,
        body: { commandId, partnerId: action.playerId },
      });
    case 'challenge':
      return api.POST('/api/seasons/{seasonId}/duels', {
        ...season,
        body: { commandId, opponentId: action.playerId },
      });
    case 'accept':
    case 'decline':
      return api.POST('/api/seasons/{seasonId}/interactions/{interactionId}/answer', {
        params: { path: { seasonId, interactionId: action.id } },
        body: { commandId, accept: action.kind === 'accept' },
        signal,
      });
    case 'withdraw':
      return api.POST('/api/seasons/{seasonId}/interactions/{interactionId}/withdraw', {
        params: { path: { seasonId, interactionId: action.id } },
        body: { commandId },
        signal,
      });
    case 'leave':
      return api.POST('/api/seasons/{seasonId}/coop/leave', { ...season, body: { commandId } });
    case 'publish':
      return api.POST('/api/seasons/{seasonId}/partner-requests', {
        ...season,
        body: { commandId },
      });
    case 'respond':
      return api.POST('/api/seasons/{seasonId}/partner-requests/{requestId}/respond', {
        params: { path: { seasonId, requestId: action.id } },
        body: { commandId },
        signal,
      });
    case 'close':
      return api.POST('/api/seasons/{seasonId}/partner-requests/{requestId}/close', {
        params: { path: { seasonId, requestId: action.id } },
        body: { commandId },
        signal,
      });
  }
}
