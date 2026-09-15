# Lumi 1.0 Overnight Pass 02 — Voice Lifecycle Root Cause

## Baseline
Development head starts at f37edcf86e2d7cbc1eb263af4b5bdee5ed118b5b. Phone operational authority remains installed Code739 Black Box. No phone publication in this pass.

## Material diagnosis
1. Code739 cue suppression is operationally disproven as a complete fix. It armed/restored successfully but owner still heard bloops.
2. The captured turn contains a deterministic churn chain:
   - active TTS -> human-onset candidate -> yield after 140ms
   - recognizer hears Lumi self-audio (`Based...`) and rejects partials
   - barge floor times out while recognizerActive=true and recognizerPresent=true
   - recovery nevertheless issues STOP and transitions to RECOVERING
   - a new on-device SpeechRecognizer START follows
   - Code739 suppression arms around that new START and restores on READY
3. Therefore the next source fix must target lifecycle ownership, not broaden stream muting.

## Frozen Code740 candidate contract
A. Healthy-recognizer preservation
- On barge floor timeout, if recognizerActive && recognizerPresent and no recognizer error/liveness failure exists, close the barge floor without stopping/recreating SpeechRecognizer.
- Re-arm only after explicit liveness failure, recognizer error, destroyed recognizer, manual start from STOPPED, or a bounded watchdog proving no audio/transcript liveness.

B. Self-audio-before-yield gate
- A TTS-overlap onset alone is insufficient to stop TTS.
- Require non-self evidence before yielding: usable partial not matching current/recent Lumi TTS, or independent near-field/audio evidence above the existing confidence threshold.
- Self-audio partials extend/close the observation window without forcing recognizer recreation.

C. Cue-origin instrumentation
- Record recognizer start/stop/recreate counters and reason codes per conversation generation.
- Record whether an audible-cue opportunity occurred at create/start/stop/destroy boundaries.
- Preserve Code739 stream suppression temporarily as diagnostic fallback only; do not mute STREAM_MUSIC, STREAM_ALARM, or STREAM_VOICE_CALL.

D. Acceptance invariants
- G1-G7 remain PENDING until installed-phone owner evidence.
- No source test may label bloops fixed.
- Candidate success requires materially fewer recognizer lifecycle boundaries in synthetic/source diagnostics and zero regression to TTS/visual/truth-convergence contracts.

## Update/install audit
Current repository evidence confirms the public workflow only publishes a verified package/pointer. It does not perform an Android PackageInstaller commit on the phone. Self-install therefore remains blocked at the Android installer/security boundary until an owner-granted mechanism (e.g. authorized PackageInstaller flow or maintenance/ADB bridge) produces installed-phone proof. Do not represent update discovery as installation.

## Repository hygiene finding
The overnight branch carries many historical public update packages/workflows and an Aug-20 SOURCE-MANIFEST describing Code247. These are historical artifacts, not current implementation authority. Do not delete them during the release-critical overnight lane, but exclude them from current-truth reasoning unless explicitly marked historical. Canonical Code739 package/source remains the implementation baseline.

## Next lane
Obtain/edit the canonical Code739 source from the signed update package or authoritative embedded source, implement the lifecycle contract above, then build/test as a development candidate without publishing. If canonical source cannot be safely materialized through the available connector, continue with provider latency, dependency evidence, memory recall instrumentation, and install-boundary diagnostics rather than fabricating a code change.
