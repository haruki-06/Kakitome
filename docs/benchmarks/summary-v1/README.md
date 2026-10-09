# Summary benchmark v1

Generated 2026-10-03 17:00 by `Kakitome.Bench summary` on AMD64 Family 26 Model 68 Stepping 0, AuthenticAMD (16 logical CPUs).
Cases: `benchmarks/summary-v1/cases.json` (7 synthetic transcripts: 6 Japanese incl. one long multi-chunk meeting, 1 English).
Recall = reference items found (keyword groups). Spurious = decisions/actions produced where the reference has none.
Invented # = numbers in the summary that do not occur in the transcript (kanji numerals normalized). Raw outputs: `benchmarks/runs/summary-v1/` (not committed).

| Provider | Decisions | Actions | Owner | Due | Questions | Facts | Spurious | Invented # | Cited | Language | Avg s | Max s | Peak MB | Failed |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| extractive-v1 | 38 % | 58 % | 27 % | 56 % | 60 % | 65 % | 0 | 0 | 100 % | 98 % | 0.0 | 0.0 | 40 | 0 |
| qwen3-4b-instruct-2507-q4_k_m (GPU) | 75 % | 75 % | 64 % | 100 % | 80 % | 100 % | 1 | 0 | 100 % | 97 % | 5.2 | 9.1 | 3579 | 0 |
| qwen3-4b-instruct-2507-q4_k_m (CPU) | 75 % | 83 % | 82 % | 100 % | 80 % | 100 % | 1 | 0 | 100 % | 97 % | 38.6 | 92.1 | 3965 | 0 |
| phi-4-mini-instruct-q4_k_m (GPU) | 62 % | 58 % | 55 % | 78 % | 100 % | 95 % | 1 | 0 | 100 % | 97 % | 4.6 | 6.6 | 3663 | 0 |
| phi-4-mini-instruct-q4_k_m (CPU) | 50 % | 58 % | 55 % | 67 % | 100 % | 100 % | 1 | 0 | 100 % | 97 % | 21.3 | 50.4 | 3776 | 0 |

## Per case

### weekly-meeting (ja, 12 lines)

- extractive-v1: dec 0/2 act 1/2 owner 0/2 due 1/2 q 1/1 facts 3/3 spurious 0 invented# 0 cited 8/8 lang 100% 0.0s 34MB
- qwen3-4b-instruct-2507-q4_k_m (GPU): dec 1/2 act 2/2 owner 2/2 due 2/2 q 1/1 facts 3/3 spurious 0 invented# 0 cited 9/9 lang 99% 5.4s 2674MB
- qwen3-4b-instruct-2507-q4_k_m (CPU): dec 1/2 act 2/2 owner 2/2 due 2/2 q 1/1 facts 3/3 spurious 0 invented# 0 cited 9/9 lang 99% 31.9s 3965MB
- phi-4-mini-instruct-q4_k_m (GPU): dec 1/2 act 1/2 owner 1/2 due 1/2 q 1/1 facts 3/3 spurious 0 invented# 0 cited 7/7 lang 98% 4.6s 2771MB
- phi-4-mini-instruct-q4_k_m (CPU): dec 1/2 act 1/2 owner 1/2 due 1/2 q 1/1 facts 3/3 spurious 0 invented# 0 cited 4/4 lang 98% 14.1s 3768MB

### lecture-ml (ja, 9 lines)

