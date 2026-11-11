# Совет экспертов по вопросам этапа 5 (2026-11-11)

По поручению заказчика («вызывать экспертов и думать как лучше»): на каждый вопрос — три независимых эксперта (баланс и честность гонки на 16 игроков; удобство компании друзей с телефонов; простота и надёжность реализации в этом коде), затем судья выбирает лучшее решение, совместимое со SPEC. Все решения — «по умолчанию, проверить на бете»: механики за выключенными флагами, числа — в `Ruleset`. Текст судей — ниже, на английском, как его записали агенты.

## D-525

Keep the D-525 default: a duel is a race to the next completion, the reward is coins paid by the system, there are no stakes between players, and features.duels is off. Opinion 3 supplies the engine edges and opinions 1 and 2 supply the anti-abuse rules. Every new number and toggle goes in Ruleset.
(1) Settlement: the win is decided inside the command that completes the run, as a Settlement step (D-413), not as an effect. It writes one duel-settled event that stores the winner and run id, and adds coins with CoinsReason.Duel, amount duels.rewardCoins (10). The command queue breaks ties. Editing hours after the roll changes dice but never re-runs the duel decision.
(2) Fresh run: duels.countRunsStartedAfterAccept (default true) means only a run rolled after acceptance counts. To leave room for that, duels.durationDays goes from 3 to 7. Time running out is a draw with no reward, expired by the existing timer (interaction-expired). Interaction.ExpiresAt holds the invite TTL first, then accepted + durationDays.
(3) Anti-farming: duels.samePairCooldownDays (14) between the same pair, duels.maxWinsPerSeason (default 5, tune with the simulator), and duels.maxOpenPerPlayer (1). The open-invite and open-duel limits count pending invites and accepted duels together (D-523).
(4) Drops: a voluntary drop by a duelist forfeits the duel and the opponent wins at once. This is duels.dropForfeits, default true. A tech-drop or a forced/event drop cancels the duel with no payout, because it is not the player's fault.
(5) Reject: payout and reversal work like bets (D-414). Coins are paid on completion. A rejected proof from the winner reverses the win and the coins with compensating events. The win does not pass to the opponent and the duel ends as a draw. The UI marks the win «предварительно» until the proof is approved.
(6) Exclusions as in D-525: the first finisher cannot challenge or be challenged. Players who finished later can duel. If a duelist becomes the first finisher mid-duel, a win counts without coins, mirroring D-414 and D-422. Soft-deleting a participant cancels the duel with no payout.
(7) The invite card shows both players' current runs: game, HLTB estimate and hours played.
(8) Undo: undoing a completion that settled a duel also undoes duel-settled. Undoing an acceptance after the duel is settled is refused, and the refusal lists the dependent command (invariant 12).
Rejected: opinion 1's stake escrow, where the loser's stake goes to the winner, and option 2, both players on the same game.

**Почему.** SPEC «Экономика» says «Прямых переводов между игроками нет». Coins pass between players only through item and event effects. «Проверки на дыры» lists «Подкармливать друга монетками — прямых переводов нет», and «Ставки» repeats the ban. Opinion 1's escrow sends the loser's stake to the winner, so it breaks this rule in substance. The SPEC wins. Opinion 1's farming concern is real, but the pair cooldown plus a season win cap fix it cheaply without transfers, as opinion 2 argues.
Option 2 conflicts with «игра занята, пока её кто-то играет» and needs state that spans two runs.
The sniping problem (a player near the end of a long game challenges someone who just started) is real in all three opinions. The fresh-run toggle removes it. Showing both runs on the invite and the 7-day window make it fair to accept.
Opinion 3's engine shape fits the existing patterns: a Settlement (D-413), no new base action (stays at 12), the reused Interaction table with ExpiresAt and the FireTimers timer, events that store outcomes, and only additive migrations.
Bets-style payout and reversal (D-414 and D-422) is better than opinion 2's settle-on-approval. It keeps the code paths and the frozen-first rule the same as bets and the weekly challenge (D-526).
Every number and toggle sits in Ruleset behind a flag that is off, so the owner can change or switch off duels later without code changes. That makes the choice easy to undo, and the SPEC is not contradictory here. Status: decided by default, «проверить на бете».

