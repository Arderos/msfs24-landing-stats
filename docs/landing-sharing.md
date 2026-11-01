# Share a landing

Hover a landing in the history list and select **Share**, immediately to the left
of Delete. The standard Windows save dialog writes one `.lslanding` file. The
file includes all available contacts belonging to that landing episode, including
bounces, and the saved data for charts, 3D playback and controller assignments.
Older recordings retain the data they originally contained.

Select **Import** above the history list to choose one or several files. Dropping
the files onto the application window or opening them from Windows Explorer uses
the same import operation. Opening a file brings the application forward and
selects the imported landing, even if it was already imported.

Imported contacts have a distinct history shade and an **Imported** label. They
are excluded from the monthly average. Native recordings remain separate even
when a file contains a copy of one of the user's own landings. Saved charts and
3D playback work without a running simulator or connected controllers.

## Identity and persistence

Import validates a complete package before publishing its contacts. Each contact
gets a local identity derived from the canonical package revision and original
contact ID. Its original identity and package hash remain in the saved record
and summary index. Local API projections expose `isImported` as well.

Importing the same content again selects the existing contact without creating
a duplicate. Re-exporting an imported episode preserves that content identity.
A changed package or a different subset of its contacts is a separate revision,
so revisions cannot accidentally become one replay episode.

Deleting an imported contact retains the ordinary deletion tombstone. An explicit
later file import can restore the contact under a new local identity without
clearing that tombstone. Repeated imports then find the restored contact. Imports
never replace a native recording. Each contact uses the repository's durable
publication and index recovery; a storage failure may leave completed contacts
in place, and retry imports only the missing contacts.

Each selected file has an independent result. A damaged file is reported while
other selected files continue importing. The history panel shows the imported,
duplicate and failed counts, with expandable error details.

## File format

Version 1 is a ZIP archive with exactly two members:

| Member | Contents |
| --- | --- |
| `manifest.json` | `format` (`MSFS Landing Stats landing`), `version` (`1`), contact count in `contacts`, and the SHA-256 of `landings.json` in `sha256` |
| `landings.json` | A `contacts` array of complete [v7 landing records](format-v7.md) |

The reader checks ZIP structure, bounded expansion, manifest version, checksum,
record layout, series columns and episode consistency before writing history.
No archive entry is extracted to a filesystem path. Encoded and total decoded
content are bounded by the existing 256 MiB archive limits; the manifest is
limited to 4 KiB. Unsupported versions and incomplete or ambiguous episodes are
rejected. This package contains compact history, not the original raw diagnostic
capture, and importing it does not rerun landing analysis.

## Windows integration

On each ordinary launch, the portable launcher checks the current user's
`Software\Classes` association for `.lslanding`. The `MSFSLandingStats.Landing`
ProgID points its open command and icon to the actual portable executable. Moving
the executable and launching it refreshes those paths; the extracted runtime
does not replace them. Windows' protected `UserChoice` setting is left intact.

The launcher writes file-open requests to a durable inbox before checking whether
an application instance already exists. The current user's application in the
same Windows session handles queued requests after startup and when notified.
It acknowledges a request after import handling, so a request arriving during
startup or recovery survives the process handoff. Retried requests use the same
duplicate detection as an explicit import.

Export and import are local file operations. Sharing the exported file with
another person is a separate action. Imported records participate in optional
history backup with their imported origin preserved.
