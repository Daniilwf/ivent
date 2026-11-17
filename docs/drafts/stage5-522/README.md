# Черновики тестов 5.22

Сохранены при паузе по просьбе заказчика. Это исходники будущих тестов, а не выполненные проверки: они не включены в test discovery и ещё не прошли typecheck в целевых местах.

- `PollsScreen.test.tsx.draft` → `web/src/polls/PollsScreen.test.tsx`, 37 сценариев.
- `PollsNavigation.test.tsx.draft` → `web/src/app/PollsNavigation.test.tsx`, 9 сценариев.

Перед переносом создать типизированные заглушки и согласованные публичные props из PROGRESS. Затем зафиксировать meaningful assertion RED, не import/compile error. Оба черновика проверены парсером и Prettier; они не являются пропущенными рабочими тестами.
