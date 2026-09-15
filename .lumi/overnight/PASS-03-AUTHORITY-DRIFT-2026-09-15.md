# Lumi 1.0 Overnight Pass 03 — Canonical Authority Drift

## Material finding
The previous overnight passes treated the repository `main` source tree as if it were the canonical Code739 implementation source. That is false.

Evidence:
- `main` / `lumi-1.0-overnight` app/build.gradle declares versionCode 247 and versionName 4.0.0-lumi-1.0-r1.
- Commit d191ba9 is named `publish(lumi): Code739 Silent Listening`, but its diff only updates `.lumi/public-updates/Lumi-Code739-Silent-Listening-R449.zip` and `.lumi/public-updates/latest-update.json`; it does not update the checked-out app source tree to Code739.
- The installed phone Black Box proves Code739 is the operational runtime.

Therefore the current overnight branch contains audit notes layered over historical Code247 source plus Code739 distribution artifacts. Editing `app/src/...` on this branch would violate the locked rule that canonical source is implementation authority and could silently regress hundreds of releases.

## Correction to overnight authority model
1. Operational authority remains installed-phone Code739 Black Box.
2. Implementation authority is the canonical source embedded in the signed Code739 update package, not the repository root app tree.
3. Do not implement Code740 by patching the Code247 root tree.
4. Do not merge `feature/phone-health-v1` into the morning candidate. It diverges from main (9 commits ahead, 14 behind) and was developed against a historical tree. Preserve it as design/source reference only until its changes are rebased onto the actual canonical Code739 source.
5. Historical SOURCE-MANIFEST / Code247 material remains historical and must be explicitly excluded from current-truth dependency coverage.

## Release-critical consequence
The voice lifecycle diagnosis remains valid because it is grounded in installed-phone Code739 causal evidence, but no Code740 source modification is safe until canonical Code739 source bytes are materialized from the signed package. The connector can inspect text files and metadata but cannot safely extract/edit the binary ZIP payload in place. Do not fabricate a source fix.

## Next independent lanes while canonical source extraction is blocked
- Audit provider latency and optional-adapter fail-fast behavior using phone evidence and any current-release source artifacts that are text-accessible.
- Define deterministic memory recall and speaker-continuity evidence contracts without marking acceptance PASS.
- Audit Phone Health branch as a transplant candidate only; identify portable modules and dependencies, no merge.
- Expand dependency graph classification to distinguish canonical-current, operational-current, historical, experimental, and distribution-only artifacts.
- Preserve one consolidated morning update rule; if canonical Code739 source cannot be safely materialized/build-tested before morning closure, do not publish a fake Code740.

## Frozen gates
G1-G7 remain PENDING and unarmed. No source or repository test substitutes for installed-phone owner evidence.
