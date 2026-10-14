# Project instructions

## Backlog

- Keep discovered unresolved issues and deferred improvements in `BACKLOG.md`.
- Before adding an issue, check for an existing entry and update it rather than
  creating a duplicate. Preserve reproduction data, distinguish confirmed causes
  from hypotheses, and include verification criteria.
- A backlog entry is not authorization to implement, deploy, or release a change.
- When resolved, record verification and the published version without deleting
  the issue's history.

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
