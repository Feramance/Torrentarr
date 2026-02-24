# Torrentarr Logging Overhaul Plan

## Current State Analysis

**Existing Implementation:**
- **Host (Orchestrator)**: `.config/logs/torrentarr.log` (rolling daily)
- **WebUI**: `.config/logs/webui.log` (rolling daily) 
- **Workers**: `.config/logs/worker-{instanceName}.log` (rolling daily)

**Current Issues:**
1. Only Host has a `LoggingLevelSwitch` for runtime log level changes
2. WebUI and Workers don't have runtime log level control
3. No correlation ID propagation across processes
4. Limited Trace-level logging in services

## Enhanced Plan for Per-Process Logging

### Phase 1: Standardize Runtime Log Level Control
- Add `LoggingLevelSwitch` to WebUI and Workers
- Create shared logging configuration service
- Implement API endpoints for log level changes in all processes

### Phase 2: Enhanced Structured Logging
- Add correlation ID propagation across all processes
- Implement consistent structured logging format
- Add process-specific metadata (type, instance, role)

### Phase 3: Service-Specific Trace Logging
- Enhance each service with comprehensive Trace logging
- Add operation context to all log messages
- Implement log correlation across services

### Phase 4: Log Management Improvements
- Add log rotation and cleanup policies
- Implement log aggregation for debugging
- Add health checks for log file availability

## Implementation Details

### 1. Shared Logging Configuration
```csharp
// Add to all Program.cs files
var levelSwitch = new LoggingLevelSwitch(LogEventLevel.Information);
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.ControlledBy(levelSwitch)
    .Enrich.WithProperty("ProcessType", "Host/WebUI/Worker")
    .Enrich.WithProperty("ProcessInstance", instanceName ?? "N/A")
    .WriteTo.Console()
    .WriteTo.File(Path.Combine(logsPath, logFileName), rollingInterval: RollingInterval.Day)
    .CreateLogger();
```

### 2. Correlation ID Propagation
- Generate UUID per request/operation
- Pass through service boundaries
- Include in all log messages

### 3. Service Trace Enhancements
- **TorrentProcessor**: Add Trace for every decision point
- **FreeSpaceService**: Add Trace for space calculations
- **SeedingService**: Add Trace for rule evaluations
- **MediaValidationService**: Add Trace for validation decisions

### 4. Log File Naming Convention
- Host: `torrentarr-{date}.log`
- WebUI: `webui-{date}.log`
- Workers: `worker-{instance}-{date}.log`

## Structured Logging Format
```json
{
  "timestamp": "2026-02-24T10:00:00Z",
  "level": "TRACE",
  "service": "TorrentProcessor",
  "operation": "torrent_processing",
  "correlation_id": "abc123",
  "torrent_id": "hash123",
  "torrent_name": "MovieName",
  "category": "Movies",
  "qbit_instance": "qBit",
  "message": "Processing torrent state transition",
  "context": {
    "from_state": "Downloading",
    "to_state": "Seeding",
    "progress": 100,
    "free_space_available": "50GB"
  }
}
```

## Files to Modify

### Core Files:
- `src/Torrentarr.Host/Program.cs` - Enhanced logging config
- `src/Torrentarr.WebUI/Program.cs` - Add LoggingLevelSwitch
- `src/Torrentarr.Workers/Program.cs` - Add LoggingLevelSwitch
- `src/Torrentarr.Infrastructure/Services/ILoggingService.cs` - New centralized logging
- `src/Torrentarr.Infrastructure/Services/LoggingService.cs` - Implementation

### Service Files (Enhanced Trace Logging):
- `src/Torrentarr.Infrastructure/Services/TorrentProcessor.cs`
- `src/Torrentarr.Infrastructure/Services/FreeSpaceService.cs`
- `src/Torrentarr.Infrastructure/Services/SeedingService.cs`
- `src/Torrentarr.Infrastructure/Services/MediaValidationService.cs`
- `src/Torrentarr.Infrastructure/Services/ArrImportService.cs`
- `src/Torrentarr.Infrastructure/Services/ArrMediaService.cs`
- `src/Torrentarr.Infrastructure/Services/ArrSyncService.cs`
- `src/Torrentarr.Infrastructure/Services/ConnectivityService.cs`

### API Endpoints:
- `src/Torrentarr.Host/Program.cs` - Add log level endpoints for Host
- `src/Torrentarr.WebUI/Program.cs` - Add log level endpoints for WebUI
- `src/Torrentarr.Workers/Program.cs` - Add log level endpoints for Workers

## Testing Strategy

### Unit Tests:
- Logging service formatting tests
- Correlation ID generation and propagation
- Structured logging format validation

### Integration Tests:
- Cross-process log correlation
- Runtime log level switching
- Log file creation and rotation

### Performance Tests:
- Log volume at Trace level
- Log file size management
- Correlation ID overhead

## Benefits

1. **Comprehensive Debugging**: Trace-level logging for every decision point
2. **Cross-Process Visibility**: Correlation IDs across all processes
3. **Runtime Control**: Dynamic log level adjustment in all processes
4. **Structured Data**: Consistent format for log aggregation and analysis
5. **Service Context**: Rich metadata for troubleshooting
6. **Performance Monitoring**: Detailed operation timing and resource usage

## Rollout Strategy

1. **Phase 1**: Implement shared logging infrastructure
2. **Phase 2**: Add correlation ID and structured logging
3. **Phase 3**: Enhance services with Trace logging
4. **Phase 4**: Add log management and cleanup
5. **Phase 5**: Testing and validation

This plan maintains the existing per-process log file structure while significantly enhancing it with runtime control, structured logging, and comprehensive Trace-level logging across all services.