# Матрица тестов

Каждое правило из `docs/SPEC.md` и каждая строка «Проверки на дыры» связаны с тестами. Правило без теста — незакрытая задача.

Статус: ⬜ теста нет, 🟥 тест есть и падает (ждёт реализации), 🟨 часть правила покрыта зелёными тестами (остальное — в указанной задаче плана), ✅ тест зелёный, ➖ не относится к этапу 1 (указан этап).
Пути тестов: `Engine.Tests/<Механика>/…`, `Web.Tests/…`, `web/src/…` (Vitest), `e2e/…` (Playwright). Имена — план, уточняются при написании.

## Правила этапа 1

### Прогресс и победа

| ID | Правило | Тесты | Статус |
| --- | --- | --- | --- |
| P1 | Очки и позиция — разные показатели; кубы за прохождение меняют оба на сумму | `Runs/CompletionTests.Dice_sum_adds_points_and_moves_forward`, `Runs/CompletionTests.Points_and_position_accumulate_over_runs` (зелёные); правка админа и стартовые значения (✅): `Players/AdminAdjustTests.Position_change_does_not_touch_points`, `Players/AdminAdjustTests.Points_change_does_not_touch_position`, `Players/AdminAdjustTests.Position_change_is_a_transfer_with_the_admin_reason`, `Players/AddPlayerMidSeasonTests.Starting_cell_gives_no_points_and_starting_points_give_no_cells`, `Players/AddPlayerMidSeasonTests.Player_joining_mid_season_plays_on_from_the_given_cell_and_points`, инвариант «фишка двигается только событиями `PlayerMoved`, перенос админом — путь из одной клетки» (`Invariants/PlayerAdminInvariantTests.Invariants_hold_after_every_command`) | ✅ |
| P2 | Первое место — первый дошедший до финиша | `Finish/FirstFinisherTests.First_to_reach_finish_is_first` | ⬜ |
| P3 | Первый финиш предварительный до одобрения пруфа; при реджекте откат, место следующему | `Finish/ProvisionalFinishTests.*`, e2e `05-finish` | ⬜ |
| P4 | Первый: до одобрения финиша очки как обычно, но не влияет на других; после одобрения заморожен, очки не растут | `Finish/FreeModeTests.*`, инвариант 8 | ⬜ |
| P5 | Остальные места — по очкам на момент дедлайна | `Ranking/LeaderboardTests.Others_ranked_by_points` | ⬜ |
| P6 | Финишировавшие не первыми: бонус по порядку один раз, позиция фиксирована, очки растут | `Finish/FinishBonusTests.*`, инвариант 9 | ⬜ |
| P7 | Никто не финишировал — все места по очкам | `Ranking/LeaderboardTests.No_finisher_all_by_points` | ⬜ |
| P8 | Тайбрейк: число пройденных игр, затем кто раньше набрал итоговые очки | `Ranking/TiebreakTests.*` | ⬜ |
| P9 | Засчитывается бросок до дедлайна; пруф можно догрузить; недопройденная не засчитывается | `Seasons/DeadlineTests.*`, e2e `06-deadline` | ⬜ |
| P10 | Итоги только после проверки всех пруфов | `Seasons/FinalizeTests.Cannot_finalize_with_pending_proofs` | ⬜ |
| P11 | Лидерборд: очки и клетки до финиша; первый финишировавший сверху независимо от очков | `Ranking/LeaderboardTests.*`, инвариант 10 | ⬜ |

### Игровой цикл

| ID | Правило | Тесты | Статус |
| --- | --- | --- | --- |
| T1 | Строгая машина состояний; активных прохождений не больше лимита из конфига | `Rolls/RollTests.Roll_while_rolling_is_rejected_with_wrong_phase`, `Rolls/RollTests.Roll_while_playing_is_rejected_with_wrong_phase`, `Rolls/RollTests.Roll_by_unknown_player_is_rejected`, `Runs/CompletionTests.Start_when_idle_is_rejected_with_wrong_phase`, `Runs/CompletionTests.Start_when_already_playing_is_rejected_with_wrong_phase`, `Runs/CompletionTests.Start_by_unknown_player_is_rejected`, `Runs/CompletionTests.Complete_when_idle_is_rejected_with_wrong_phase`, `Runs/CompletionTests.Complete_when_rolling_is_rejected_with_wrong_phase`, `Runs/CompletionTests.Completing_twice_is_rejected_with_wrong_phase`, `Runs/CompletionTests.Complete_by_unknown_player_is_rejected`, `Turns/TransitionMatrixTests.Every_disallowed_pair_is_rejected_without_events`, инвариант 3 (`Invariants/SliceInvariantTests.Invariants_hold_after_every_command`) | 🟨 C4 |
| T2 | Ожидание выбора хранится на сервере, закрытая вкладка ничего не ломает | `Turns/PendingChoiceTests.*`, e2e `10-two-tabs` | ⬜ |
| T3 | Первый финишировавший проходит цикл в свободном режиме без движения и влияния; без очков и монеток после одобрения финиша | `Finish/FreeModeTests.*` | ⬜ |
| T4 | Финишировавшие не первыми продолжают цикл, фаза движения пропускается | `Finish/LaterFinisherTests.No_movement_after_finish` | ⬜ |

### Пул и ролл

