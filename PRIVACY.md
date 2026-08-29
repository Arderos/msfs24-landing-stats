# Privacy policy

Last updated: August 29, 2026

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

## Bug reports

The separate **Report bug** action is optional and requires an explicit click.
It uploads the retained telemetry for the latest landing together with its
calculated result and technical metadata needed to investigate the report. This
data is not uploaded merely by enabling Google Drive backup.

## Other network access

The application checks GitHub for software updates. Microsoft Flight Simulator
telemetry is received locally through SimConnect.

## Contact

Privacy questions can be submitted through the project's
[GitHub issue tracker](https://github.com/Arderos/msfs24-landing-stats/issues).

Changes to this policy are published in this repository with their revision
history.
