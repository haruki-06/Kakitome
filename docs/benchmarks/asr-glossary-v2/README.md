# Automatic glossary benchmark v2 (ADR-031)

Question (user proposal, 2026-10-04): can Kakitome detect a poor transcript and build a glossary by itself, then
transcribe again?

- Input: the same real Japanese lecture as v1 (1 h 27 min), imported by the user. The audio, the
  transcripts and the reference list stay on the user's PC; only aggregate numbers are reported here.
- Engine: whisper.cpp (Whisper.net 1.9.1, Vulkan) / whisper-large-v3-turbo-q5_0 on an RTX 5070 Ti, product path
  (`Kakitome.Bench lecture`), greedy decoding, 1.3.x prompts.
- Reference: 62 misrecognition pairs found by reading the baseline transcript (homophones of
  domain terms). Score = occurrences of the right forms / (right + wrong).
  This set is wider and harder than v1's 35 terms, so the numbers are not comparable with v1.
- Tools: `Kakitome.Bench glossary-draft` (local LLM draft) and `glossary-score`.

## Can a poor transcript be detected?

Not from the recognizer's confidence. The baseline transcript had ~90 wrong domain terms, yet its mean segment
confidence was 0.89 and no segment was below 0.5 (two below 0.7). Homophone errors are confident errors: the audio
really sounds like the wrong form. Only knowledge of the subject reveals them.

## Can a local model build the glossary?

| Variant | Right | Wrong | Accuracy | Extra time |
|---|---:|---:|---:|---|
| A: no glossary (current default) | 30 | 92 | 25 % | — |
| B: Qwen3-4B (local "Summary AI") drafts terms from A, re-transcribe with them as hints | 36 | 70 | 34 % | LLM 41 s + full ASR again (255 s) |
| C: B's "wrong -> right" fixes applied to A | — | — | — | 0 usable fixes |
| D: a correct glossary (the 62 right-hand terms, no corrections), re-transcribe | 56 | 61 | 48 % | full ASR again |

Findings

- C: the 4B model's fix proposals were mostly harmful — meaning-changing rewrites, a reversed fix
  (right → wrong) and style edits of whole sentences. Conflicting proposals cancel
  out, and nothing usable is left. Applying such fixes automatically would add errors.
- B: drafted terms help as hints (+9 points), but the draft also lists misrecognized forms as terms, which the
  re-run then reinforces. A reading check (same pronunciation) cannot tell the
  direction of a homophone fix, and the Windows phonetic analyzer is not available to an unpackaged app
  (CLASS_E_CLASSNOTAVAILABLE).
- D: a glossary from someone who knows the subject (the user, or a capable AI assistant the user chooses to ask)
  nearly doubles the accuracy with hints alone; its "wrong -> right" lines then fix the remaining known errors after
  every future transcription of the project.

## Decision

Guide the user to D instead of running B automatically (ADR-031): every longer transcript without a glossary shows a
tip with a ready-made request for an AI assistant (to be given together with `transcript.txt`), and one action imports
the result for the recording's project and transcribes again. B is kept as a benchmark experiment; it may be offered
later as a reviewable local draft.

Limits: one lecture, one speaker, one GPU; the reference pairs were labelled by the agent from the transcript.