| ID | Правило | Тесты | Статус |
| --- | --- | --- | --- |
| G1 | Пул общий: теги, заметка, автор; игроки добавляют, админ правит и удаляет | `Web.Tests/Pool/*` | ⬜ |
| G2 | Предупреждение о похожих названиях («Dice Fold» / «Dice & Fold») | `Pool/SimilarTitleTests.*`, `Import/DuplicateReportTests.*` | ⬜ |
| G3 | Мягкое удаление: пропадает из роллов, прохождения сохраняются | `Rolls/RollTests.Deleted_game_never_rolled`, `Rolls/RollTests.Only_deleted_games_left_rejects_with_no_available_games`, `Rolls/RollTests.Wheel_spins_only_over_categories_with_available_games`, `Runs/SnapshotTests.Deleted_game_run_keeps_snapshot` | 🟨 E1 |
| G4 | Часы подтягиваются при добавлении и фиксируются в прохождении при ролле | `Rolls/RollTests.Roll_offers_available_game_and_moves_player_to_rolling`, `Rolls/RollTests.Roll_snapshot_keeps_missing_hours_as_null`, `Runs/CompletionTests.Pool_hours_change_after_roll_does_not_change_the_run`, `Web.Tests/Pool/HoursProviderTests.*` | 🟨 D6 |
| G5 | Колесо — только анимация, результат решает сервер | `Web.Tests/Rolls/RollIdempotencyTests.Refresh_does_not_change_result`, e2e `10-two-tabs` | ⬜ |
| G6 | Колесо крутится только по категориям, где под фильтрами есть доступная игра | `Rolls/RollTests.Wheel_spins_only_over_categories_with_available_games`, `Rolls/RollTests.Wheel_skips_category_whose_only_game_is_reserved_by_another_player`, `Rolls/RollTests.Wheel_follows_category_weights` | 🟨 C5 |
| G7 | Промахи («Уже прошёл», «Сейчас играет») пишутся в лог, выбор идёт из оставшихся, бесконечных рероллов нет | `Rolls/RollTests.Game_offered_to_another_player_is_a_being_played_miss`, `Rolls/RollTests.Game_being_played_by_another_player_is_a_being_played_miss`, `Rolls/RollTests.Game_completed_by_another_player_is_a_completed_in_season_miss` | ✅ |
| G8 | Статусы игры: пройдена кем-то / играет другой → промах; дропнута другим → доступна; «Уже проходил» → бесплатно и исключение; своя дропнутая или техдропнутая → не выпадает | `Rolls/RollTests.Game_completed_by_another_player_is_a_completed_in_season_miss`, `Rolls/RollTests.Game_completed_in_season_is_not_offered_again_even_to_its_player`, `Rolls/AvailabilityTests.*` (дроп, тех-реролл, «Уже проходил»), инварианты 5 (`Invariants/SliceInvariantTests.Invariants_hold_after_every_command`), 6 | 🟨 C6 |
| G9 | Двое одну игру одновременно не играют (включая зарезервированную при ролле) | `Rolls/RollTests.Game_offered_to_another_player_is_a_being_played_miss`, `Rolls/RollTests.Game_being_played_by_another_player_is_a_being_played_miss`, `Rolls/RollTests.Only_game_reserved_by_another_player_rejects_with_no_available_games`, инвариант 4 (`Invariants/SliceInvariantTests.Invariants_hold_after_every_command`) | ✅ |
| G10 | Пустой пул: сигнал админу (снятие фильтра зоны — этап 2) | `Rolls/RollTests.Empty_pool_rejects_with_no_available_games`, `Rolls/RollTests.Only_deleted_games_left_rejects_with_no_available_games`, `Rolls/EmptyPoolTests.*` (сигнал админу) | 🟨 C5 |
| G11 | У каждой категории виден счётчик доступных игр | `Rolls/CategoryStatsTests.*` | ⬜ |
| G12 | Фильтры — предикат по играм с приоритетом «эффект > зона > обычный», пустое пересечение → более приоритетный | `Rolls/RollFilterPriorityTests.*` (тестовые фильтры) | ⬜ |

### Реролл, дроп, тех-реролл

| ID | Правило | Тесты | Статус |
| --- | --- | --- | --- |
| RR1 | Первый реролл после каждого ролла бесплатный, дальше по `rerollCost`; ресурс `freeRerolls` | `Rolls/RerollTests.*` | ⬜ |
| RR2 | Дроп: кубы за дроп отнимают очки и позицию, обязательный плохой ивент (ручной эффект), монеток нет | `Runs/DropTests.*`, e2e `02-drop` | ⬜ |
| RR3 | Штраф не откидывает дальше старта (чекпоинт — этап 2) | движение: `Map/MovementTests.Drop_penalty_does_not_go_past_start`, `Map/MovementTests.Back_clamped_at_start`; сам дроп (C6): `Runs/DropTests.Position_clamped_at_start`, инвариант 7 | 🟨 C6 |
| RR4 | «Дроп не раньше часа» — только подсказка в интерфейсе | `web/…/DropDialog.test.tsx` | ⬜ |
| RR5 | Тех-реролл: бесплатный, причина обязательна, окно из конфига, позже — только админ | `Runs/TechRerollTests.*`, e2e `03-tech-reroll` | ⬜ |
| RR6 | Админ превращает тех-реролл в дроп со штрафом | `Runs/TechRerollTests.Admin_converts_to_drop` | ⬜ |

### Награда за прохождение

