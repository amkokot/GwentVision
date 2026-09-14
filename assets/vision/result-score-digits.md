# Final result score font

`result-score-digits.json` contains 23 small normalized glyph masks for digits
0–9, using the existing `HudDigitReader` distance and ambiguity thresholds.
These are separate from the live scoreboard font. Source labels were visually
reviewed from the settled result tables:

- `sessions/20260901-215818/frame-008264-221205247.jpg`: 56/55, 84/125, 75/33.
- `sessions/20260902-100300/frame-005907-101251706.jpg`: 61/73, 12/39, 0/0.

Regenerate explicitly with the tests command `--calibrate-result-scores`.
`--post-match-scores-regression` excludes these two calibration images and checks
other retained frames, the unused third round, rating-panel rejection, and storage
correction. The frames are from the same two matches; broader match coverage is
still needed. No player names or screenshot pixels outside the digits are stored
in the glyph asset.