**Поля конфига.** `features.duels=false; duels.rewardCoins=10; duels.durationDays=7; duels.maxOpenPerPlayer=1; duels.countRunsStartedAfterAccept=true; duels.samePairCooldownDays=14; duels.maxWinsPerSeason=5; duels.dropForfeits=true (tech-drop/forced drop = cancel, no payout); invite TTL and limits via interactions.inviteTtlHours=24 and interactions.maxOpenInvitesPerPlayer=3 (D-523)`

**Заметка для заказчика.** SPEC «Решить» still lists «Нужны ли дуэли и по каким правилам». The default sits behind features.duels, which is off. Turning duels on is the owner's call at the beta. Opinion 1 offered a stake variant: the loser's coins go to the winner through escrow. That would need a SPEC change, because it is a transfer between players, so it was not adopted. If duels turn out too unattractive or too inflationary, tune rewardCoins, maxWinsPerSeason and samePairCooldownDays with the stage 3 simulator against shop prices.

## D-520/D-521

Keep the default. (1) The partner cannot have their own active run. The coop run takes the partner's single active-run slot, and their turn phase becomes "playing" this run. Accepting an invite is checked again inside the command queue: the partner must have no active run, no offered game and no pending choice. The partner can leave at any time with LeaveCoop, with no penalty (D-522). This is a fixed engine rule, not a Ruleset flag: do not add `reward.coop.partnerMayHaveOwnRun`. If the schema needs the field, RulesetSupport rejects `true`. (2) Coins are split exactly like points. The roller gets ceil(total × coinsShare) and the partner gets the rest, so the shares always add up to the total. `reward.coop.coinsShare = 0.5` lives in the Ruleset so it can be tuned after the beta or simulation (for example 0.6 each, or 1.0). Only the run's own completion coins are split, including the coins paid on an unmet condition (D-507). Bets, the weekly challenge, duels and global goals are settled per player. The split is recorded in the `coop-reward-split` event: total, both shares and the rounding. Decided by default, to be checked on the beta.

**Почему.** SPEC line 36 says a player "always has one active run and one clear next step". A partner with a second run of their own would break this. Allowing it is not a config switch: it is a redesign (turn phase per run, which run gets effects and drops, how undo works). So "no own run" is SPEC-compatible as a fixed rule. Opinion 3 is right that shipping an unsupported flag would be misleading. The Ruleset can still add it later in a backward-compatible way.

For coins, SPEC line 109 fixes only the points split (half rounded up to the roller), and line 589 leaves coins open. Splitting coins the same way keeps value conserved: no farming by playing every game in coop, and no hidden transfers between friends. It reuses the same rounding and one property test (shares add up to the total, roller's share >= partner's). Undo stays simple because the split happens inside the completion command.

Opinion 2's concern is real: coop gives the partner less reward per hour and may go unused. Making `coinsShare` a Ruleset number answers it, since the owner can raise it to 1.0 after the beta without touching code. Both parts are cheap to change later (one number, or a later flag), and SPEC is not contradictory, so the owner does not need to decide now.

Tests needed:
- Totals of 0 and 1 coins.
- Reject or undo after approval takes back both players' shares.
- Undo refuses when the dependent commands belong to either player, and the refusal names them.
- A deactivated or deleted partner is removed from the coop as if they left.
- A partner who finished but not first takes their points share without moving.
- The first finisher can never be a partner.

**Поля конфига.** `reward.coop.coinsShare = 0.5 (share for each player; roller gets ceil, partner gets the rest; coins split exactly like points). No reward.coop.partnerMayHaveOwnRun field: the partner having no run of their own is a fixed engine rule (SPEC line 36); if the field must exist, RulesetSupport rejects true.`

**Заметка для заказчика.** On the beta, watch how often coop is played and the coins per player-week. If coop goes unused, raise reward.coop.coinsShare (for example to 0.6–1.0 per player). A second run for the partner is a redesign, not a config switch.

