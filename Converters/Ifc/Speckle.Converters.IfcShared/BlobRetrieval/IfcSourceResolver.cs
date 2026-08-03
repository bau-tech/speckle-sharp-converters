using Microsoft.Extensions.Logging;
using Speckle.Connectors.Common.Operations;
using Speckle.InterfaceGenerator;
using Speckle.Sdk.Api;
using Speckle.Sdk.Api.GraphQL.Enums;
using Speckle.Sdk.Api.GraphQL.Models;

namespace Speckle.Converters.IfcShared.BlobRetrieval;

/// <summary>
/// Traces a received version back to the file-import job that produced it, giving access to the
/// original uploaded file's blob id via <see cref="FileImport.id"/>.
/// </summary>
/// <remarks>
/// Targets the legacy <c>file_uploads</c>-backed <c>FileImportResource</c> API (server &gt;= 2.25.8),
/// not the newer <c>ModelIngestionResource</c> (server &gt;= 3.0.3) - confirmed against a real
/// self-hosted 2.31.14 server that the legacy table is what's actually populated there. A server
/// running &gt;= 3.0.3 exclusively (no legacy file_uploads rows) would need a ModelIngestionResource
/// equivalent added alongside this, which is out of scope for this prototype.
/// </remarks>
[GenerateAutoInterface]
public class IfcSourceResolver(IClientFactory clientFactory, ILogger<IfcSourceResolver> logger) : IIfcSourceResolver
{
  private const int PAGE_SIZE = 25;

  /// <summary>
  /// Finds the successfully-converted file-import job whose result is <paramref name="receiveInfo"/>'s
  /// selected version, and returns its blob id (<see cref="FileImport.id"/>).
  /// </summary>
  /// <returns>The blob id, or <c>null</c> if no matching, successful job was found - never throws.</returns>
  public async Task<string?> TryResolveBlobId(ReceiveInfo receiveInfo, CancellationToken cancellationToken)
  {
    try
    {
      using var client = clientFactory.Create(receiveInfo.Account);

      string? cursor = null;
      do
      {
        var page = await client
          .FileImport.GetModelFileImportJobs(
            receiveInfo.ProjectId,
            receiveInfo.ModelId,
            PAGE_SIZE,
            cursor,
            cancellationToken
          )
          .ConfigureAwait(false);

        var match = page.items.FirstOrDefault(job => job.convertedVersionId == receiveInfo.SelectedVersionId);
        if (match is not null)
        {
          if (match.convertedStatus != (int)FileUploadConversionStatus.Success)
          {
            logger.LogInformation(
              "IfcSourceResolver: found file-import job {JobId} for version {VersionId} but its status is {Status}, not Success - treating as no source file available.",
              match.id,
              receiveInfo.SelectedVersionId,
              (FileUploadConversionStatus)match.convertedStatus
            );
            return null;
          }

          return match.id;
        }

        cursor = page.cursor;
      } while (cursor is not null);

      logger.LogInformation(
        "IfcSourceResolver: no file-import job found for version {VersionId} in model {ModelId} - this version likely wasn't produced by a file upload (e.g. an ordinary Speckle send).",
        receiveInfo.SelectedVersionId,
        receiveInfo.ModelId
      );
      return null;
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
      // Soft-fail by design: this is an enrichment step, never a reason to block a receive.
      logger.LogWarning(
        ex,
        "IfcSourceResolver: failed to resolve a file-import job for version {VersionId} - falling back to DirectShape for any objects that would have been enriched.",
        receiveInfo.SelectedVersionId
      );
      return null;
    }
  }
}