| ID | Правило | Тесты | Статус |
| --- | --- | --- | --- |
| W1 | Число кубов по часам: `hoursPerDie`, округление, `min`/`max` | `Runs/CompletionTests.Dice_count_is_hours_per_die_rounded_nearest_within_limits`, `Runs/CompletionTests.Dice_count_uses_configured_rounding`, `Runs/CompletionTests.Dice_count_respects_configured_min_and_max`, инвариант «кубики = часы и снапшот» (`Invariants/SliceInvariantTests.Invariants_hold_after_every_command`) | ✅ |
| W2 | Тип кубика по сложности; «выше сложной» → d6 и ручной хороший ивент | `Runs/CompletionTests.Die_sides_follow_difficulty`, `Runs/CompletionTests.Die_value_comes_from_the_random_source_in_die_range`, `Runs/DieByDifficultyTests.*` (хороший ивент за «выше сложной») | 🟨 C7 |
| W3 | Челлендж даёт `challengeBonus.extraDice` | `Runs/ChallengeTests.*` | ⬜ |
| W4 | Пруф не блокирует: кубы кидаются сразу, одобрение позже | `Runs/CompletionTests.Dice_sum_adds_points_and_moves_forward`, `Runs/CompletionTests.Dice_are_rolled_before_the_token_moves`, `Runs/CompletionTests.Dice_thrown_without_proof` | 🟨 C8 |
| W5 | Реджект снимает очки и клетки этого прохождения, остальные последствия остаются | `Proofs/RejectTests.*`, e2e `04-reject` | ⬜ |
| W6 | Без часов нельзя кинуть кубы; оценка игрока со ссылкой, админ правит | `Runs/CompletionTests.Completion_without_hours_is_rejected_with_hours_required`, `Runs/CompletionTests.Completion_without_hours_uses_player_estimate`, `Runs/CompletionTests.Completion_with_non_positive_estimate_is_rejected_with_invalid_hours`, `Runs/CompletionTests.Rejected_completion_can_be_retried_with_an_estimate`, `Runs/HoursRequiredTests.*` (ссылка на источник, правка админом) | 🟨 C7 |
| W7 | Кубики хранятся по отдельности; правка часов докидывает недостающие, лишние снимает с конца | `Runs/CompletionTests.Dice_sum_adds_points_and_moves_forward` (кубики по отдельности), `Runs/HoursEditTests.*` | 🟨 C7 |
| W8 | Сложность засчитывается по пруфу; понижение → более низкая; каждый кубик ⌈старое × новые грани / старые грани⌉, в событии оба значения (Q-5) | `Proofs/DifficultyOverrideTests.*` | ⬜ |
| W9 | Отзыв: оценка 1–10 и текст, виден в ленте, профиле, на странице игры | `Runs/ReviewTests.*`, `Web.Tests/Reviews/*` | ⬜ |
| W10 | Монетки за прохождение по длине игры (Q-2) | `Runs/CoinsRewardTests.*` | ⬜ |

### Снапшот и конфиг

| ID | Правило | Тесты | Статус |
| --- | --- | --- | --- |
| S1 | Всё, что касается прохождения, фиксируется при ролле; остальное — текущая версия конфига | `Rolls/RollTests.Roll_offers_available_game_and_moves_player_to_rolling`, `Runs/CompletionTests.Start_creates_playing_run_with_roll_snapshot`, `Runs/CompletionTests.Ruleset_change_after_roll_does_not_change_dice_count`, `Runs/CompletionTests.Ruleset_change_while_playing_does_not_change_dice_count_or_sides`, `Runs/CompletionTests.Ruleset_change_before_roll_applies_to_that_run`, `Runs/CompletionTests.Pool_hours_change_after_roll_does_not_change_the_run` | 🟨 C7 |
| S2 | Правка конфига посреди сезона не меняет идущие прохождения | `Runs/CompletionTests.Ruleset_change_after_roll_does_not_change_dice_count`, `Runs/CompletionTests.Ruleset_change_while_playing_does_not_change_dice_count_or_sides`, `Rulesets/MidSeasonChangeTests.Roll_snapshot_records_the_ruleset_version_of_the_season`, `Change_after_roll_keeps_the_rolled_version_in_the_offer_and_the_run`, `Change_after_roll_does_not_change_the_dice_of_that_game`, `Roll_after_the_change_takes_the_new_version`, `Roll_after_several_changes_takes_the_latest_version`, `Rejected_change_does_not_affect_the_next_roll`; `Invariants/RulesetInvariantTests.Snapshots_hold_the_rules_of_their_version_after_every_command` | 🟨 C6/C7 |
| C1 | JSON-схема генерируется из типов; `ruleset.default.json` её проходит | `Rulesets/SchemaTests.Schema_generation_is_deterministic`, `Schema_is_up_to_date` (при `UPDATE_RULESET_SCHEMA=1` перезаписывает `docs/ruleset.schema.json`), `Default_passes_schema`, `Pinned_test_ruleset_passes_schema`, `Optional_field_may_be_left_out`, `Ruleset_with_a_typo_or_unknown_value_fails_schema` (12 случаев), `Generated_schema_rejects_the_same_typos` (12 случаев) | ✅ |
| C2 | Невалидный конфиг не сохраняется | Разбор: `Rulesets/ParseTests.Default_json_parses`, `Optional_field_left_out_reads_as_null`, `Broken_ruleset_is_refused_naming_the_field` (14 случаев: неизвестные поля, опечатка, нет обязательного, неверное перечисление, числа и флаги строками, null), `Json_null_is_refused`. Проверка: `Rulesets/ValidationTests.Default_ruleset_is_valid`, `Pinned_test_ruleset_is_valid`, `Ruleset_on_the_edge_is_valid` (19 граничных), `Invalid_ruleset_reports_the_field` (31 случай), `All_errors_are_reported_at_once`, `Valid_fields_next_to_an_invalid_one_are_not_reported`, `Mechanic_not_implemented_in_this_build_is_an_error_on_its_field` (14), `Linear_map_with_every_flag_off_is_playable`. Движок: `Rulesets/VersionHistoryTests.Season_with_an_invalid_ruleset_is_not_created`, `Rejection_of_an_invalid_ruleset_names_the_field`, `Invalid_change_is_rejected_and_the_version_stays`. API:, `Rulesets/ValidationEdgeTests.*`, `Web.Tests/Api/RulesApiTests.*` | ✅ |
| C3 | Каждое изменение — новая версия с датой и автором; история «было/стало» видна игрокам | Версии: `Rulesets/VersionHistoryTests.Season_is_created_with_ruleset_version_1_and_its_rules_in_the_state`, `Change_is_a_new_version_with_the_whole_ruleset`, `Each_change_bumps_the_version_by_one`, `Going_back_to_an_earlier_ruleset_is_still_a_new_version`, `Same_ruleset_is_rejected_as_unchanged`, `Equal_ruleset_read_again_from_json_is_rejected_as_unchanged`, `Change_before_the_season_exists_is_rejected`, `Changing_the_map_length_does_not_rebuild_the_map`, `Version_history_is_the_ruleset_events_of_the_log`. «Было/стало»: `Rulesets/DiffTests.Equal_rulesets_have_no_changes`, `Changed_number_is_one_leaf_change`, `Changed_enum_and_flag_are_json_text`, `Changed_array_element_has_an_index_in_the_path`, `Longer_array_shows_the_added_elements_as_new`, `Shorter_array_shows_the_removed_elements_as_gone`, `Array_of_objects_is_compared_down_to_the_leaves`, `Replaced_nested_object_unfolds_to_its_leaves`, `Nullable_value_becoming_null_is_a_change_to_json_null`, `Changes_come_sorted_by_path`, `Diff_is_symmetric_in_before_and_after`. , `Web.Tests/Api/RulesApiTests.*` (история с датой и автором) | 🟨 H7 |
| C4 | Новые поля имеют дефолты, старые версии читаются | `Rulesets/BackwardCompatTests.Every_field_missing_from_the_reference_json_is_optional` (эталон релиза, `docs/ruleset.default.json`, тестовый конфиг), `Guard_notices_a_required_field_missing_from_the_json`, `Season_created_event_with_the_release_ruleset_reads_back`, `Ruleset_without_an_optional_field_reads_inside_an_event` — стражи D-50, зелёные; эталон релиза пока равен конфигу по умолчанию и замораживается при первом релизе | ✅ |
| CT1 | Все примеры из CONTENT.md загружаются в модель контента и проходят её схему (поведение — этапы 4–6) | `Content/ContentExamplesTests.Every_example_loads` | ⬜ |