## D-508

Off by default for season 1: `events.badEventBuyout.enabled = false`, and the engine rejects any buyout with `feature.disabled`. The config block stays so the owner can turn it on mid-season with no migration. If it is turned on, it works like this:
(1) It is a separate player command, `BuyOffBadEvent(pendingId)`, in the single command queue. It is not an effect, so it does not count toward the 12 base actions.
(2) It removes only a consequence that is still waiting: a manual effect, a roll or dice modifier, or a run condition. Consequences that already fired, such as a push or lost coins, cannot be bought off.
(3) It never applies to a bad event that came from a drop or was taken instead of coins on a paid reroll. The default is `excludeSources: ["drop","reroll"]`, and the engine enforces it, not only the UI. Content can also mark an event `buyable: false`.
(4) Price is `cost × (1 + earlier buyouts this season × costStep)`, with placeholders `cost: 10`, `costStep: 1` and `maxPerSeason: 2`. Setting `costStep: 0` gives a flat price.
(5) It is rejected when the balance is negative or would go negative, and for the player who finished first (frozen).
(6) It writes a result event, `pending-bought-off`, with the pending id and the price paid, plus a `resource-changed` coins event. Undo works by compensation (invariant 12).
An "indulgence" shop item can later be added as content, as long as it fits the existing actions. Decided «по умолчанию, проверить на бете».

**Почему.** SPEC line 119 lists the buyout only as «возможно», and line 585 leaves it as an open question. So "off" is the right default, and adding a flag does not shrink the SPEC. All three experts agree on off.

The details if it is turned on come from opinion 1, with the command shape from opinion 3. Two SPEC rules shaped them:
- SPEC line 153 says «дроп (плохой обязателен)». Buying off the drop's bad event would quietly turn that penalty into a coin cost.
- A paid reroll already costs "coins or a bad event". Buying that bad event off later collapses the choice into "always coins".
So both sources are excluded by default. The exclusion list lives in the config, so the owner can change it.

SPEC line 120 («в минусе покупать нельзя») blocks a buyout while the balance is negative. Opinion 1's balance argument is the strongest: coins pile up with the leaders, so a flat price would let a leader run away. That is why the price escalates and there is a season cap. Both are numbers in the Ruleset, and `costStep: 0` gives opinion 3's flat price if the owner wants it.

Opinion 3's pending-only command is the simplest correct implementation. It avoids the D-511 suspend flow and needs only additive storage: a pending status and a new event type.

Everything here is a Ruleset flag or number, and the feature is off by default. The SPEC is not contradictory, so this does not need the owner.

Implementation note: the source exclusion needs an origin marker on each pending item, so record where it came from (drop, reroll or other) when it is created on stage 5.

**Поля конфига.** `events.badEventBuyout: { enabled: false, cost: 10, costStep: 1, maxPerSeason: 2, excludeSources: ["drop","reroll"] }; event definition optional field buyable (default true)`

## D-509

Use the default, made stricter. Video comes only as an absolute https link. The server stores it as text (event payload plus the ProofLink column) and never fetches, previews, embeds or unfurls it. Photos are uploaded through the existing POST /api/files pipeline (D-108/D-116). A proof is valid with at least one photo or one link, and it can have both: photos plus one optional video link. The link is validated the same way as run-proof links: https only, a length cap, no userinfo, and a check on both the server and the client render. It is shown as a plain anchor with target=_blank and rel="noopener noreferrer nofollow". SubmitIrlProof and ReviewIrlProof are commands that go through the queue. ProofStatus is a column. A submitted proof can't be edited. A resubmission is a new irl-proof-submitted event that sets the status back to pending. If the link is dead, private or needs a login, the admin rejects it with a required comment and the player can resubmit; there is no penalty and the turn is not blocked. Approving a proof only closes the manual effect and never grants points, coins or movement. The form shows a hint: 'загрузи видео в любой сервис и вставь ссылку, она должна открываться без входа'. Behind features.events (off by default).

