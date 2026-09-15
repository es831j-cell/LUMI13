# Lumi 1.0 Overnight Cold Audit — 2026-09-14

## Authority baseline
- Implementation authority: canonical Code739 source/release.
- Operational authority: installed-phone Black Box from Code739.
- Acceptance gates remain frozen and unarmed. No source/build result may claim installed-phone PASS.
- One consolidated morning update only. No intermediate phone publication.

## Verified current phone baseline
- Installed: Code739 / 5.3.4-silent-listening-r449.
- Public updater reports installed=published=739 and UP_TO_DATE.
- TTS Code738 recovery is operational: requested/actual local voice agree; synthesis start/success true; last error 0.
- Visual finalization self-check is PASS on Code739 evidence.
- Current causal integrity reports PASS with no open/orphan/dropped traces in the captured release evidence.
- Durable conversation store is present and reload-proven, but recall acceptance proof remains 0.

## Material defects / gaps ranked
### P0 — Voice cue suppression did not solve owner-observed bloops
Code739 reports 13 suppression cycles and restoration on READY, yet the owner still hears bloops. The current implementation assumes the audible cue lives on SYSTEM/NOTIFICATION streams. That assumption is disproven operationally. Do not extend stream muting blindly. Diagnose lifecycle churn and recognition-service behavior first. Current evidence shows barge-in timeout recovery stops/rearms SpeechRecognizer, creating another cue opportunity.

### P0 — Self-update stops at Android install boundary
Update discovery, package verification, canonical validation and phone-channel convergence work, but unattended install is not operationally proven. Android installer/security boundary remains authoritative. Do not claim self-install until an allowed mechanism produces installed-phone evidence. Desktop/ADB may be used only under granted maintenance authority.

### P0 — Frozen Lumi 1.0 acceptance is NOT ARMED
G1 Natural Conversation, G2 Durable Conversation Memory, G3 Speaker/Owner Continuity, G4 Hands-Free Barge-In, G5 Real Phone Function, G6 Resource Endurance, G7 Black Box/Intent Evidence all require installed-phone evidence. Overnight work may improve prerequisites but cannot mark these PASS.

### P1 — Barge-in false-positive/self-audio path
Code739 evidence shows active TTS generated a human-onset candidate, yielded after 140 ms, then STT heard Lumi's own phrase ('Based on...') and rejected it as self audio. The floor later timed out and forced recognizer recovery/rearm. This path can degrade naturalness and likely contributes to cue frequency. Desired fix: retain recognizer when healthy, reject self-audio before yielding TTS when confidence is insufficient, and avoid stop/rearm solely because a barge floor expired with recognizer still alive.

### P1 — Conversation latency
Last captured response latency is 8343 ms. Provider routing has one healthy provider while Gemini timed out and Cloudflare returned 404. Optional providers must not stall the healthy route. Diagnose timeout/cooldown contribution and keep Lumi Core final authority.

### P1 — Memory recall proof absent
Store: present, 179 records / 88 completed turns in captured evidence, reloadProof=true, recallProof=0. Need deterministic recall test instrumentation without fabricating owner acceptance.

### P1 — Speaker continuity not proven
ownerAccepted=2 but activeSpeakerBound=false and audio gate reports identity evidence unavailable with continuity allowed. Owner/admin continuity persistence is present, but acoustic speaker identity acceptance remains unproven.

### P1 — Dependency/runtime coverage partial
Truth convergence reports dependencyTopology=GRAPH_VERIFIED with findings=0, but runtimeCoverage=7/12 PARTIAL and sourceCorrections activeDefects=2 pendingAcceptance=2 totalOpen=4. Expand evidence coverage before 1.0.

## Architecture constraints carried forward
- Lumi Core is final decision authority. Providers/routers/executors are delegates.
- Dependency tracing is required for executable, conversational, UI, maintenance, update, diagnostic, routing and recovery paths.
- UI may be Lumi-authored within platform/security/recovery bounds, with evidence and rollback.
- Phone Health V1: asked scan = full scan; diagnose first, safe reversible repair pass second, verify repairs, max 3 safe attempts before asking for ADB escalation; severity Info/Warning/Critical; include third-party app health; history retained.
- Home Network Intelligence current phase is observation/information-only. Discover/correlate/inventory devices, ownership/history/signal/capabilities, but do not block/quarantine/change router/firewall/VLAN/DNS/SSID policy.
- Device capability discovery should model exposed authorized capabilities such as print/scan/cast/storage/etc., without inventing actuation authority.
- Preserve Guardian/recovery separation, signing continuity, canonical-source integrity, rollback and append-only history requirements.

## Overnight development lanes
1. Voice lifecycle: eliminate unnecessary SpeechRecognizer stop/rearm and instrument actual cue origin. Do not mute music/alarm/call streams.
2. Barge-in: distinguish human onset from self TTS before yielding; preserve healthy recognizer through floor timeout.
3. Update/install: audit exact Android install boundary and available owner-granted path; never fake unattended install.
4. Memory/identity: add deterministic evidence instrumentation for recall and speaker continuity while keeping acceptance owner-triggered.
5. Provider latency: fail fast around degraded optional adapters; healthy provider should not wait behind optional failures.
6. Dependency/diagnostics: raise 7/12 runtime coverage and close active defects where source evidence supports it.
7. Phone Health and Home Network: integrate only bounded, testable foundations that do not destabilize the 1.0 core.

## Morning release closure gates
- Clean build and tests.
- Signer continuity.
- Canonical source embedded and reproducible.
- Update/package hashes verified.
- No regression to Code738 TTS recovery or Code736/737 visual/truth behavior.
- No intermediate publication.
- One final candidate only after cold-audit rerun.
- Installed-phone G1-G7 remain PENDING until real owner test evidence exists.