- extractive-v1: dec 0/0 act 1/1 owner 0/0 due 0/1 q 0/0 facts 2/4 spurious 0 invented# 0 cited 4/4 lang 97% 0.0s 40MB
- qwen3-4b-instruct-2507-q4_k_m (GPU): dec 0/0 act 1/1 owner 0/0 due 1/1 q 0/0 facts 4/4 spurious 1 invented# 0 cited 8/8 lang 98% 4.8s 2646MB
- qwen3-4b-instruct-2507-q4_k_m (CPU): dec 0/0 act 1/1 owner 0/0 due 1/1 q 0/0 facts 4/4 spurious 1 invented# 0 cited 7/7 lang 98% 31.1s 3876MB
- phi-4-mini-instruct-q4_k_m (GPU): dec 0/0 act 1/1 owner 0/0 due 1/1 q 0/0 facts 4/4 spurious 1 invented# 0 cited 6/6 lang 97% 4.6s 3663MB
- phi-4-mini-instruct-q4_k_m (CPU): dec 0/0 act 1/1 owner 0/0 due 1/1 q 0/0 facts 4/4 spurious 1 invented# 0 cited 4/4 lang 99% 21.0s 3776MB

### one-on-one (ja, 8 lines)

- extractive-v1: dec 0/1 act 0/2 owner 0/2 due 0/1 q 0/1 facts 0/2 spurious 0 invented# 0 cited 6/6 lang 99% 0.0s 40MB
- qwen3-4b-instruct-2507-q4_k_m (GPU): dec 1/1 act 1/2 owner 1/2 due 1/1 q 1/1 facts 2/2 spurious 0 invented# 0 cited 11/11 lang 99% 4.9s 2651MB
- qwen3-4b-instruct-2507-q4_k_m (CPU): dec 1/1 act 1/2 owner 1/2 due 1/1 q 1/1 facts 2/2 spurious 0 invented# 0 cited 8/8 lang 99% 36.6s 3880MB
- phi-4-mini-instruct-q4_k_m (GPU): dec 0/1 act 1/2 owner 1/2 due 1/1 q 1/1 facts 2/2 spurious 0 invented# 0 cited 4/4 lang 99% 4.2s 2741MB
- phi-4-mini-instruct-q4_k_m (CPU): dec 0/1 act 1/2 owner 1/2 due 1/1 q 1/1 facts 2/2 spurious 0 invented# 0 cited 4/4 lang 99% 18.1s 3769MB

### customer-call (ja, 9 lines)

- extractive-v1: dec 0/2 act 2/2 owner 1/2 due 1/1 q 1/1 facts 2/2 spurious 0 invented# 0 cited 7/7 lang 89% 0.0s 40MB
- qwen3-4b-instruct-2507-q4_k_m (GPU): dec 1/2 act 1/2 owner 0/2 due 1/1 q 1/1 facts 2/2 spurious 0 invented# 0 cited 7/7 lang 84% 4.1s 3579MB
- qwen3-4b-instruct-2507-q4_k_m (CPU): dec 1/2 act 2/2 owner 2/2 due 1/1 q 1/1 facts 2/2 spurious 0 invented# 0 cited 9/9 lang 83% 34.7s 3881MB
- phi-4-mini-instruct-q4_k_m (GPU): dec 1/2 act 1/2 owner 1/2 due 1/1 q 1/1 facts 2/2 spurious 0 invented# 0 cited 5/5 lang 88% 4.2s 2922MB
- phi-4-mini-instruct-q4_k_m (CPU): dec 1/2 act 1/2 owner 1/2 due 1/1 q 1/1 facts 2/2 spurious 0 invented# 0 cited 5/5 lang 85% 17.9s 3772MB

### interview (ja, 7 lines)

- extractive-v1: dec 0/0 act 0/0 owner 0/0 due 0/0 q 0/0 facts 2/3 spurious 0 invented# 0 cited 5/5 lang 100% 0.0s 40MB
- qwen3-4b-instruct-2507-q4_k_m (GPU): dec 0/0 act 0/0 owner 0/0 due 0/0 q 0/0 facts 3/3 spurious 0 invented# 0 cited 3/3 lang 100% 3.6s 2654MB
- qwen3-4b-instruct-2507-q4_k_m (CPU): dec 0/0 act 0/0 owner 0/0 due 0/0 q 0/0 facts 3/3 spurious 0 invented# 0 cited 3/3 lang 100% 19.3s 3878MB
- phi-4-mini-instruct-q4_k_m (GPU): dec 0/0 act 0/0 owner 0/0 due 0/0 q 0/0 facts 3/3 spurious 0 invented# 0 cited 1/1 lang 100% 3.6s 3433MB
- phi-4-mini-instruct-q4_k_m (CPU): dec 0/0 act 0/0 owner 0/0 due 0/0 q 0/0 facts 3/3 spurious 0 invented# 0 cited 2/2 lang 100% 15.2s 3771MB