**Почему.** All three opinions back the default, and it matches the SPEC. SPEC line 156 says 'пруф фото или видео, админ подтверждает, ход не блокируется', and CONTENT `proof: media` asks for photo or video. A link to a video is a video proof, and a photo-only proof satisfies 'фото или видео', so nothing in the SPEC gets narrower.

Opinion 3 is the best argued and the closest to the invariants. It needs no new infrastructure: no ffmpeg, no large multipart uploads, no range serving, no disk quotas. That matters on a VPS with 1 core, 1 GB of RAM and a 400 MB budget, and it keeps backups small. Because the server never fetches the link, there is no SSRF. The events record results (file ids and a link string), so replay stays deterministic. ProofStatus is a column, the schema change only adds columns, and no new base action is needed.

From opinion 1 I took immutable submissions, resubmission as a new event, and treating a dead link as a rejection rather than a penalty. From opinion 2 I took photos plus an optional link and the hint text.

Everything the owner may want to adjust goes into Ruleset events.irlProof: maxFiles (default 5), allowLink (true) and allowedLinkHosts (empty means any https host). Adding video uploads later is a widening change: one more file type, with no change to the event format, since the event already holds file ids.

Two things are not part of D-509 and get their own DECISIONS entries. The first is what happens when a proof is never submitted (a free skip, as opinion 1 points out). The second is the stage-6 gallery: CONTENT example 3 says 'скинь видео в галерею'. Until then the gallery shows photos and the link, with no previews, iframes or oEmbed, because those would bring back server-side fetching.

Easy to change, so this is decided «по умолчанию, проверить на бете».

**Поля конфига.** `events.irlProof.maxFiles = 5 (int, photo uploads per proof); events.irlProof.allowLink = true (bool, allow one https video link); events.irlProof.allowedLinkHosts = [] (string[], empty = any https host); events.irlProof.maxLinkLength = 2048 (int); gated by features.events (default false)`

**Заметка для заказчика.** Decided «по умолчанию, проверить на бете». Video for now is only a link, with no upload. If you want videos stored on the site for the stage-6 gallery, it can be added later as one more file type without breaking anything.

## D-515

Keep the D-515 default and tighten it the way opinion 3 describes, which is also where all three opinions agree. (1) The voter (UserId) is stored in full in the vote events (vote-cast and vote-changed) and in the Vote table, with the unique index (PollId, UserId). The poll's `anonymous` flag is copied into the event data. The raw season export keeps it, and that export is admin-only, for debugging and season import/export, never shown to players. (2) In an anonymous poll, no UI, API or SignalR output shows the voter to anyone, the admin included. That covers the feed (FeedEntryView.Author = null), the hub push, the admin poll screen, the admin command journal, the undo preview, the undo refusal that lists dependent commands, "Your season" and the "Errors" page (CastVote body and user masked). This is done at the single chokepoint FeedVisibility, with a new Access kind AnonymousVote. (3) Each vote appears in the feed as "голос подан", with no name, no option and no time-based tally. Players see "N из M проголосовали" and their own vote, read from the Vote table filtered by their own UserId. Per-option counts are published only in the poll-closed event, together with the tie-break roll and the effect. A vote change in an anonymous poll is not published. (4) Live per-option counts are an owner-tunable flag, `showLiveCounts` in PollDefinition with a Ruleset default, off for anonymous polls. A non-anonymous poll stays Public, with name and option in the feed. (5) The poll screen and RUNBOOK say plainly: «анонимно для участников; имена хранятся только для подсчёта и видны в выгрузке сезона». The variant "the admin sees names" is rejected. The whole thing sits behind `features.polls`, which stays off by default. Status: по умолчанию, проверить на бете.

**Почему.** SPEC 12 says the log of all actions is visible to all players. SPEC 191 and 404 explicitly allow an anonymous poll ("анонимно или нет", Poll.анонимность). These fit together if the action is visible and only the voter's identity is withheld. The vote, the closing tally, the tie roll and the effect are all in the log. Invariant 4 still holds because the event keeps the result, including who voted. That keeps one vote per person, vote changes, compensating undo (invariant 12), replay integrity and season import working. Nothing in the SPEC is narrowed.

