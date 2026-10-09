# ASR glossary / prompt / decoding benchmark v1 (ADR-030)

- Input: one real Japanese university lecture (1 h 27 min, single microphone in a classroom),
  imported by the user. The audio and its transcripts stay on the user's PC and are not part of the repository; only
  aggregate counts are reported here.
- Engine: whisper.cpp (Whisper.net 1.9.1, Vulkan) / whisper-large-v3-turbo-q5_0 on an RTX 5070 Ti, through the product
  path (Media Foundation reader → 16 kHz → VAD chunks), `Kakitome.Bench lecture`.
- Glossary: 67 terms of the subject (case names, doctrines, statutes) written the way an AI assistant would list them
  for the course topics. It contains **no corrections** ("wrong -> right"), so the numbers show the effect of hints
  alone; corrections are deterministic and covered by unit tests.
- Term accuracy: 35 domain terms whose misrecognitions occurred in the baseline; right = occurrences of the correct
  form, wrong = occurrences of the observed wrong forms.

| Variant | RTF | Terms right | Terms wrong | Term accuracy | 「。」/1k chars | 「、」/1k chars | Loops |
|---|---:|---:|---:|---:|---:|---:|---:|
| A: previous text as prompt (1.2.1) | 0.036 | 70 | 47 | 60 % | 1.7 | 3.3 | 1 |
| B: A + beam search 5 | 0.050 | 66 | 47 | 58 % | 0.0 | 0.3 | 1 |
| C: glossary hints, rotation | 0.038 | 79 | 34 | 70 % | 14.9 | 58.9 | 0 |
| D: C + beam search 5 | 0.052 | 82 | 36 | 69 % | 14.3 | 55.4 | 0 |
| E: punctuated opening only (no glossary) | 0.045 | 72 | 43 | 63 % | 15.2 | 51.2 | 0 |
| **F: glossary hints, topic selection (adopted)** | 0.044 | 83 | 33 | **72 %** | 14.7 | 60.1 | 0 |

Findings

- Hints and a punctuated prompt fix the missing punctuation completely and reduce term errors; topic-based selection
  helps when the glossary is larger than the prompt budget.
- Beam search does not pay off here (accuracy unchanged, +40 % time) and is not used.
- Remaining errors are mostly homophones that sound identical; these are
  what "wrong -> right" corrections are for. Fillers (えー, まあ) appear more often once punctuation is produced; the
  existing cleanup removes the unambiguous ones.
- Limits: one speaker, one recording, one GPU; RTF differences of ±0.005 are within run-to-run noise.