### english-standup (en, 7 lines)

- extractive-v1: dec 1/1 act 1/3 owner 1/3 due 1/2 q 1/1 facts 0/2 spurious 0 invented# 0 cited 6/6 lang 100% 0.0s 40MB
- qwen3-4b-instruct-2507-q4_k_m (GPU): dec 1/1 act 2/3 owner 2/3 due 2/2 q 1/1 facts 2/2 spurious 0 invented# 0 cited 11/11 lang 100% 4.2s 2957MB
- qwen3-4b-instruct-2507-q4_k_m (CPU): dec 1/1 act 2/3 owner 2/3 due 2/2 q 1/1 facts 2/2 spurious 0 invented# 0 cited 11/11 lang 100% 24.7s 3879MB
- phi-4-mini-instruct-q4_k_m (GPU): dec 1/1 act 1/3 owner 1/3 due 1/2 q 1/1 facts 1/2 spurious 0 invented# 0 cited 13/13 lang 100% 4.6s 3114MB
- phi-4-mini-instruct-q4_k_m (CPU): dec 1/1 act 1/3 owner 1/3 due 1/2 q 1/1 facts 2/2 spurious 0 invented# 0 cited 4/4 lang 100% 12.0s 3773MB

### long-planning (ja, 72 lines)

- extractive-v1: dec 2/2 act 2/2 owner 1/2 due 2/2 q 0/1 facts 4/4 spurious 0 invented# 0 cited 26/26 lang 100% 0.0s 40MB
- qwen3-4b-instruct-2507-q4_k_m (GPU): dec 2/2 act 2/2 owner 2/2 due 2/2 q 0/1 facts 4/4 spurious 0 invented# 0 cited 15/15 lang 100% 9.1s 3140MB
- qwen3-4b-instruct-2507-q4_k_m (CPU): dec 2/2 act 2/2 owner 2/2 due 2/2 q 0/1 facts 4/4 spurious 0 invented# 0 cited 15/15 lang 100% 92.1s 3904MB
- phi-4-mini-instruct-q4_k_m (GPU): dec 2/2 act 2/2 owner 2/2 due 2/2 q 1/1 facts 4/4 spurious 0 invented# 0 cited 11/11 lang 100% 6.6s 3457MB
- phi-4-mini-instruct-q4_k_m (CPU): dec 1/2 act 2/2 owner 2/2 due 1/2 q 1/1 facts 4/4 spurious 0 invented# 0 cited 11/11 lang 100% 50.4s 3771MB

## 2026-10-09: sections by kind of recording (ADR-038)

Same cases, Qwen3-4B on the GPU, after the model states the kind of recording and sections that do not fit it are
dropped (3+ key points for longer passes):

| Provider | Decisions | Actions | Owner | Due | Questions | Facts | Spurious | Invented # | Cited | Language | Avg s | Max s | Peak MB | Failed |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| extractive-v1 | 38 % | 58 % | 27 % | 56 % | 60 % | 65 % | 0 | 0 | 100 % | 98 % | 0.0 | 0.0 | 42 | 0 |
| qwen3-4b-instruct-2507-q4_k_m (GPU) | 88 % | 75 % | 73 % | 89 % | 80 % | 100 % | 0 | 0 | 100 % | 97 % | 5.9 | 9.7 | 3588 | 0 |
