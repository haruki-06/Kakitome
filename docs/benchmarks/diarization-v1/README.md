# Speaker diarization benchmark v1 (ADR-037)

- Input: 7 synthetic Japanese meeting conversations made with the Windows OneCore voices Ayumi, Haruka (female)
  and Ichiro (male; the 4th speaker is Ichiro with +30 % pitch): 2 speakers (female + male, both female), 3, 4, a
  1-speaker monologue, and two with
  pink-ish noise at 10 dB SNR. Pauses of 0.25–0.7 s between turns. `Kakitome.Bench diarize` builds and scores them.
- Engine: sherpa-onnx 1.13.8 offline diarization, pyannote segmentation 3.0 + the embedding model below, CPU.
- Turn accuracy: each true turn gets the found speaker it overlaps most (what Kakitome does with transcript segments);
  found speakers are then mapped to true speakers one-to-one to maximize correct turns. Count: conversations whose
  number of speakers was estimated exactly (auto) — not applicable when the count is given.
- One stage = sherpa-onnx clustering as is. Two stage (Kakitome): first stage at threshold 0.7, then one voice
  embedding per group from up to 20 s of its audio, the recording's mean voice removed, groups merged at cosine ≥ 0.1,
  groups under 4 % of the speech joined to the closest speaker, and clusters whose original voices are ≥ 0.85 similar
  merged; with a known count, groups merge down to it.
- Limits: synthetic voices are cleaner and more consistent than people; overlapping speech is not covered.

| Embedding | Clustering | Turn accuracy (mean) | Worst conversation | Count right | RTF |
|---|---|---:|---:|---:|---:|
| 3D-Speaker campplus_sv_zh_en_16k-common_advanced | one stage, threshold 0.5 | 93% | 65% | 7/7 | 0.042 |
| 3D-Speaker campplus_sv_zh_en_16k-common_advanced | one stage, threshold 0.7 | 93% | 70% | 6/7 | 0.041 |
| 3D-Speaker campplus_sv_zh_en_16k-common_advanced | one stage, threshold 0.9 | 73% | 50% | 3/7 | 0.040 |
| 3D-Speaker campplus_sv_zh_en_16k-common_advanced | one stage, count forced | 76% | 50% | — | 0.041 |
| 3D-Speaker campplus_sv_zh_en_16k-common_advanced | two stage (default) | 90% | 50% | 6/7 | 0.047 |
| 3D-Speaker campplus_sv_zh_en_16k-common_advanced | two stage, count given | 93% | 70% | — | 0.046 |
| 3D-Speaker eres2net_sv_en_voxceleb_16k | one stage, threshold 0.5 | 89% | 60% | 4/7 | 0.073 |
| 3D-Speaker eres2net_sv_en_voxceleb_16k | one stage, threshold 0.6 | 90% | 65% | 5/7 | 0.073 |
| 3D-Speaker eres2net_sv_en_voxceleb_16k | one stage, threshold 0.7 | 92% | 70% | 5/7 | 0.088 |
| 3D-Speaker eres2net_sv_en_voxceleb_16k | one stage, threshold 0.8 | 88% | 67% | 5/7 | 0.090 |
| 3D-Speaker eres2net_sv_en_voxceleb_16k | one stage, threshold 0.9 | 79% | 50% | 3/7 | 0.075 |
| 3D-Speaker eres2net_sv_en_voxceleb_16k | one stage, count forced | 82% | 50% | — | 0.073 |
| WeSpeaker en_voxceleb_resnet34_LM | one stage, threshold 0.5 | 84% | 33% | 4/7 | 0.061 |
| WeSpeaker en_voxceleb_resnet34_LM | one stage, threshold 0.6 | 77% | 33% | 3/7 | 0.061 |
| WeSpeaker en_voxceleb_resnet34_LM | one stage, threshold 0.7 | 73% | 33% | 3/7 | 0.061 |
| WeSpeaker en_voxceleb_resnet34_LM | one stage, threshold 0.8 | 57% | 33% | 1/7 | 0.061 |
| WeSpeaker en_voxceleb_resnet34_LM | one stage, threshold 0.9 | 54% | 25% | 1/7 | 0.061 |
| WeSpeaker en_voxceleb_resnet34_LM | one stage, count forced | 89% | 67% | — | 0.061 |
| NeMo en_titanet_small | one stage, threshold 0.5 | 91% | 65% | 5/7 | 0.043 |
| NeMo en_titanet_small | one stage, threshold 0.6 | 92% | 65% | 6/7 | 0.043 |
| NeMo en_titanet_small | one stage, threshold 0.7 | 92% | 65% | 6/7 | 0.043 |
| NeMo en_titanet_small | one stage, threshold 0.8 | 82% | 50% | 4/7 | 0.043 |
| NeMo en_titanet_small | one stage, threshold 0.9 | 76% | 50% | 3/7 | 0.043 |
| NeMo en_titanet_small | one stage, count forced | 76% | 50% | — | 0.043 |

Per conversation (turn accuracy / speakers found):

- 3D-Speaker campplus_sv_zh_en_16k-common_advanced one stage, threshold 0.5: two-mf 100%/2, two-ff 94%/2, three 94%/3, four 65%/4, one 100%/1, two-mf-noisy 100%/2, three-noisy 94%/3
- 3D-Speaker campplus_sv_zh_en_16k-common_advanced one stage, threshold 0.7: two-mf 100%/2, two-ff 94%/2, three 94%/3, four 70%/3, one 100%/1, two-mf-noisy 100%/2, three-noisy 94%/3
- 3D-Speaker campplus_sv_zh_en_16k-common_advanced one stage, threshold 0.9: two-mf 50%/1, two-ff 50%/1, three 94%/3, four 50%/2, one 100%/1, two-mf-noisy 100%/2, three-noisy 67%/2
- 3D-Speaker campplus_sv_zh_en_16k-common_advanced one stage, count forced: two-mf 50%/1, two-ff 50%/1, three 94%/3, four 70%/3, one 100%/1, two-mf-noisy 100%/2, three-noisy 67%/2
- 3D-Speaker campplus_sv_zh_en_16k-common_advanced two stage (default): two-mf 100%/2, two-ff 94%/2, three 94%/3, four 50%/2, one 100%/1, two-mf-noisy 100%/2, three-noisy 94%/3
- 3D-Speaker campplus_sv_zh_en_16k-common_advanced two stage, count given: two-mf 100%/2, two-ff 94%/2, three 94%/3, four 70%/3, one 100%/1, two-mf-noisy 100%/2, three-noisy 94%/3

## Real recording check

A 57-minute two-person variety video imported from a URL (the user's own Library; not distributed). Speech with
background music, sound effects and lively voices.

| Clustering | Speakers found |
|---|---|
| one stage, threshold 0.7 | 189 (95 after assigning transcript segments) |
| one stage, threshold 0.9 / 1.1 / 1.3 | 76 / 17 / 1 |
| two stage without the mean voice removed, similarity 0.3–0.7 | 1 |
| two stage (default) | 2 |

All groups were 0.5–0.9 similar before removing the mean voice (shared microphone, room and music); after it the
groups formed three clusters, two of them 0.88 similar in their original voices (one person) and the third 0.80–0.83.

## Decision

Two stage with the defaults above: 90 % mean turn accuracy on the synthetic set (only the pitch-shifted 4th voice is
merged with its source voice), 2 speakers on the real video instead of 95. With a known count: 93 %, every count right.
The same-voice limit (0.85) rests on one real recording — revisit with more real recordings (single-speaker lectures in
particular).