### Карта (линейная)

| ID | Правило | Тесты | Статус |
| --- | --- | --- | --- |
| M1 | Карта — граф; линейная карта — цепочка из `map.linearLength` | `Map/LinearMovementTests.Linear_map_is_a_chain_from_start_through_numbered_cells_to_finish`, `Map/LinearMovementTests.Linear_map_of_length_one_goes_straight_from_start_to_finish`, `Map/LinearMovementTests.Season_map_length_comes_from_the_ruleset`, `Map/LinearMovementTests.Default_season_map_has_linear_length_steps`, `Map/LinearMovementTests.Forward_enters_cells_along_the_chain`, `Map/LinearMovementTests.Forward_zero_steps_goes_nowhere` | ✅ |
| M2 | Назад дальше старта нельзя | `Map/MovementTests.Back_clamped_at_start`, `Map/MovementTests.Back_clamped_at_start_without_history`, `Map/MovementTests.Back_exactly_to_start_ends_on_start`, `Map/MovementTests.Back_from_start_goes_nowhere`, `Map/MovementTests.Back_from_start_after_walking_back_to_it_goes_nowhere`, `Map/MovementTests.Back_zero_steps_goes_nowhere`, `Map/VisitTests.Moving_back_clamped_at_start_stops_on_the_start`, `Map/VisitTests.A_move_that_entered_no_cell_stops_nowhere` | ✅ |
| M3 | Лишние шаги после финиша сгорают | `Map/LinearMovementTests.Forward_extra_steps_after_finish_burn`, `Map/LinearMovementTests.Forward_exactly_to_finish_ends_on_finish`, `Map/LinearMovementTests.Forward_from_finish_goes_nowhere`, `Map/LinearMovementTests.Completion_overshooting_finish_stops_on_finish_but_keeps_all_points`, `Map/LinearMovementTests.Completion_landing_exactly_on_finish`, `Map/LinearMovementTests.Completion_one_step_short_of_finish` | ✅ |
| M4 | Путь хранится отрезками, назад — по пройденным рёбрам | `Map/PathTests.*` (отрезки, перенос, откат по истории и после неё, JSON, путь в сезоне и при повторе лога); `Map/MovementTests.Back_retraces_the_walked_cells_of_the_last_segment`, `Map/MovementTests.Back_through_a_merge_follows_the_walked_branch_not_the_primary_one`, `Map/MovementTests.Back_to_the_first_cell_of_the_segment_uses_only_history`, `Map/MovementTests.Back_without_history_follows_the_primary_backward_edge_at_a_merge`, `Map/MovementTests.Back_without_history_follows_the_only_incoming_edge`, `Map/MovementTests.Back_past_the_walked_history_continues_along_primary_edges`, `Map/MovementTests.Back_does_not_cross_a_segment_boundary`, `Map/MovementTests.Back_after_a_transfer_ignores_the_walked_branch_before_it`, `Map/MovementTests.Back_from_the_finish_retraces_the_last_steps`; точки срабатывания (D-90): `Map/VisitTests.*`, `Invariants/PlayerAdminInvariantTests.Every_move_fires_its_entered_cells_and_a_transfer_fires_none`; инвариант M4 «путь кончается на позиции, внутри отрезка — рёбра карты, отрезков = 1 + переносов» (`Invariants/PlayerAdminInvariantTests.Invariants_hold_after_every_command`) | ✅ |