All three opinions agree on four points, and each closes a real hole in the bare default. With 16 friends, a per-vote feed line plus live per-option counts can be deanonymized by timing. Hiding counts until close stops bandwagoning and adds a reveal moment at a key moment, which DESIGN allows. The admin is a friend and often a player, so letting the admin see names would make the anonymity hollow. This matches D-515 as written, which already hides names in the admin panel.

Opinion 3 is the best argued for implementation. It uses the existing FeedVisibility per-type table, whose test fails for any event type without an entry, so vote events cannot leak by default. The flag lives in the event, so no extra lookup is needed and filtering stays correct after an undo. No schema change is needed beyond the planned additive Poll, PollOption and Vote tables, and no new base action. It also lists every leak path that needs one integration test: "no response contains the voter's userId, playerId or name".

From opinion 2 I add the honest wording and a showLiveCounts flag the owner can change. From opinion 1 I add hiding vote changes. Its admin "anonymous abuse signals" are left out as unneeded, because the engine already enforces who may vote.

Everything here is a read-side projection over data that is stored in full either way. That makes it easy to undo: showing names to the admin later, or turning on live counts, is a flag or projection change, not a migration. So no owner decision is required.

**Поля конфига.** `PollDefinition.Anonymous (bool, existing); PollDefinition.ShowLiveCounts (bool, default from Ruleset polls.showLiveCountsDefault = false for anonymous polls, true for non-anonymous); polls.allowChangeVote (existing; changes in anonymous polls are not published to the feed); features.polls (stays false by default)`

**Заметка для заказчика.** Please check on the beta. In anonymous polls, nobody, including the admin, sees who voted or which option they chose. Vote counts open only when the poll closes, and live counts can be turned on with the showLiveCounts flag. Names stay only in the database and in the admin-only season export, which are needed for counting and undo, and the interface says so. If you want the admin to see names, that is a display change, not a data change.

## D-529(2)

