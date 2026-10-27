# Contributing

## Branches and review

Work on a task branch such as `fix/<topic>`, `feat/<topic>` or `docs/<topic>`.
Open a pull request into `main`; do not commit directly to `main`. Keep each
change focused and run the checks relevant to its behavior. A branch or pull
request does not authorize a release.

For documentation-only changes, include `[skip ci]` in the commit message and
pull-request title, and retain it in a squash commit. Do not dispatch builds or
signing for documentation.

## Repository hygiene

Keep `docs/` limited to maintained instructions, architecture and reference
contracts. Remove obsolete plans, investigation diaries, one-off validation
reports and duplicate release notes. Git preserves their history; do not create
a committed archive of unnecessary material.

Do not commit generated reports, raw captures, temporary scripts, diagnostic
output, personal paths or local tool configuration. Keep working evidence in
ignored `artifacts/` when needed. Review the complete staged file list and diff;
every added file must have a continuing purpose in the repository.

Keep internal hostnames and deployment addresses in ignored local configuration.
Documentation and tests should use neutral placeholders or reserved example
addresses.

## Issues

[GitHub Issues](https://github.com/Arderos/msfs24-landing-stats/issues) is the
source of truth for current defects and requested improvements. Check for
duplicates and confirm that a defect still affects current code or deployment.
Distinguish the version that recorded data from the version that uploaded it.

Use concise English titles and descriptions. State the problem, concrete impact,
priority (P0–P3 with a reason), reproduction evidence and acceptance criterion.
Link supporting code or evidence instead of pasting investigation diaries. Keep
private telemetry and machine-local paths out of public issues.

Missing information in historical captures, already fixed recording limitations,
speculative risks and isolated unexplained events need investigation before they
become current bugs. Preserve the former LS identifier when migrating an issue;
do not recreate resolved issues just to close them or maintain a second backlog.

An issue does not authorize implementation, deployment or release. Close it with
the verified result and published version when applicable.

## Changelogs and release notes

- Compare with the previous **published release**, not an intermediate build,
  working-tree state, or the most recent commit. Verify the release baseline.
- Describe the final user-visible changes, not the development process, audit
  history, implementation details, or intermediate decisions.
- Keep entries concise. Combine related changes into one short, outcome-focused
  bullet rather than listing every operation or internal fix separately.
- **Added**: a capability that did not exist in the previous published release.
- **Improved**: an improvement to a capability that already existed in that release.
- **Fixed**: a bug that affected the previous published release. Do not list bugs
  introduced and fixed during development of the new version.
- Describe a new capability in its finished form; do not present refinements to
  its unreleased implementation as improvements to the previous release.
- Write changelogs in English unless the user explicitly requests another language.
- Apply these rules to both `CHANGELOG.md` and the published release notes.

For example, the 0.8.9 UI work is one fix:

> Moved intensive operations out of the UI thread, fixing periodic freezes and keeping the interface responsive during loading, saving, and shutdown.

Do not add an Improved entry about a 500 ms placeholder delay when those loading
placeholders did not exist in 0.8.8.