### Сезон

| ID | Правило | Тесты | Статус |
| --- | --- | --- | --- |
| SE1 | Статусы: черновик → идёт → закрытие → завершён → архив; недопустимые переходы отклоняются | `Seasons/SeasonStatusTests.Season_is_created_as_a_draft_with_its_name_and_deadline`, `Season_created_without_a_deadline_has_none`, `Season_goes_through_every_status_in_order`, `Starting_a_draft_season_changes_nothing_but_the_status`, `Status_can_only_move_to_the_next_one` (все недопустимые пары), `Undefined_status_value_is_an_invalid_transition`, `Game_actions_are_rejected_in_a_draft_season`, `Game_actions_are_rejected_once_the_season_is_no_longer_active` (закрытие, завершён, архив), `Game_actions_work_once_the_draft_season_is_started`, `Deadline_can_be_set_by_the_admin`, `Deadline_set_at_creation_can_be_moved`, `Deadline_can_be_removed`, `Deadline_cannot_be_changed_once_the_season_is_finished`, `Season_commands_before_the_season_exists_are_rejected`; лог: `Seasons/SeasonAdministrationLogTests.*`; инвариант «статусы только по порядку, игровые действия только в «идёт»» (`Invariants/PlayerAdminInvariantTests.Invariants_hold_after_every_command`) | ✅ |
| SE2 | В «закрытии» роллы и броски запрещены, пруфы принимаются | `Seasons/DeadlineTests.*`, инвариант 11 | ⬜ |
| SE3 | При завершении сохраняется снимок итогов | `Seasons/FinalizeTests.Snapshot_saved` | ⬜ |
| SE4 | Новый игрок посреди сезона: стартовую позицию, очки и монетки задаёт админ | `Players/AddPlayerMidSeasonTests.Player_added_by_default_starts_on_start_with_zero_balance` (черновик и «идёт»), `Player_joining_mid_season_gets_the_cell_points_and_coins_the_admin_sets`, `Starting_values_are_the_sum_of_logged_changes`, `Explicit_start_cell_and_zero_balance_write_no_changes`, `Only_nonzero_starting_values_are_logged`, `Starting_cell_gives_no_points_and_starting_points_give_no_cells`, `Player_joining_mid_season_plays_on_from_the_given_cell_and_points`, `Unknown_starting_cell_is_rejected`, `Player_cannot_join_once_the_season_is_closing`, `Same_user_cannot_join_twice_mid_season_even_with_a_starting_balance`; `Seasons/SeasonSetupTests.Adding_the_same_player_twice_is_rejected`, `Adding_the_same_user_twice_under_another_player_id_is_rejected`; инвариант 2 (`Invariants/PlayerAdminInvariantTests.Invariants_hold_after_every_command`) | ✅ |
| SE5 | Флаг неактивности ставит админ; подсказка по игрокам без действий `inactiveHintDays` с отметкой об активной игре | Флаг: `Players/InactivityFlagTests.Admin_marks_a_player_inactive`, `Admin_brings_an_inactive_player_back`, `Marking_an_inactive_player_inactive_again_is_rejected`, `Bringing_back_an_active_player_is_rejected`, `Unknown_player_is_rejected`, `Flag_changes_nothing_else_about_the_player`, `Player_can_be_marked_inactive_in_a_draft_season`, `Flag_survives_replay`, инвариант «флаг = последнее значение от админа, без пустых событий» (`Invariants/PlayerAdminInvariantTests.Invariants_hold_after_every_command`). Подсказка — чтение в админке: `Web.Tests/Admin/InactiveHintTests.*` (E2) | 🟨 E2 |
| SE6 | Пруф прохождения, которое довело до финиша, стоит в начале очереди | `Proofs/ProofQueueTests.Finishes_on_top` | ⬜ |
| SE7 | Освобождение первого места: первым становится следующий, его бонус снимается, заморозка с момента, когда стал первым (при подтверждённом финише) или при одобрении; бонусы всех финишировавших пересчитываются (Q-4) | `Finish/FirstPlaceReassignTests.*` | ⬜ |

### Эффекты (часть этапа 1)

| ID | Правило | Тесты | Статус |
| --- | --- | --- | --- |
| E1 | Текстовые эффекты → ручное разрешение: «применено» или «не применимо» с комментарием | `Effects/ManualEffectTests.*` | ⬜ |
| E2 | Лимит цепочки: 3 уровня и 50 событий на команду, обрыв с записью в лог | `Effects/ChainLimitTests.*` (тестовые эффекты), инвариант 12 | ⬜ |
| E3 | Выключенная флагом механика отклоняется движком и не порождает событий | Включить нереализованную механику нельзя: `Rulesets/ValidationTests.Mechanic_not_implemented_in_this_build_is_an_error_on_its_field` (11 флагов, `mapMode: graph`, `choiceCount ≠ 1`, `maxActiveRunsPerPlayer ≠ 1`), `Seasons/SeasonSetupTests.Season_with_a_mechanic_not_implemented_yet_is_rejected`, `Ruleset_cannot_be_changed_mid_season_to_a_mechanic_not_implemented_yet`. Механизм «команда требует флаг» (`Features/FeatureFlagTests.*`, инвариант 19) — с первой механикой за флагом | 🟨 первая механика за флагом |

