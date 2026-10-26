# Manage examples

These examples use the canonical client-neutral layout. The same nested maintenance sections work under a legacy `qBit-*` section. Configure and save them in **Manage**; use previews before applying changes.

## Manual recovery that keeps failed torrents

```toml
[TorrentClient.seedbox]
Host = 'localhost'
ManagedCategories = ['movies']
[TorrentClient.seedbox.Maintenance]
Enabled = true
[TorrentClient.seedbox.Maintenance.ErrorRecovery]
Enabled = true
Outcome = 'keep'
MaxRepairAttempts = 1
MaxPerRun = 2
[TorrentClient.seedbox.Maintenance.Operations.ErrorRecovery]
Enabled = true
ExecutionMode = 'manual'
ExcludedTags = ['keep', 'manual-pause']
```

## Daily orphan cleanup

Only the orphan tool is given automatic execution here. Other tools keep their existing execution policies; configure or disable them separately under Manage.

```toml
[TorrentClient.seedbox]
Host = 'localhost'
[TorrentClient.seedbox.Maintenance]
Enabled = true
[[TorrentClient.seedbox.Maintenance.PathMappings]]
ClientPath = '/downloads'
LocalPath = '/data/downloads'
[TorrentClient.seedbox.Maintenance.Orphans]
Enabled = true
ScanRoots = ['/data/downloads']
MinimumAgeMinutes = 1440
MaxFilesPerRun = 10
[TorrentClient.seedbox.Maintenance.Operations.OrphanScan]
Enabled = true
ExecutionMode = 'automatic'
Schedule = '15 3 * * *'
```

## Manual hardlink audit

```toml
[TorrentClient.seedbox]
Host = 'localhost'
ManagedCategories = ['movies']
[TorrentClient.seedbox.Maintenance]
Enabled = true
[[TorrentClient.seedbox.Maintenance.PathMappings]]
ClientPath = '/downloads'
LocalPath = '/data/downloads'
[TorrentClient.seedbox.Maintenance.Hardlinks]
Enabled = true
Tag = 'noHL'
[TorrentClient.seedbox.Maintenance.Operations.HardlinkAudit]
Enabled = true
ExecutionMode = 'manual'
Schedule = '15 4 * * *'
```

Check the hardlink tool, then apply its reviewed result once. Select Automatic and save if scheduled execution is wanted. Failed hardlink inspection blocks tagging; root-scoped link counting remains unsupported.
