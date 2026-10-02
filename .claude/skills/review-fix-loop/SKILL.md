---
name: review-fix-loop
description: The multi-agent workflow used on FullUI to find and fix bugs: independent read-only reviewers per area, a consolidated severity-ranked docs/BUGS.md, parallel fix agents in git worktrees, then merge and re-verify. Use when the user asks to review code, build a bug list, fix a batch of bugs, or "walk through each file", and whenever work is large enough to split across agents.
---

# Review, rank, fix, verify

## 1. Review (parallel, read-only)
One reviewer agent per area (server, web, installer/CI, Fire TV). Each must read EVERY file in scope, one by one, and may only write throwaway files under the scratchpad. A good reviewer prompt names: the scope, the contract documents to compare against, the bug classes to hunt (security/privacy, concurrency, contract mismatches, real-platform assumptions the tests cannot see, error handling for a non-coder user, test quality) and the output format: ID, severity, file:line, what is wrong, concrete failure scenario, 1-2 line fix, plus a list of files reviewed with no issues. Tell reviewers to verify by running experiments (e.g. a DI-container smoke test, patching a mock to behave like the real thing) and to mark UNVERIFIED when they cannot.

## 2. Consolidate into docs/BUGS.md
Merge duplicates found by several reviewers into one entry. Rank by importance: CRITICAL (feature totally broken, security hole, data loss) > HIGH (likely to fail in real use) > MEDIUM > LOW. The order is the fix order. Add a "Test-quality gaps" section (tests that cannot fail, mocks more lenient than reality). Keep a status section that says FIXED / PARTIAL / NOT DONE / "needs a real server". Record scope decisions (e.g. desktop-only) and mark deferred IDs rather than deleting them.

## 3. Fix (parallel, isolated)
One fix agent per area in its own git worktree, with disjoint file ownership; tell each which IDs to do in order, to keep tests green, never weaken a test, to commit in logical commits, NOT push and NOT edit docs/BUGS.md (the coordinator updates it). Cross-area needs (e.g. web needs a new server field) go through the coordinator as messages, not edits. Change of scope mid-flight: message or stop the agent; do not merge an unfinished branch.

## 4. Merge and verify
Merge branches one at a time, rebuild, and run EVERY suite after each merge (server: `cd server && dotnet test`; web: typecheck, test, build, e2e; installer: `bash installer/tests/run-tests.sh`; ABI: `tools/abi-matrix.sh`). Update BUGS.md statuses from the agents' per-ID reports, push, and tell the user plainly what is proven (tests) versus unproven (needs a real Jellyfin / Windows / device).

## Habits that paid off
- Make the compiler or checker prove claims (pin to the oldest supported API; run the ABI matrix) before trusting agent reports.
- A reviewer that reproduces a bug with a faithful mock is worth ten that read code.
- Always check that a new regression test FAILS on the old code.