Skip the contract (RGG #16) in stage 5. Add no `features.contracts` flag, no ruleset field and no task yet. Record D-529(2) as decided by default, to be checked at the beta. Players who want harder runs for bigger rewards use what already exists: D-507 run conditions, risky decks, `reward.challengeBonus` challenges and the weekly challenge. If the customer asks for a contract after the beta, build it behind a flag that is off by default, as a variant of the challenge. The player requests it at roll time from a short list of conditions set in the Ruleset or content, so it is captured in the roll snapshot. The admin only approves or declines it at proof check and never imposes one on a run that is already going. The reward comes from the Ruleset, with a per-player cap per season and scaling by place. It is not allowed for frozen players (D-412) or after the deadline. A rejected proof or a rollback removes the reward, the same way D-503 does for events.

**Почему.** SPEC.md never mentions a contract (the only hit for "контракт" is the API contract), and the stage 5 list in SPEC is complete without one. Skipping it does not shrink the SPEC. Adding a mechanic SPEC doesn't ask for only grows the biggest, riskiest stage (STAGE5_ANALYSIS). All three experts agree on skipping. Opinion 2 makes the strongest single point: the admin checks proofs once or twice a day, so a contract the admin sets on a run already in progress would bind the player to a condition they never agreed to. It would also break the snapshot taken at the roll (invariant 5). Opinion 1 adds two balance risks: the admin's personal judgement deciding rewards, and the leader pulling further ahead. Both point to a future version where the player asks at the roll and the numbers live in the Ruleset. That is why I took the variant from opinions 1 and 2 over opinion 3's version, where the admin sets the contract mid-run and the player cannot remove it. Following opinion 3, I do not reserve a flag in advance. A contract is a D-507 condition plus a challengeBonus reward, so it can be added later behind a flag that is off by default, with no change to the format or schema now. Nothing here is hard to undo and SPEC has no contradiction, so the owner does not need to decide now.

**Поля конфига.** `None now. If it is built later: features.contracts (bool, default false), contracts.maxPerPlayerPerSeason, contracts.reward (extraDice/coins), contracts.rankScaling, contracts.conditions (list of allowed D-507 conditions).`

**Заметка для заказчика.** Decided by default, to be checked at the beta. If the friends want the RGG-style deal with the host, the contract can be added later behind its own flag with the limits above.

## D-484

Option (а), as already set as the default in D-529 (3). It is built in task 5.3 behind the events feature flag. Rules:
(1) The Бабл blocks a bad event that lands on the player by chance: from an item, a cell, a difficulty above hard, or a targeted event from another player. It never blocks the paid-reroll bad event or the mandatory bad event of a drop, because those are a price and a penalty.
(2) Every bad-event draw event records a required source enum: item | cell | difficulty | incoming | rerollCost | dropPenalty. Only the first four can be intercepted. The JSON schema validates the enum, and an FsCheck invariant checks that rerollCost and dropPenalty are never intercepted.
(3) One Бабл holds one charge. It is used automatically on the first matching threat, a qualifying bad event or a hostile effect, whichever comes first, as the item text says ("следующий"). The engine emits a result event `BadEventIntercepted(PlayerId, InstanceId, ObjectId, Source, EventId)`, the same shape as HostileIntercepted. `intercept` becomes a set (["badEvent","hostile"]), and each threat gets one interception check. A targeted bad event from another player is both hostile and bad, so it uses one charge, not two. When a player holds several interceptors, the oldest instance goes first, and the interface states this.
(4) A cancelled bad event counts as "did not happen": no rewards, achievement progress or condition credit for it.
(5) The interface does not hide the paid-reroll and drop options while a Бабл is active. It labels them «Бабл не защищает от платы/штрафа». A drop does not use up the Бабл charge. These texts go into the UI dictionary and GLOSSARY.
(6) Interception happens before any mid-effect question (D-407). Rollback returns the charge and the bad event through compensating events in reverse order, and refuses when later commands depend on it.
(7) If a coin buy-out of bad events is added later, it stays a separate mechanic, and the Бабл resolves before it.
A cap on active Бабл effects per player is an optional Ruleset number for the balance simulator, not a hard-coded rule. Decided by default, to be checked on the beta.

**Почему.** The SPEC treats the reroll bad event as a payment ("следующие стоят монеток или плохой ивент") and the drop bad event as a mandatory penalty ("обязательный плохой ивент"). Option (б) would let a 30-coin item cancel both, which shrinks SPEC rules, so the SPEC rules it out. Option (в) is already covered by D-529: implementation waits for task 5.3, and (а) applies at that moment with no rework.
All three experts agree on (а). The third gives the cleanest implementation: a required source enum, a set-valued intercept that reuses the Shield's Interception path, no new base action (the 12/12 cap holds), and a result event, so replay stays deterministic. The second adds the most player-friendly interface detail: label the options instead of hiding them, and don't use up the charge on a drop. The first adds the anti-farming rule (a cancelled event gives no credit).
The first expert's hard limit of one active Бабл is not in the SPEC, so it becomes an optional Ruleset number for the stage-3 balance simulator rather than a code rule. The second expert's ban on picking the bad-event reroll while a Бабл is active is too restrictive; the warning label is enough.
All of this is content semantics plus an additive event type and enum, so it is easy to undo. The owner can still pick a different variant before task 5.3, as D-484 is listed in «Ждут решения».

**Поля конфига.** `Optional, not required: effects.bubble.maxActive (int|null, default null = unlimited; for tuning with the balance simulator). The events feature flag (features.events) gates the whole mechanic. The intercept-source enum is fixed in the content schema, not in the Ruleset.`

**Заметка для заказчика.** D-484 stays in the «Ждут решения» list as before. Unless you choose otherwise before task 5.3, the Бабл blocks only random bad events (item, cell, difficulty, events from others). It does not block the paid-reroll bad event or the mandatory drop bad event. If you want those blocked too (option б), say so, but it makes rerolls and drops nearly free for players with coins.

## D-507

Confirm the default: an unmet event condition means the run counts «без кубов, но с монетками» (`reward.unmetConditionPolicy = noDiceKeepCoins`). Status: decided by default, to be checked in the beta. Rules for the engine:
(1) "No dice" means the run gives no dice at all: no difficulty dice, no `ChallengeExtraDice`, no `grantEvent` good event from the difficulty, and no one-shot "+dice" modifiers. Those modifiers are used up with this run. So the run gives no points and no move. Coins still follow the D-96 formula over credited hours.
(2) The run still counts as a completion. It is not a drop and not a reject. The game is closed for the season, and the run counts toward stats, achievements and challenges. A reject keeps its normal meaning, and "not met" never takes coins away.
(3) The result of the check is written into the completion events as an explicit field (`ConditionMet` plus the list of conditions checked on `CompletionRolled` or `RunCompleted`). Replay never recomputes it.
(4) The condition belongs to that one run. It is closed when the run closes, met or not. A contradicting condition still waiting for the next game (D-506) is not judged on the current run.
(5) When the admin changes the difficulty at approval (D-97), the condition is checked again in both directions. If it becomes unmet, the dice are removed from the end and points and cells are taken back as a difference. If it becomes met, the missing dice are rolled. The same dependency checks apply as for hour edits. Hour edits on a run with an unmet condition roll nothing extra.
(6) The admin can mark a condition as not applicable to this game at proof review (for example, the game has no hard mode).
(7) The interface shows the condition on the roll result and on the run page. Before the player confirms the completion, it warns: «Условие ивента не выполнено: кубов не будет, монетки начислятся».
(8) If there are several conditions, the run counts as unmet when any one of them fails. In co-op, the condition is checked for the player it was put on, and the shared dice are 0 for both players.
`ignore` and `countAsDrop` stay in the schema as Ruleset options. If `countAsDrop` is not built in stage 5, config validation rejects it with a clear error (`ruleset.unsupported`), and this is written in DECISIONS.md. The value is not removed from the schema.

**Почему.** SPEC line 110 sets this exact default («по умолчанию прохождение засчитывается без кубов, но с монетками»). The «Решить» item on line 586 only asks to confirm it, and it is already in ruleset.default.json and D-507. All three experts agree. The order of outcomes stays right for balance: condition met > not met (coins only) > drop. So it always pays to finish the game, and nothing can be farmed by failing the condition on purpose. It is also gentle enough for a group of friends: the hours played still turn into coins.
It fits the current code: it reuses the D-96 completion path with an empty list of dice, so it needs no new base action (we are at 12 of 12) and no new kind of event. It uses the D-14 and D-97 "per-die" edit mechanism.
I took from opinion 3 the explicit `ConditionMet` field (it keeps replay deterministic, invariant 4), the rule that challenge dice and grantEvent are also lost, and the re-check in both directions. From opinion 2, the warning and the admin's "not applicable" mark (the most likely argument in the group). From opinion 1, the rules that the run counts as a completion and that the condition does not carry over.
I did not take full removal of countAsDrop. SPEC says "by default", which implies other options, and the owner should be able to switch the policy in the Ruleset. The value stays in the schema, and only an explicit validation error is allowed while it is not implemented.
Owner sign-off is not needed: it is one Ruleset flag that is easy to switch, it matches SPEC, and nothing here is hard to undo.

**Поля конфига.** `reward.unmetConditionPolicy = "noDiceKeepCoins" (enum noDiceKeepCoins | countAsDrop | ignore; fixed in the run snapshot at roll time; countAsDrop may be rejected by validation as ruleset.unsupported until it is implemented)`

**Заметка для заказчика.** По умолчанию, проверить на бете: невыполненное условие ивента — прохождение засчитано, кубов нет (включая челлендж и бонусы на этот ролл), монетки начисляются; перед подтверждением игрок видит предупреждение, админ при проверке пруфа может отметить «условие неприменимо». Переключается в reward.unmetConditionPolicy.