### Лог, откат, целостность

| ID | Правило | Тесты | Статус |
| --- | --- | --- | --- |
| L1 | Лог всех действий, включая админа, виден всем игрокам | `Web.Tests/Feed/*`, e2e `07-undo` | ⬜ |
| L2 | События хранят результаты; повтор лога детерминирован | `Seasons/SliceScenarioTests.Replaying_the_log_gives_the_current_state`, `Seasons/SliceScenarioTests.Replaying_the_log_read_back_from_storage_gives_the_same_state`, `Seasons/SliceScenarioTests.Same_seed_and_commands_give_the_same_log`, `Invariants/SliceInvariantTests.Same_seed_and_commands_give_the_same_log`, снапшот-тесты Verify | 🟨 C13 |
| L3 | Откат — команда целиком компенсирующими событиями в обратном порядке; есть зависимые — отказ со списком | `Undo/UndoTests.*`, инвариант 13, e2e `07-undo` | ⬜ |
| L4 | Проверка целостности: пересчёт из лога совпадает с сохранённым | `Seasons/SliceScenarioTests.Replaying_every_prefix_of_the_log_matches_the_state_after_each_command`, инвариант 1 (`Invariants/SliceInvariantTests.Invariants_hold_after_every_command`), `Log/IntegrityTests.*`, `Web.Tests/Integrity/*`, `Web.Tests/Queue/CommandQueueTests.Command_through_the_queue_writes_log_and_projection_that_agree` | 🟨 C12 |
| L5 | Экспорт и импорт сезона одним архивом | `Web.Tests/SeasonTransfer/*` | ⬜ |
| L6 | Формат события версионируется, старые события читаются через преобразование | `Kernel/EventFormatTests.*` (эталонный формат, пошаговое преобразование старых версий, отказ для новее сборки и без преобразования), `Seasons/SliceScenarioTests.Every_event_of_the_slice_survives_the_json_round_trip` | ✅ |
| L7 | Все изменения через одну очередь, одна команда — одна транзакция, повтор `CommandId` не выполняется дважды | `Web.Tests/Queue/CommandQueueTests.*`, `Web.Tests/Queue/CommandQueueRobustnessTests.*` (атомарность при сбое, 50 параллельных команд, повтор `CommandId` засчитывается только той же команде того же автора и сезона, перезапуск, остановка) | ✅ |

### Аккаунты, безопасность, эксплуатация

| ID | Правило | Тесты | Статус |
| --- | --- | --- | --- |
| A1 | Аккаунты создаёт админ, временный пароль, игрок меняет пароль сам | `Web.Tests/Auth/*` | ⬜ |
| A2 | Роли: игрок, админ, зритель; зритель не делает ни одного игрового действия | `Web.Tests/Api/SeasonApiTests.Spectator_admin_and_player_outside_the_season_are_forbidden`, `Web.Tests/Api/SeasonApiTests.Spectator_and_outsider_can_view_the_season`, e2e `11-spectator` | 🟨 I1 |
| A3 | Ограничение попыток входа (по IP и по логину), куки HttpOnly/Secure/SameSite, CSRF, сессия кончается при удалении аккаунта или смене роли | `Web.Tests/Api/AccountApiTests.*` (кука HttpOnly/SameSite, CSRF, неверные данные, удалённый аккаунт), `Web.Tests/Api/LoginRateLimitTests.*`, `Web.Tests/Api/SeasonApiTests.Post_without_the_antiforgery_token_is_refused`, `Web.Tests/Api/WebSecurityTests.*`, `Web.Tests/Api/ProductionSurfaceTests.*` | 🟨 D8 |
| A4 | Игрок действует только за себя | `Web.Tests/Api/SeasonApiTests.A_player_cannot_act_for_another_player`, `…Spectator_admin_and_player_outside_the_season_are_forbidden`, e2e `08-permissions` | 🟨 I1 |
| A5 | Аватарка по ссылке: только https, публичные адреса, белый список, лимит | `Web.Tests/Files/AvatarUrlTests.*` | ⬜ |
| A6 | Загрузка файлов: тип по содержимому, лимиты, WebP, миниатюры GIF | `Web.Tests/Files/UploadTests.*` | ⬜ |
| A7 | Тестовые эндпоинты есть только в Development и Test | `Web.Tests/Environment/ProductionHasNoTestEndpointsTests` | ⬜ |
| A8 | Режим обслуживания: баннер и только чтение | `Web.Tests/Maintenance/*` | ⬜ |
| A9 | Кнопка «Сообщить о баге» с контекстом и скриншотом, выгрузка файлом | `Web.Tests/BugReports/*`, `web/…/BugReportButton.test.tsx` | ⬜ |
| A10 | `/health` и страница «Ошибки» | `Web.Tests/Observability/*` | ⬜ |
| A13 | Пользователь участвует в сезоне не больше одного раза (D-65) | `Seasons/SeasonSetupTests.Adding_the_same_user_twice_under_another_player_id_is_rejected` | ✅ |
| A14 | Обновления в реальном времени: зритель сезона узнаёт о действии другого игрока без перезагрузки; анонимный не подключается | `Web.Tests/Api/SeasonHubTests.*` (догрузка пропущенного — E3) | 🟨 E3 |
| A15 | Экран среза: ролл → старт → завершение с кубами, фишка сдвигается, второй браузер видит сдвиг без перезагрузки; отказы по-русски; устаревший ответ не затирает свежий; сеть упала — сообщение, а не зависание | `e2e/tests/slice.spec.ts` (десктоп и телефон), `web/src/season/SeasonScreen.test.tsx`, `web/src/season/CompleteForm.test.tsx`, `web/src/App.test.tsx` | 🟨 H2–H4 |
| A16 | Сезон по умолчанию: последний, где пользователь играет; для остальных — последний; без сезонов — 404 | `Web.Tests/Api/SeasonApiTests.Current_season_*`, `Web.Tests/Api/EmptySiteTests.*` | ✅ |
| A17 | Сайт раздаёт собранный фронтенд: файлы как есть, клиентские маршруты — SPA, неизвестные `/api` и `/hubs` — 404 | `Web.Tests/Api/FrontendHostingTests.*` | ✅ |
| A11 | Время хранится в UTC, дедлайны показываются по Москве с подписью | `web/…/formatDeadline.test.ts` | ⬜ |
| A12 | Внешние сервисы (HLTB, Steam, IGDB) недоступны — сайт работает | `Web.Tests/Providers/*` | ⬜ |

