# Privacy policy

Last updated: October 10, 2026

MSFS Landing Stats is a local Windows application. Landing analysis and storage
happen on the user's computer.

## Google Drive backup

Google Drive backup is optional and starts only after the user connects a Google
account in Settings. The application requests the
`https://www.googleapis.com/auth/drive.file` scope. This permits it to create,
read, update, and delete only the Google Drive files that MSFS Landing Stats
creates or that the user explicitly opens with the application. It does not
request access to the rest of the user's Drive.

When backup is enabled, MSFS Landing Stats synchronizes:

- saved landing records;
- application language and simulator auto-start preferences.

OAuth credentials are protected for the current Windows user and remain on the
local computer. They are not included in the backup. Raw telemetry, pending bug
reports, and diagnostic upload identity are not synchronized to Google Drive.
Google Drive data is transferred directly between the application and Google's
APIs; the project author does not receive it.

Google Drive data is used only to provide the backup and synchronization
features requested by the user. It is not sold, shared with advertisers, used
for advertising, or used to train models. MSFS Landing Stats' use and transfer
of information received from Google APIs adheres to the
[Google API Services User Data Policy](https://developers.google.com/terms/api-services-user-data-policy),
including the Limited Use requirements.

The user can disconnect Google Drive in Settings at any time. Access can also be
revoked from the Google Account permissions page. Disconnecting removes the
locally stored OAuth credential but does not delete existing backup files.
Deleting a synchronized landing in MSFS Landing Stats moves its corresponding
Drive backup file to the Drive trash during synchronization. The user can also
remove the application folder or individual files directly in Google Drive.

## Diagnostic telemetry

Automatic diagnostic uploads are enabled by default, including after an update.
They can be disabled in Settings. This preference applies to this installation
and is not changed by Google Drive synchronization. When disabled, automatic
uploads stop, including queued retries; already received server data is not
deleted. Pending automatic reports remain local and resume if re-enabled.

Each report contains the raw recorded landing episode, including aircraft state,
exact simulator position, physical controller axes and device labels, calculated
landing results, and a bounded journal of analysis and 3D reconstruction decisions.
The journal includes selected methods, fallback/rejection reasons and diagnostic
measurements. It does not contain account credentials or a hardware UUID.
Reports go to the Landing Stats diagnostic server over HTTPS, authenticated with
a random installation ID and a Windows-protected per-installation signing key.

Automatic reporting runs in the background without interface notifications.
When it is off, **Report bug** is available for explicit submission of the latest
retained landing. Automatic uploads and manual reports are separate from Google
Drive backup. Existing legacy RAW captures still require their previous upload
consent as well as the current automatic-upload setting; the new default does
not authorize those older files. Turning automatic uploads off also pauses
previously consented legacy RAW uploads. Explicit manual bug reports are unaffected.

Automatic reports waiting locally are bounded to 128 archives and 256 MiB,
including quarantined reports. When full, no new automatic diagnostic copy is
created; saved landing history is unaffected. Disabling automatic reporting does
not disable local landing analysis or storage.

## Other network access

The application checks GitHub for software updates. Microsoft Flight Simulator
telemetry is received locally through SimConnect.

## Contact

Privacy questions can be submitted through the project's
[GitHub issue tracker](https://github.com/Arderos/msfs24-landing-stats/issues).

Changes to this policy are published in this repository with their revision
history.
