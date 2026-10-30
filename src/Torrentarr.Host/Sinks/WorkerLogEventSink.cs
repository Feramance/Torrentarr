using Torrentarr.Infrastructure.Logging;

namespace Torrentarr.Host.Sinks;

public sealed class WorkerLogEventSink(string logsPath) : ServiceLogSink(logsPath, "Host");
