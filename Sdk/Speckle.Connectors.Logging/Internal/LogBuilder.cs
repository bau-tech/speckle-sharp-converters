using System.Text;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Resources;
using Serilog;
using Serilog.Exceptions;

namespace Speckle.Connectors.Logging.Internal;

internal static class LogBuilder
{
  public static LoggerProvider Initialize(
    string applicationAndVersion,
    string connectorVersion,
    SpeckleLogging? speckleLogging,
    ResourceBuilder resourceBuilder
  )
  {
    var factory = LoggerFactory.Create(loggingBuilder =>
    {
      if (speckleLogging != null)
      {
        loggingBuilder.SetMinimumLevel(SpeckleLogLevelUtility.GetMicrosoftLevel(speckleLogging.MinimumLevel));
        if (speckleLogging.File is not null || speckleLogging.Console)
        {
          var serilogLogConfiguration = new LoggerConfiguration()
            .MinimumLevel.Is(SpeckleLogLevelUtility.GetSerilogLevel(speckleLogging.MinimumLevel))
            .Enrich.FromLogContext()
            .Enrich.WithExceptionDetails();

          if (speckleLogging.File is not null)
          {
            // TODO: check if we have write permissions to the file.
            var logFilePath = SpecklePathProvider.LogFolderPath(applicationAndVersion);
            logFilePath = Path.Combine(logFilePath, speckleLogging.File.Path ?? "SpeckleCoreLog.txt");
            // shared: true — some hosts (e.g. Tekla's panel) re-run Connector.Initialize per dialog open/close,
            // creating a fresh Serilog logger pointed at the same file each time. Without `shared`, the new
            // sink's non-coordinated open silently clobbers/truncates whatever the previous instance wrote
            // (including the very entries we need to debug a receive). `shared` makes Serilog use a
            // mutex-coordinated sink that always appends, safe across multiple same-process logger instances.
            serilogLogConfiguration = serilogLogConfiguration.WriteTo.File(
              logFilePath,
              rollingInterval: RollingInterval.Day,
              retainedFileCountLimit: 10,
              shared: true
            );
          }

          if (speckleLogging.Console)
          {
            serilogLogConfiguration.WriteTo.Console();
          }

          var serilogLogger = serilogLogConfiguration.CreateLogger();

          // Without this, `serilogLogger` is a standalone Serilog logger that nothing ever routes
          // ILogger<T> calls through — every _logger.LogInformation/LogError/etc. in the app goes to
          // a LoggerFactory with no Serilog provider attached and silently disappears. AddSerilog wires
          // it in as an ILoggerProvider so DI-resolved ILogger<T> instances actually reach the file/console sinks.
          loggingBuilder.AddSerilog(serilogLogger, dispose: true);

          if (speckleLogging.File is not null)
          {
            serilogLogger
              .ForContext("applicationAndVersion", applicationAndVersion)
              .ForContext("connectorVersion", connectorVersion)
              .ForContext("userApplicationDataPath", SpecklePathProvider.UserApplicationDataPath())
              .ForContext("installApplicationDataPath", SpecklePathProvider.InstallApplicationDataPath)
              .Information(
                "Initialized logger inside {applicationAndVersion}/{connectorVersion}. Path info {userApplicationDataPath} {installApplicationDataPath}."
              );
          }
        }
      }

      foreach (var otel in speckleLogging?.Otel ?? [])
      {
        InitializeOtelLogging(loggingBuilder, otel, resourceBuilder);
      }
    });

    return new LoggerProvider(factory);
  }

  private static void InitializeOtelLogging(
    ILoggingBuilder loggingBuilder,
    SpeckleOtelLogging speckleOtelLogging,
    ResourceBuilder resourceBuilder
  ) =>
    loggingBuilder.AddOpenTelemetry(x =>
    {
      x.IncludeScopes = true;
      x.AddOtlpExporter(y =>
        {
          y.Protocol = OtlpExportProtocol.HttpProtobuf;
          y.Endpoint = speckleOtelLogging.Endpoint is null
            ? throw new InvalidOperationException("Need a logging endpoint")
            : speckleOtelLogging.Endpoint;
          var sb = new StringBuilder();
          bool appendSemicolon = false;
          foreach (var kvp in speckleOtelLogging.Headers ?? [])
          {
            sb.Append(kvp.Key).Append('=').Append(kvp.Value);
            if (appendSemicolon)
            {
              sb.Append(',');
            }
            else
            {
              appendSemicolon = true;
            }
          }
          y.Headers = sb.ToString();
        })
        .AddProcessor(new ActivityScopeLogProcessor())
        .SetResourceBuilder(resourceBuilder);
    });
}
