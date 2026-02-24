# Fix Progress Log

## Task: Fix paths and process view chips

### Changes Required
1. Move db and logs to `.config/` (no `torrentarr` subfolder)
2. Fix Serilog log path to match API log path
3. Fix process view chips to only show ManagedCategories

---

## Progress

### [COMPLETED] Fix Program.cs (Host) - paths

**File:** `src/Torrentarr.Host/Program.cs`

Changes:
- Added base path calculation BEFORE Serilog init (line 14-17):
  ```csharp
  var basePath = Path.Combine(Directory.GetCurrentDirectory(), ".config");
  var logsPath = Path.Combine(basePath, "logs");
  var dbPath = Path.Combine(basePath, "qbitrr.db");
  Directory.CreateDirectory(basePath);
  Directory.CreateDirectory(logsPath);
  ```
- Serilog: `logs/torrentarr.log` → `.config/logs/torrentarr.log` (line 26)
- Database: removed old path and now uses `dbPath` defined at top (line 120)
- Removed duplicate `homePath`, `dbPath`, `logsPath` definitions

### [COMPLETED] Fix Program.cs (WebUI) - Serilog log path

**File:** `src/Torrentarr.WebUI/Program.cs`

Changes:
- Added base path calculation before Serilog (line 9-14)
- Serilog: `logs/webui.log` → `.config/logs/webui.log` (line 21)

### [COMPLETED] Fix Program.cs (Workers) - Serilog log path

**File:** `src/Torrentarr.Workers/Program.cs`

Changes:
- Added base path calculation before Serilog (line 17-21)
- Serilog: `logs/worker-{instance}.log` → `.config/logs/worker-{instance}.log` (line 27)

### [COMPLETED] Fix process view chips

**File:** `src/Torrentarr.Host/Program.cs` (lines 272-276)

Changes:
- Changed `monitoredForDefault` from union of ManagedCategories + Arr categories
- Now only shows ManagedCategories from qBit config
- This matches qBitrr behavior where qBit cards show only qBit-managed categories

---

## Summary

| File | Change |
|------|--------|
| `Program.cs` (Host) | Base path `.config/`, db `.config/qbitrr.db`, logs `.config/logs/`, chips fix |
| `Program.cs` (WebUI) | Logs to `.config/logs/webui.log` |
| `Program.cs` (Workers) | Logs to `.config/logs/worker-{instance}.log` |

**Build Status:** ✅ SUCCESS (0 warnings, 0 errors)

---

## New Folder Structure

```
.config/
├── config.toml      # Config file
├── qbitrr.db       # SQLite database
└── logs/
    ├── torrentarr.log   # Host logs
    ├── webui.log       # WebUI logs
    └── worker-{name}.log # Worker logs
```