## Проверка на дыры

| Дыра | Как закрыта | Тесты | Статус |
| --- | --- | --- | --- |
| Выгодно дропать длинные игры | почти линейные кубы, штраф по очкам | W1, RR2; баланс — симулятор | ⬜ / ➖ этап 3 |
| Тех-реролл вместо дропа | окно, причина, админ делает дропом | RR5, RR6 | ⬜ |
| Крутить колесо, обновляя страницу | результат решает сервер | G5 | ⬜ |
| Копить лоты магазина | лоты живут N минут | — | ➖ этап 4 |
| Подкармливать друга | прямых переводов нет | нет команд перевода (архитектурный тест списка команд) | ⬜ |
| Финишировавший первым помогает или вредит | заморожен | P4, T3, инвариант 8 | ⬜ |
| Реджект прохождения, которое довело до финиша | место предварительное | P3, SE7 | ⬜ |
| Два действия одновременно | единая очередь | L7, e2e `10-two-tabs` | 🟨 I1 |
| Двое финишировали почти одновременно | порядок по очереди команд | `Finish/ConcurrentFinishTests` (Web.Tests) | ⬜ |
| Реджект после объявления итогов | итоги после проверки всех пруфов | P10 | ⬜ |
| Правка конфига посреди сезона | снапшот при ролле | S1, S2 | 🟨 C6/C7 |
| Удалили клетку с игроком | публикация блокируется | — | ➖ этап 2 |
| Цикл из телепортов | проверка редактора | — | ➖ этап 2 |
| Откат назад через слияние веток | история пути | M4: `Map/MovementTests.Back_through_a_merge_follows_the_walked_branch_not_the_primary_one`, `Map/MovementTests.Back_without_history_follows_the_primary_backward_edge_at_a_merge` (граф вручную; редактор — этап 2) | ✅ движок / ➖ редактор — этап 2 |
| Чужой толчок через развилку | ветка по умолчанию | — | ➖ этап 2 |
| Толчок на магазин офлайн | купон | — | ➖ этап 4 |
| Зона с пустым пулом | предупреждение и приоритет фильтров | G10, G12 | ⬜ / ➖ этап 2 |
| Двое играют одну игру | игра занята | G9, инвариант 4 | 🟨 C13 |
| Удалили игру во время прохождения | мягкое удаление, снапшот | G3, `Runs/CompletionEdgeTests.Deleting_the_game_mid_run_keeps_the_run_and_its_snapshot` | ✅ |
| Дубли игр в пуле | предупреждение о похожих | G2 | ⬜ |
| Неактивный как случайная цель | ручной флаг | SE5 (флаг, `Players/InactivityFlagTests.*`); цели — этап 4 | 🟨 флаг — C2; цели — этап 4 |
| Очки фармятся предметами | очки только за кубы и явные эффекты | P1 | ⬜ |
| Бесплатный дроп в конце сезона | дроп отнимает очки (и в минус) | RR2, `Runs/DropTests.Points_can_go_negative` | ⬜ |
| Сталкивают финишировавшего ради второго бонуса | позиция фиксирована, бонус один раз | P6, инвариант 9 | ⬜ |
| Игра не допройдена к дедлайну | не засчитывается | P9 | ⬜ |
| Уход в минус по монеткам | в минусе нельзя покупать | — | ➖ этап 4 |
| Бесконечная цепочка эффектов | лимиты | E2 | ⬜ |
| Правка часов после броска | кубики по отдельности | W7 | ⬜ |
| Потеря данных | ежедневный внешний бэкап | `backup:verify`, RUNBOOK | ⬜ |
| Ошибка админа | откат и открытый лог | L1, L3 | ⬜ |
| Ставка на себя, сговор | — | — | ➖ этап 4 |
| Выигрыш ставки по отклонённому пруфу | — | — | ➖ этап 4 |
| Накрутка голосов | — | — | ➖ этап 5 |
| Спам в галерее и комментариях | — | — | ➖ этап 6 |
| Обновление посреди сезона ломает данные | бэкап, миграции на копии, флаги | J5, E3 | ⬜ |

## Инварианты случайных партий

