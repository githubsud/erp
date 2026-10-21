# Backups and restore

The whole database (all companies) is backed up by `scripts/backup.ps1` and put back by `scripts/restore.ps1`.
Both use the `erp-postgres` Docker container. Each script's help (`Get-Help scripts\backup.ps1 -Full`) lists all options.

## Every night

Run this once, as the Windows user who runs Docker:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\backup.ps1 -Install -At 23:30 -CopyTo "E:\ERP-backups"
```

This registers the scheduled task **ERP nightly backup**. Every night it:

1. saves `backups\erp-<date>-<time>.dump` with a `.sha256` checksum next to it;
2. **test-restores** that backup into a scratch database and checks that companies, users and ledger lines came back;
3. copies it to `-CopyTo`: a USB disk, or a folder that syncs to the cloud such as OneDrive (a backup on the same disk as the database dies with that disk);
4. keeps the newest 14 backups in each place (`-Keep` changes this);
5. writes one line per run to `backups\backup.log`, either `OK …` or `FAILED …`. Check this log every week.

To make a backup by hand: `powershell -ExecutionPolicy Bypass -File scripts\backup.ps1 -Verify`.

## Putting a backup back

```powershell
powershell -ExecutionPolicy Bypass -File scripts\restore.ps1                      # newest backup in backups\
powershell -ExecutionPolicy Bypass -File scripts\restore.ps1 -File E:\ERP-backups\erp-20261020-233000.dump
```

The restore script:
- checks the checksum;
- asks you to type `RESTORE`;
- saves the current database as `backups\before-restore-<time>.dump`, so the restore itself can be undone;
- stops the API, replaces the database, and starts the API again.

Everything entered after the backup was made is lost.

## Keep the encryption key

QID and passport numbers, IBANs and AI keys are stored encrypted with `Security:FieldEncryptionKey` (and `Security:BlindIndexKey`, used to search by QID). A backup restored under a different key loads, but those fields cannot be read.

Keep a copy of the production keys away from the backups, for example in a password manager. Never put it in the same folder or on the same disk as the backups.
