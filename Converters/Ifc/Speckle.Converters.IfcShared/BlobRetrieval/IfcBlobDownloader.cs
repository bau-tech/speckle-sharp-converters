using Microsoft.Extensions.Logging;
using Speckle.Connectors.Common.Operations;
using Speckle.InterfaceGenerator;
using Speckle.Sdk.Api;

namespace Speckle.Converters.IfcShared.BlobRetrieval;

/// <summary>
/// Downloads the original uploaded file for a given blob id, to a local temp path.
/// </summary>
[GenerateAutoInterface]
public class IfcBlobDownloader(IClientFactory clientFactory, ILogger<IfcBlobDownloader> logger) : IIfcBlobDownloader
{
  /// <summary>
  /// Downloads the blob to a fresh temp file.
  /// </summary>
  /// <returns>The local path of the downloaded file, or <c>null</c> on any failure - never throws.</returns>
  public async Task<string?> TryDownload(ReceiveInfo receiveInfo, string blobId, CancellationToken cancellationToken)
  {
    string targetPath = Path.Combine(Path.GetTempPath(), "speckle-ifc", $"{blobId}.ifc");

    try
    {
      string? directory = Path.GetDirectoryName(targetPath);
      if (!string.IsNullOrEmpty(directory))
      {
        Directory.CreateDirectory(directory);
      }

      using var client = clientFactory.Create(receiveInfo.Account);
      await client
        .FileImport.DownloadFile(receiveInfo.ProjectId, blobId, targetPath, null, cancellationToken)
        .ConfigureAwait(false);

      return targetPath;
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
      // Soft-fail by design: this is an enrichment step, never a reason to block a receive.
      logger.LogWarning(
        ex,
        "IfcBlobDownloader: failed to download blob {BlobId} for project {ProjectId} - falling back to DirectShape for any objects that would have been enriched.",
        blobId,
        receiveInfo.ProjectId
      );
      return null;
    }
  }
}