| # | Инвариант | Этап 1 | Статус |
| --- | --- | --- | --- |
| 1 | Пересчёт из лога = сохранённое состояние | да; срез B1: `Invariants/SliceInvariantTests.Invariants_hold_after_every_command` | 🟨 C13 |
| 2 | Очки и монетки = сумма изменений в неотменённых событиях | да; срез B1: `Invariants/SliceInvariantTests.Invariants_hold_after_every_command`; с правками админа, стартовыми значениями и прочими ресурсами (без нулевых записей в словаре): `Invariants/PlayerAdminInvariantTests.Invariants_hold_after_every_command` (✅ C2) | 🟨 C13 |
| 3 | Активных прохождений не больше лимита | да; срез B1: `Invariants/SliceInvariantTests.Invariants_hold_after_every_command`; со сбросом предложенной игры админом: `Invariants/PlayerAdminInvariantTests.Invariants_hold_after_every_command` (✅ C2) | 🟨 C13 |
| 4 | Одну игру одновременно играет не больше одного прохождения | да (кооп — этап 5); срез B1: `Invariants/SliceInvariantTests.Invariants_hold_after_every_command` | 🟨 C13 |
| 5 | Пройденная в сезоне игра никому больше не выпадает | да; срез B1: `Invariants/SliceInvariantTests.Invariants_hold_after_every_command` | 🟨 C13 |
| 6 | Своя дропнутая или техдропнутая игра не выпадает | да | ⬜ |
| 7 | Позиция на существующей клетке, не позади старта (чекпоинт — этап 2) | да; срез B1: `Invariants/SliceInvariantTests.Invariants_hold_after_every_command` | 🟨 C13 |
| 8 | После финиша первым позиция не меняется, чужие эффекты не применяются; после одобрения финиша не меняются и очки | да (чужих эффектов на этапе 1 нет) | ⬜ |
| 9 | Бонус финиша не больше одного раза, позиция финишировавшего не меняется | да | ⬜ |
| 10 | Порядок лидерборда соответствует правилам мест и тайбрейков (эталонная реализация в тестах) | да | ⬜ |
| 11 | После дедлайна нет роллов и бросков игрока (K-7) | да | ⬜ |
| 12 | Цепочка эффектов не превышает лимитов | да, на тестовых эффектах | ⬜ |
| 13 | Откат команды без зависимых возвращает ровно прежнее состояние | да | ⬜ |
| 14 | Одинаковое зерно и команды дают одинаковый лог | да; срез B1: `Invariants/SliceInvariantTests.Same_seed_and_commands_give_the_same_log` | 🟨 C13 |
| 15 | В минусе нет покупок | ➖ этап 4 | ➖ |
| 16 | Эффекты от других не меняют начатый шаг | ➖ этап 4 | ➖ |
| 17 | Залоги = открытые ставки, реджект отменяет выигрыш | ➖ этап 4 | ➖ |
| 18 | Не больше одного голоса от пользователя | ➖ этап 5 | ➖ |
| 19 | Выключенная механика не порождает событий | да | ⬜ |
| 20 | Достижение не больше одного раза в своей области | ➖ этап 6 | ➖ |

Дополнительно на этапе 1: у каждого отклонённого движком действия — ноль событий; число кубиков у завершённого прохождения соответствует его текущим часам и снапшоту. Срез B1: оба проверяются в `Invariants/SliceInvariantTests.Invariants_hold_after_every_command` (там же: удалённая игра не выпадает, фишка стоит на min(сумма кубов, длина карты), очки = сумма кубов); в сценарных тестах каждый отказ проверяется через `ScenarioAssert.RejectsWithoutChanges` (ноль событий, состояние и лог не изменились). Правки правил посреди сезона (S1, S2, C3): `Invariants/RulesetInvariantTests.Snapshots_hold_the_rules_of_their_version_after_every_command` — версия в состоянии равна 1 + число `ruleset-changed`, соседние версии различаются, снапшот каждого предложения и прохождения совпадает с правилами своей версии и не меняется после создания, грани кубиков завершённого прохождения — из снапшота.

## E2E-сценарии

| # | Сценарий | Файл | Статус |
| --- | --- | --- | --- |
| 1 | Полный цикл: сезон, три игрока, ролл, пруф, кубы, движение, другой видит без перезагрузки, одобрение | `e2e/01-full-cycle.spec.ts` | ⬜ |
| 2 | Дроп: штраф, ручной плохой ивент | `e2e/02-drop.spec.ts` | ⬜ |
| 3 | Тех-реролл в окне и после (перемотка 49 ч) | `e2e/03-tech-reroll.spec.ts` | ⬜ |
| 4 | Реджект снимает очки и клетки | `e2e/04-reject.spec.ts` | ⬜ |
| 5 | Финиш: предварительный, одобрение и заморозка, второй с бонусом, реджект первого и пересчёт | `e2e/05-finish.spec.ts` | ⬜ |
| 6 | Дедлайн: броски запрещены, пруфы принимаются, итоги после проверки | `e2e/06-deadline.spec.ts` | ⬜ |
| 7 | Откат действия админом | `e2e/07-undo.spec.ts` | ⬜ |
| 8 | Права: игрок не открывает админку и не действует за другого | `e2e/08-permissions.spec.ts` | ⬜ |
| 9 | Телефон 390×844: главная, прохождение, карта с зумом | `e2e/09-mobile.spec.ts` | ⬜ |
| 10 | Две вкладки и параллельные клики | `e2e/10-two-tabs.spec.ts` | ⬜ |
| 11 | Зритель видит всё и не может действовать | `e2e/11-spectator.spec.ts` | ⬜ |
| 12 | Правила показывают числа конфига, правка → новые числа и история | `e2e/12-rules.spec.ts` | ⬜ |
| 13 | Скорость главной на демо-сезоне с мобильной сетью < 2,5 с | `e2e/13-performance.spec.ts` | ⬜ |
