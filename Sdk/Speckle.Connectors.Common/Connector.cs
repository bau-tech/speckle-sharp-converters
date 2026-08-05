using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Speckle.Connectors.Common.Common;
using Speckle.Connectors.Logging;
using Speckle.Objects.Geometry;
using Speckle.Sdk;

namespace Speckle.Connectors.Common;

public static class Connector
{
  private sealed record LoggingDisposable(IDisposable Logging, IDisposable Tracing, IDisposable Metrics) : IDisposable
  {
    public void Dispose()
    {
      Logging.Dispose();
      Tracing.Dispose();
      Metrics.Dispose();
    }
  }

  public static readonly string TabName = "Speckle";
  public static readonly string TabTitle = "Speckle";

  /// <summary>
  /// Initialises the Speckle SDK and logging for a connector.
  /// Pass <paramref name="additionalTypeAssemblies"/> for any assemblies that contain custom
  /// <see cref="Speckle.Sdk.Models.Base"/> subclasses that are NOT in Speckle.Objects —
  /// e.g. connector-specific model objects that need to be resolved by the TypeLoader.
  /// </summary>
  public static IDisposable Initialize(
    this IServiceCollection serviceCollection,
    Application application,
    HostAppVersion version,
    params Assembly[] additionalTypeAssemblies
  )
  {
    var assemblyVersion = Assembly.GetExecutingAssembly().GetVersion();
    var typeAssemblies = new[] { typeof(Point).Assembly }.Concat(additionalTypeAssemblies).ToArray();
    // Use overload without explicit Speckle version to maintain runtime compatibility with older Speckle.Sdk
    serviceCollection.AddSpeckleSdk(application, HostApplications.GetVersion(version), typeAssemblies);

    return serviceCollection.AddOpenTelemetry(
      "Connector",
      application,
      version,
#if DEBUG || LOCAL
      new SpeckleLogging(Console: true, File: new(), MinimumLevel: SpeckleLogLevel.Debug),
      new SpeckleTracing(Console: false),
      new SpeckleMetrics(Console: false)
#else
      new SpeckleLogging(
        Console: true,
        File: new(),
        Otel:
        [
          new(
            Endpoint: new Uri("https://seq.speckle.systems/ingest/otlp/v1/logs"),
            Headers: new() { { "X-Seq-ApiKey", "Y0Ya2CFVt1tCSgrbY07c" } }
          ),
        ],
        MinimumLevel: SpeckleLogLevel.Information
      ),
      new SpeckleTracing(
        Console: false,
        Otel:
        [
          new(
            Endpoint: new Uri("https://seq.speckle.systems/ingest/otlp/v1/traces"),
            Headers: new() { { "X-Seq-ApiKey", "Y0Ya2CFVt1tCSgrbY07c" } }
          ),
        ]
      ),
      null
#endif
    );
  }

  public static IDisposable AddOpenTelemetry(
    this IServiceCollection serviceCollection,
    string serviceName,
    Application application,
    HostAppVersion version,
    SpeckleLogging loggingConfig,
    SpeckleTracing? tracingConfig,
    SpeckleMetrics? metricsConfig
  )
  {
    var assemblyVersion = Assembly.GetExecutingAssembly().GetVersion();
    var (logging, tracing, metrics) = Observability.Initialize(
      serviceName,
      application.Name + " " + HostApplications.GetVersion(version),
      application.Slug,
      assemblyVersion,
      new(loggingConfig, tracingConfig, metricsConfig)
    );
    //do this after the AddSpeckleSdk so that the logging system gets values from here.
    serviceCollection.AddLogging(x =>
    {
      x.ClearProviders();
      x.AddProvider(new SpeckleLogProvider(logging));
      x.SetMinimumLevel(GetMicrosoftLevel(loggingConfig.MinimumLevel));
    });
    serviceCollection.AddSingleton<Speckle.Sdk.Logging.ISdkActivityFactory, ConnectorActivityFactory>();
    return new LoggingDisposable(logging, tracing, metrics);
  }

  private static LogLevel GetMicrosoftLevel(SpeckleLogLevel speckleLogLevel) =>
    speckleLogLevel switch
    {
      SpeckleLogLevel.Debug => LogLevel.Debug,
      SpeckleLogLevel.Verbose => LogLevel.Trace,
      SpeckleLogLevel.Information => LogLevel.Information,
      SpeckleLogLevel.Warning => LogLevel.Warning,
      SpeckleLogLevel.Error => LogLevel.Error,
      SpeckleLogLevel.Fatal => LogLevel.Critical,
      _ => throw new ArgumentOutOfRangeException(nameof(speckleLogLevel), speckleLogLevel, null),
    };
}
