# Google Health Data Export

The 07:00 Asia/Tokyo timer collects all 39 documented readable Google Health v4
data types, plus profile, settings, account identity, irregular-rhythm profile,
paired devices, and exercise TCX exports. `HEALTH_INCLUDE_ALL_DATA` defaults to
`true`; setting it to `false` retains the smaller summary mode.

## Coverage

| Category             | Data types or resources                                                                                                                                                                        |
| -------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Activity             | Steps, active and total calories, distance, floors, altitude gain, active minutes, active-zone minutes, sedentary periods, activity level, time and calories in heart-rate zones, swim lengths |
| Fitness and exercise | Exercise sessions, running VO2 max, VO2 max, daily VO2 max, exercise TCX including GPS positions when recorded                                                                                 |
| Health measurements  | Heart rate, HRV, oxygen saturation, respiratory rate during sleep, weight, body fat, height, blood glucose, core body temperature                                                              |
| Daily health         | Resting heart rate, HRV, heart-rate zones, oxygen saturation, respiratory rate, sleep temperature derivations                                                                                  |
| Sleep                | Sleep sessions and the stages and summaries supplied by Google                                                                                                                                 |
| Nutrition            | Nutrition logs with nutrients, hydration logs, food details referenced by those nutrition logs, food measurement units                                                                         |
| Medical features     | ECG sessions and waveform samples, irregular-rhythm notifications, irregular-rhythm profile                                                                                                    |
| Account              | Health profile, settings, identity, paired devices                                                                                                                                             |

The catalog is defined in [HealthDataCatalog.cs](src/HealthSync.Core/HealthDataCatalog.cs).
It does not include v4beta-only sensor types or write-only menstrual periods,
ovulation tests, symptoms, or moods. No health-write permission is requested.
The shared food database is not copied: only foods referenced by the selected
nutrition logs are retrieved. Food measurement units are reference information,
not evidence that a food was consumed.

## Window and Granularity

The default interval is today and the preceding 13 dates, not all account history.
High-frequency activity and measurement types use one-hour rollups. Daily types
retain their date, while sleep, exercise, nutrition, hydration, ECG, and rhythm
notifications retain the returned records. Profiles, settings, devices, and
food reference information describe the current state.

Physical-time queries use UTC boundaries. Session APIs that require recorded
civil time use an expanded query and are filtered back to the exact UTC interval.
Sleep uses the wake/end time. An exercise TCX is validated as XML without DTDs or
external entities, and embedded in the snapshot JSON rather than a separate TCX file.

## Long-Term History

`HEALTH_ARCHIVE_ENABLED` defaults to `true`. Each successful run writes the entire
snapshot to `Health Sync History/health_sync_YYYY-MM-DD.json`, then updates the
existing latest file. The date uses `HEALTH_TIMEZONE`. Same-day reruns reuse the
archive using private app properties, not a filename-only match. Archives from
earlier dates are never automatically deleted or replaced.

The history folder is created in My Drive and reused. An authorized folder can
be selected with `DRIVE_ARCHIVE_FOLDER_ID`. Shared folders, shared-drive files,
and destinations whose unshared status cannot be checked are rejected. No extra
Drive scope is requested. If archiving fails, the latest file is not updated.

The 14-day limit applies to each API query, not the lifetime of stored files.
Archives overlap and must not be summed as disjoint periods. History starts when
this feature is enabled; account-wide historical backfill is not implemented.
Missed runs beyond the retrieval window may leave gaps. Storage usage grows until
the user manually removes files. Disabling archives does not delete existing ones.

The full snapshot uses `schema_version: 2`, `collection_mode`, and
`collection_statuses`. Each of the 39 types and six associated collections has a
status: `ok`, `empty`, `partial`, `not_supported`, `not_eligible`, `access_denied`,
`not_recorded`, or `not_collected`. Unavailable collections do not have fabricated
empty arrays. Only documented per-type availability errors are optional; missing
OAuth consent, malformed responses, and transport failures stop the upload.

Successful empty arrays mean no records were returned, not a measured zero.
`record_counts` counts records or aggregate buckets, not step/calorie totals or
necessarily days with measurements. Do not add hourly and daily representations
of the same metric, or overlap records from different sources.

## Daily Steps and Calories

The snapshot also retains these daily Google Health aggregates:

| Snapshot record key          | Google Health data type | Meaning                                                                    |
| ---------------------------- | ----------------------- | -------------------------------------------------------------------------- |
| `steps-daily`                | `steps`                 | Daily step count                                                           |
| `total-calories-daily`       | `total-calories`        | Total energy expenditure, including basal metabolism and activity, in kcal |
| `active-energy-burned-daily` | `active-energy-burned`  | Activity energy expenditure, excluding basal metabolism, in kcal           |

Total calories already include active energy. Do not add the two calorie values together.
These values are estimates and depend on the device, wearing time, and Google Health synchronization.

## Requests and Dates

Each type uses the read-only `POST dataPoints:dailyRollUp` endpoint with
`windowSizeDays: 1`. The range starts at midnight on the first included date
and ends at the query time, expressed in the configured timezone.
The default snapshot covers today and the preceding 13 dates.
The current day's bucket is incomplete at 07:00, not an end-of-day total.

Google Health assigns civil days using each record's UTC offset. Travel and
daylight-saving changes can therefore affect day boundaries. Full mode uses
`users/me/dataSourceFamilies/all-sources`, including available third-party and
manually logged records. Summary mode uses `google-wearables`.

API `rollupDataPoints` are preserved with their civil start/end times and
type-specific values. A bucket with an unset value means missing data. An explicit
zero value remains zero. `record_counts` counts returned buckets, not steps,
calories, or necessarily days with measurements.

## Authorization and Failure Behavior

The nine Health permissions are read-only: `activity_and_fitness`,
`health_metrics_and_measurements`, `nutrition`, `sleep`, `ecg`, `irn`, `profile`,
`settings`, and `location`, under
`https://www.googleapis.com/auth/googlehealth.<category>.readonly`.
Existing users must repeat consent and update the refresh token before deploying
full mode. Health-only and Drive-only access tokens remain separate. The Drive
permission is still `drive.file`, not full Drive access.

Required Health requests must finish before the existing Drive JSON is replaced.
Known per-type availability restrictions are stored as explicit statuses.
An unexpected failure leaves the previous snapshot intact. Read-only aggregation
requests may retry transient failures, but file creation does not.
The timer schedule, target Drive file, Key Vault references, and network settings
are unchanged.

The collector limits records to 64 MiB, each HTTP response to 16 MiB, and each
paged collection to 250,000 records and 100 pages. Exceeding a limit fails the
run without silently truncating records. The Function has an eight-minute
cancellation budget. These are per-run limits, not an automatic archive retention policy.

The schema can contain 49 record collections and 45 collection statuses when
every requested collection is available. These are schema counts, not records
from a particular person's account. Verify actual permissions, returned records,
file freshness, and destination sharing in your own environment.

## References

- [Google Health data types](https://developers.google.com/health/data-types)
- [Google Health data filters](https://developers.google.com/health/filters)
- [Google Health nutrition](https://developers.google.com/health/data-types/nutrition)
- [Google Health dailyRollUp reference](https://developers.google.com/health/reference/rest/v4/users.dataTypes.dataPoints/dailyRollUp)
- [Google Health aggregation guide](https://developers.google.com/health/aggregate-data)
- [Public application information](https://sheegyy.github.io/health-sync-info/)
- [Public privacy policy](https://sheegyy.github.io/health-sync-info/privacy.html)
