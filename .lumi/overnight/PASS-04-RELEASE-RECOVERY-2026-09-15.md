# Lumi 1.0 Overnight Pass 04 — Release Recovery and Evidence Contracts

## Cold-audit recheck
The authority correction from Pass 03 remains binding. Repository root app source is historical Code247 and MUST NOT be modified as Code740. Operational authority remains installed-phone Code739 evidence. Implementation authority remains the canonical source embedded in the signed Code739 update package.

## Material finding: distribution artifact is present but not text-extractable through the connected GitHub surface
The overnight branch contains `.lumi/public-updates/Lumi-Code739-Silent-Listening-R449.zip` and its public pointer identifies target Code739 with package SHA `71a1e7d5c8a63e8e0fb29ebfe435d7d156b0e0544de7a0b6ffd407932d982870`. The available GitHub text fetch surface rejects the ZIP as non-UTF-8, and there are no Actions runs on the overnight branch from which to download a materialized source artifact.

This is an execution-environment limitation, not evidence that canonical source is missing from the package. Do not substitute Code247 source or fabricate a Code740 patch.

## Morning release recovery path
Before any Code740 build/publish, materialize the Code739 distribution ZIP in a binary-capable runner, then:
1. Verify package SHA equals the public pointer SHA.
2. Extract `payload/canonical-source.zip` and `payload/lumi-core.apk`.
3. Verify the embedded canonical source archive is non-empty and versionCode/versionName are Code739/5.3.4-silent-listening-r449.
4. Verify APK package is `com.distressedelk.lumi`, versionCode 739, and signing certificate continuity against the prior installed-compatible APK.
5. Use only that extracted canonical source as the Code740 base.
6. Apply voice lifecycle/barge-in changes there, build once, run static/unit/package integrity checks, embed the resulting canonical source, then create one consolidated candidate.
7. Publish only after the morning closure audit. G1-G7 remain PENDING until installed-phone evidence.

## Deterministic evidence contracts prepared for the canonical source
These are instrumentation contracts, not acceptance PASS claims.

### Memory recall proof
A recall attempt must emit a single correlated trace containing: query/trigger, selected durable record IDs, record age, reload-survival flag, confidence/relevance score, final recalled proposition hash, and whether the final Lumi response actually used that proposition. Increment `recallProof` only after the response commits and the recalled proposition came from durable history rather than current-turn context/provider suggestion. Acceptance still requires owner-triggered phone testing.

### Speaker continuity proof
For every accepted/rejected speech segment emit: speaker-evidence source, owner model/enrollment version, confidence, threshold, continuity binding generation, near-field status, decision, and reason. `activeSpeakerBound=true` must require positive local identity evidence for the current binding generation; admin persistence alone must never manufacture acoustic identity proof. Uncertain identity may continue only under the explicitly allowed continuity policy and must remain visibly unproven in Black Box.

### Recognizer lifecycle proof
Every recognizer CREATE/START/READY/STOP/CANCEL/DESTROY/REUSE must carry one lifecycle generation ID and reason. A barge-floor timeout with `recognizerActive=true` and a live recognizer MUST prefer REUSE/CONTINUE over STOP+START unless a documented liveness failure exists. This gives the next phone run a direct way to prove whether bloops correlate with lifecycle churn.

### Provider latency proof
Record per-provider attempt start/end, timeout budget, cooldown decision, whether the attempt was optional, and whether it delayed the selected healthy route. Optional degraded adapters must not sit serially in front of a known-ready route. Lumi Core remains final authority regardless of provider selected.

## Dependency classification rule
Dependency coverage must classify each evidence source as one of: CANONICAL_CURRENT, OPERATIONAL_CURRENT, DISTRIBUTION_CURRENT, HISTORICAL, or EXPERIMENTAL. Historical Code247 files and divergent feature branches cannot satisfy Code739/740 runtime coverage. Runtime coverage may increase only from current canonical implementation plus installed/runtime evidence.

## Bounded feature lanes
Phone Health/Device Steward and Home Network Intelligence remain eligible only as transplant/reference work until canonical Code739 source is materialized. Do not merge divergent historical branches into the 1.0 candidate. Home Network remains observation/information-only in this phase; no router policy/block/quarantine authority is introduced.

## Status
- No intermediate phone update published.
- No unsafe source modification made.
- Code740 remains blocked on binary materialization of the signed Code739 package.
- Release recovery procedure is now deterministic rather than relying on repository-root source.
- G1-G7 remain frozen PENDING/unarmed.
