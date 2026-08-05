using Microsoft.Extensions.Logging;
using Speckle.Connectors.Common.Builders;
using Speckle.Connectors.Common.Conversion;
using Speckle.Connectors.Common.Operations;
using Speckle.Connectors.Common.Threading;
using Speckle.Connectors.TeklaShared.Operations.Receive.ConversionMapping;
using Speckle.Converters.Common;
using Speckle.Converters.TeklaShared;
using Speckle.Converters.TeklaShared.Helpers;
using Speckle.Converters.TeklaShared.Helpers.ProfileMapping;
using Speckle.Converters.TeklaShared.ToHost;
using Speckle.Converters.TeklaShared.ToHost.Ifc;
using Speckle.Objects.Data;
using Speckle.Sdk.Common.Exceptions;
using Speckle.Sdk.Models;
using Speckle.Sdk.Models.Collections;
using Speckle.Sdk.Pipelines.Progress;
using Tekla.Structures.Model;
using SystemTask = System.Threading.Tasks.Task;

namespace Speckle.Connectors.TeklaShared.Operations.Receive;

public class TeklaHostObjectBuilder : IHostObjectBuilder
{
  private readonly IRootToHostConverter _converter;
  private readonly Model _teklaModel;
  private readonly SubComponentToHostConverter _subComponentConverter;
  private readonly RevitGridsToTeklaGridsConverter _gridsConverter;
  private readonly IfcGridsToTeklaGridsConverter _ifcGridsConverter;
  private readonly TeklaReceiveCache _receiveCache;
  private readonly ConversionWarningCollector _warningCollector;
  private readonly ConversionMappingDialogService _mappingDialogService;
  private readonly IfcProfileMappingDialogService _ifcMappingDialogService;
  private readonly RevitProfileMaterialMappingProvider _mappingProvider;
  private readonly IfcProfileMappingProvider _ifcMappingProvider;
  private readonly IConverterSettingsStore<TeklaConversionSettings> _settingsStore;
  private readonly IThreadContext _threadContext;
  private readonly ILogger<TeklaHostObjectBuilder> _logger;
  private readonly TeklaExistingBeamIndex _existingBeamIndex;
  private readonly TeklaExistingContourPlateIndex _existingContourPlateIndex;

  public TeklaHostObjectBuilder(
    IRootToHostConverter converter,
    Model teklaModel,
    SubComponentToHostConverter subComponentConverter,
    RevitGridsToTeklaGridsConverter gridsConverter,
    IfcGridsToTeklaGridsConverter ifcGridsConverter,
    TeklaReceiveCache receiveCache,
    ConversionWarningCollector warningCollector,
    ConversionMappingDialogService mappingDialogService,
    IfcProfileMappingDialogService ifcMappingDialogService,
    RevitProfileMaterialMappingProvider mappingProvider,
    IfcProfileMappingProvider ifcMappingProvider,
    IConverterSettingsStore<TeklaConversionSettings> settingsStore,
    IThreadContext threadContext,
    ILogger<TeklaHostObjectBuilder> logger,
    TeklaExistingBeamIndex existingBeamIndex,
    TeklaExistingContourPlateIndex existingContourPlateIndex
  )
  {
    _converter = converter;
    _teklaModel = teklaModel;
    _subComponentConverter = subComponentConverter;
    _gridsConverter = gridsConverter;
    _ifcGridsConverter = ifcGridsConverter;
    _receiveCache = receiveCache;
    _warningCollector = warningCollector;
    _mappingDialogService = mappingDialogService;
    _ifcMappingDialogService = ifcMappingDialogService;
    _mappingProvider = mappingProvider;
    _ifcMappingProvider = ifcMappingProvider;
    _settingsStore = settingsStore;
    _threadContext = threadContext;
    _logger = logger;
    _existingBeamIndex = existingBeamIndex;
    _existingContourPlateIndex = existingContourPlateIndex;
  }

  [System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Maintainability",
    "CA1502",
    Justification = "Multi-pass receive orchestration (assemblies, sub-components, boolean cuts, deletions) - "
      + "genuinely sequential/branchy by nature; splitting it up is a real refactor, not addressed in this pass."
  )]
  public async Task<HostObjectBuilderResult> Build(
    Base rootObject,
    string projectName,
    string modelName,
    IProgress<CardProgress> onOperationProgressed,
    CancellationToken cancellationToken
  )
  {
    _logger.LogInformation(
      "Build started. rootObject type={RootType} speckle_type={SpeckleType}",
      rootObject?.GetType().FullName ?? "null",
      rootObject?.speckle_type ?? "null"
    );

    if (rootObject is null)
    {
      throw new ArgumentNullException(nameof(rootObject));
    }

    List<string> bakedObjectIds = new();
    List<ReceiveConversionResult> results = new();

    var speckleObjects = FlattenToAtomicObjects(rootObject).ToList();

    _logger.LogInformation("FlattenToAtomicObjects returned {Count} objects", speckleObjects.Count);
    foreach (var o in speckleObjects)
    {
      var t = o is TeklaObject to ? to.type : o.GetType().Name;
      _logger.LogDebug(
        "  flattened object: speckle_type={SpeckleType} tekla_type={TeklaType} id={Id}",
        o.speckle_type,
        t,
        o.id
      );
    }

    // Revit family instances arrive with symbol-space geometry behind InstanceProxy references -
    // bake them into world-space meshes so geometry-based conversion (column Z extents, bbox
    // profiles, foundation sizes) sees real coordinates instead of falling back to Z~0.
    InstanceGeometryMaterializer.Materialize(rootObject, speckleObjects, _logger);

    // let the user review/edit the Revit→Tekla profile & material mapping before anything is baked
    await ShowConversionMappingDialogIfNeeded(rootObject, speckleObjects);
    // same, for IFC-sourced elements the enricher couldn't already resolve a profile for
    await ShowIfcProfileMappingDialogIfNeeded(speckleObjects);

    // ── Pass 0: Revit Grids -> native Tekla grid systems ────────────────
    // A single Revit Grid line has no standalone Tekla equivalent - it's only meaningful as one
    // entry in a shared Grid/RadialGrid, so these are converted as a batch, up front, and excluded
    // from Pass 1's per-object dispatch below.
    var gridObjects = speckleObjects.OfType<RevitObject>().Where(IsGrid).ToList();
    if (gridObjects.Count > 0)
    {
      foreach (var outcome in _gridsConverter.Convert(gridObjects))
      {
        if (outcome.Result is ModelObject mo)
        {
          _logger.LogInformation("  Pass0 grid SUCCESS id={Id} -> {HostType}", outcome.Source.id, mo.GetType().Name);
          bakedObjectIds.Add(mo.Identifier.GUID.ToString());
          results.Add(
            new ReceiveConversionResult(
              Status.SUCCESS,
              outcome.Source,
              mo.Identifier.GUID.ToString(),
              mo.GetType().Name
            )
          );
        }
        else
        {
          _logger.LogWarning("  Pass0 grid SKIPPED id={Id}: {Warning}", outcome.Source.id, outcome.Warning);
          results.Add(
            new ReceiveConversionResult(
              Status.ERROR,
              outcome.Source,
              null,
              null,
              new ConversionException(outcome.Warning ?? "Grid not converted.")
            )
          );
        }
      }
    }

    // ── Pass 0b: IFC-enriched grid axes -> native Tekla grid systems ────
    // Mirrors Pass 0 above for IFC-sourced grid DataObjects (RevitNativeSchemaEnricher.TryEnrichGrid
    // synthesizes one DataObject per axis, shared with the Revit connector's IFC feature - see
    // IfcGridsToTeklaGridsConverter's remarks). Excluded from Pass 1's per-object dispatch below.
    var ifcGridObjects = speckleObjects.OfType<DataObject>().Where(IsIfcGrid).ToList();
    if (ifcGridObjects.Count > 0)
    {
      foreach (var outcome in _ifcGridsConverter.Convert(ifcGridObjects))
      {
        if (outcome.Result is ModelObject mo)
        {
          _logger.LogInformation(
            "  Pass0b IFC grid SUCCESS id={Id} -> {HostType}",
            outcome.Source.id,
            mo.GetType().Name
          );
          bakedObjectIds.Add(mo.Identifier.GUID.ToString());
          results.Add(
            new ReceiveConversionResult(
              Status.SUCCESS,
              outcome.Source,
              mo.Identifier.GUID.ToString(),
              mo.GetType().Name
            )
          );
        }
        else
        {
          _logger.LogWarning("  Pass0b IFC grid SKIPPED id={Id}: {Warning}", outcome.Source.id, outcome.Warning);
          results.Add(
            new ReceiveConversionResult(
              Status.ERROR,
              outcome.Source,
              null,
              null,
              new ConversionException(outcome.Warning ?? "Grid not converted.")
            )
          );
        }
      }
    }

    // ── Pass 1: Build Main Parts ─────────────────────────────────────────
    var assemblyGroups = new Dictionary<string, List<(bool isMainPart, Part part)>>();
    int count = 0;
    foreach (var speckleObject in speckleObjects)
    {
      cancellationToken.ThrowIfCancellationRequested();
      if (
        IsSubComponent(speckleObject)
        || (speckleObject is RevitObject ro && IsGrid(ro))
        || (speckleObject is DataObject ifcGridCandidate && IsIfcGrid(ifcGridCandidate))
      )
      {
        _logger.LogDebug("  Pass1 skip sub-component/grid type={Type}", (speckleObject as TeklaObject)?.type);
        count++;
        onOperationProgressed.Report(new CardProgress("Building Main Parts", (double)count / speckleObjects.Count));
        continue;
      }

      var teklaType = speckleObject is TeklaObject tko ? tko.type : speckleObject.GetType().Name;
      _logger.LogInformation("  Pass1 converting type={Type} id={Id}", teklaType, speckleObject.id);

      try
      {
        var result = _converter.Convert(speckleObject);
        if (result is ModelObject mo)
        {
          _logger.LogInformation(
            "  Pass1 SUCCESS type={Type} -> {HostType} identifier={Id}",
            teklaType,
            mo.GetType().Name,
            mo.Identifier
          );
          bakedObjectIds.Add(mo.Identifier.GUID.ToString());

          var warnings = _warningCollector.Get(speckleObject.id);
          if (warnings is { Count: > 0 })
          {
            _logger.LogWarning(
              "  Pass1 WARNING type={Type} id={Id}: {Warnings}",
              teklaType,
              speckleObject.id,
              string.Join(" ", warnings)
            );
            results.Add(
              new ReceiveConversionResult(
                Status.WARNING,
                speckleObject,
                mo.Identifier.GUID.ToString(),
                mo.GetType().Name,
                new InvalidOperationException(string.Join(" ", warnings))
              )
            );
          }
          else
          {
            results.Add(
              new ReceiveConversionResult(
                Status.SUCCESS,
                speckleObject,
                mo.Identifier.GUID.ToString(),
                mo.GetType().Name
              )
            );
          }

          if (mo is Part part && speckleObject is TeklaObject to && to.properties is not null)
          {
            if (to.properties.TryGetValue("assembly_id", out var aIdObj) && aIdObj is not null)
            {
              var aId = aIdObj.ToString()!;
              var isMain = to.properties.TryGetValue("is_main_part", out var imp) && imp is true;
              if (!assemblyGroups.TryGetValue(aId, out var group))
              {
                group = new List<(bool, Part)>();
                assemblyGroups[aId] = group;
              }
              group.Add((isMain, part));
            }
          }
        }
        else
        {
          _logger.LogWarning(
            "  Pass1 converter returned non-ModelObject for type={Type}: {ResultType}",
            teklaType,
            result?.GetType().Name ?? "null"
          );
        }
      }
      catch (Exception ex) when (ex is not OperationCanceledException)
      {
        _logger.LogError(ex, "  Pass1 ERROR converting type={Type} id={Id}", teklaType, speckleObject.id);
        results.Add(new ReceiveConversionResult(Status.ERROR, speckleObject, null, null, ex));
      }

      onOperationProgressed.Report(new CardProgress("Building Main Parts", (double)++count / speckleObjects.Count));
    }

    _logger.LogInformation(
      "Pass1 done. Baked={Baked} Errors={Errors}",
      bakedObjectIds.Count,
      results.Count(r => r.Status == Status.ERROR)
    );

    // ── Pass 1.55: delete Speckle-managed beams removed at the source ──
    // A beam that has round-tripped through this receive path at least once (carries the origin-id
    // UDA - see TeklaExistingBeamIndex/TeklaOriginIdentifier) but wasn't matched by anything in THIS
    // payload was deleted upstream (e.g. removed in Revit before the resend) - delete it here too.
    // Beams that have never round-tripped are never candidates, so unrelated native Tekla content is
    // never at risk.
    foreach (var beam in _existingBeamIndex.GetDeletionCandidates())
    {
      cancellationToken.ThrowIfCancellationRequested();
      string guid = beam.Identifier.GUID.ToString();
      if (beam.Delete())
      {
        _logger.LogInformation("  Pass1.55 deleted beam {Guid} (removed at source).", guid);
      }
      else
      {
        _logger.LogWarning("  Pass1.55 failed to delete beam {Guid} (removed at source).", guid);
      }
    }

    // Mirrors the beam pass above - TSM.ContourPlate has its own index (TeklaExistingContourPlateIndex),
    // not reusable from TeklaExistingBeamIndex (strictly typed/scanned to TSM.Beam).
    foreach (var plate in _existingContourPlateIndex.GetDeletionCandidates())
    {
      cancellationToken.ThrowIfCancellationRequested();
      string guid = plate.Identifier.GUID.ToString();
      if (plate.Delete())
      {
        _logger.LogInformation("  Pass1.55 deleted plate {Guid} (removed at source).", guid);
      }
      else
      {
        _logger.LogWarning("  Pass1.55 failed to delete plate {Guid} (removed at source).", guid);
      }
    }

    // ── Pass 1.5: Revit Openings -> BooleanPart cuts on hosts created in Pass 1 ──
    foreach (var speckleObject in speckleObjects.OfType<RevitObject>())
    {
      cancellationToken.ThrowIfCancellationRequested();
      if (!IsOpeningCategory(speckleObject))
      {
        continue;
      }

      var hostAppId = speckleObject.properties.TryGetValue("parentApplicationId", out var hostAppIdObj)
        ? hostAppIdObj as string
        : null;
      var hostPart = _receiveCache.Get(hostAppId);
      if (hostPart is null)
      {
        _logger.LogWarning("  Pass1.5 opening host not found: hostAppId={HostAppId}", hostAppId);
        continue;
      }

      try
      {
        var cut = _converter.Convert(speckleObject);
        _logger.LogInformation("  Pass1.5 SUCCESS opening id={Id} -> {HostType}", speckleObject.id, cut.GetType().Name);
      }
      catch (Exception ex) when (ex is not OperationCanceledException)
      {
        _logger.LogWarning(ex, "  Pass1.5 failed converting opening id={Id}", speckleObject.id);
      }
    }

    // ── Pass 1.5b: IFC openings -> BooleanPart cuts on hosts created in Pass 1 ──
    // Mirrors Pass 1.5 above for the IFC-sourced synthesized opening DataObjects (shared with the
    // Revit connector's IFC feature - see IfcOpeningToBooleanPartConverter's remarks).
    foreach (var speckleObject in speckleObjects.OfType<DataObject>().Where(IsIfcOpeningCategory))
    {
      cancellationToken.ThrowIfCancellationRequested();

      var hostAppId = speckleObject.properties.TryGetValue("parentApplicationId", out var hostAppIdObj)
        ? hostAppIdObj as string
        : null;
      var hostPart = _receiveCache.Get(hostAppId);
      if (hostPart is null)
      {
        _logger.LogWarning("  Pass1.5b IFC opening host not found: hostAppId={HostAppId}", hostAppId);
        continue;
      }

      try
      {
        var cut = _converter.Convert(speckleObject);
        _logger.LogInformation(
          "  Pass1.5b SUCCESS IFC opening id={Id} -> {HostType}",
          speckleObject.id,
          cut.GetType().Name
        );
      }
      catch (Exception ex) when (ex is not OperationCanceledException)
      {
        _logger.LogWarning(ex, "  Pass1.5b failed converting IFC opening id={Id}", speckleObject.id);
      }
    }

    // ── Pass 2: Build Sub-Components (Bolts, Welds, Cuts) ───────────────
    int p2WithElements = speckleObjects.OfType<TeklaObject>().Count(t => t.elements is { Count: > 0 });
    int p2TotalChildren = speckleObjects
      .OfType<TeklaObject>()
      .Where(t => t.elements != null)
      .Sum(t => t.elements.Count);
    int p2CacheHits = 0;
    _logger.LogInformation(
      "Pass2 start: {Total} objects, {WithElements} have children, {TotalChildren} total children in elements",
      speckleObjects.Count,
      p2WithElements,
      p2TotalChildren
    );

    count = 0;
    foreach (var speckleObject in speckleObjects)
    {
      cancellationToken.ThrowIfCancellationRequested();
      if (speckleObject is TeklaObject teklaObject && teklaObject.elements is { Count: > 0 })
      {
        var id = teklaObject.id ?? teklaObject.applicationId;
        if (id != null)
        {
          var parentPart = _receiveCache.Get(id);
          if (parentPart != null)
          {
            p2CacheHits++;
            _logger.LogInformation("  Pass2 parent found id={Id} children={Count}", id, teklaObject.elements.Count);
            foreach (var child in teklaObject.elements)
            {
              _logger.LogInformation(
                "    child CLR type={ClrType} speckle_type={SpeckleType}",
                child?.GetType().Name ?? "null",
                child?.speckle_type ?? "null"
              );
              if (child is TeklaObject childTekla && IsSubComponent(childTekla))
              {
                try
                {
                  _subComponentConverter.ConvertAndAttach(childTekla, parentPart);
                  _logger.LogInformation("    ConvertAndAttach OK type={Type}", childTekla.type);
                }
#pragma warning disable CA1031
                catch (Exception ex)
                {
                  _logger.LogWarning(ex, "  Pass2 failed attaching sub-component type={Type}", childTekla.type);
                }
#pragma warning restore CA1031
              }
              else
              {
                _logger.LogInformation(
                  "    child skipped (not TeklaObject or not sub-component): type={Type}",
                  child?.type ?? child?.GetType().Name ?? "null"
                );
              }
            }
          }
          else
          {
            _logger.LogInformation(
              "  Pass2 parent NOT in cache id={Id} elements={Count}",
              id,
              teklaObject.elements.Count
            );
          }
        }
      }
      onOperationProgressed.Report(new CardProgress("Building Connections", (double)++count / speckleObjects.Count));
    }
    _logger.LogInformation("Pass2 done. Cache hits={Hits}", p2CacheHits);

    // ── Pass 2b: Orphaned Sub-Components (e.g. Welds) ───────────────────
    // Tekla's ModelObject.GetChildren() does not expose every sub-component as a
    // child of its referenced part — Welds (and sometimes BoltArrays/RebarSets)
    // never get nested inside a parent's `elements` on send. They only end up in
    // the commit when the user selects them directly, arriving as standalone
    // top-level objects that Pass1 skips and Pass2's element-walk never visits.
    // Resolve their referenced parent (main_id/mainPartId/father_id) from the
    // receive cache and attach them here.
    var nestedSubComponentIds = new HashSet<string>();
    void CollectNestedIds(IEnumerable<TeklaObject> elements)
    {
      foreach (var element in elements)
      {
        if (element.id != null)
        {
          nestedSubComponentIds.Add(element.id);
        }
        CollectNestedIds(element.elements);
      }
    }
    foreach (var rootTekla in speckleObjects.OfType<TeklaObject>())
    {
      CollectNestedIds(rootTekla.elements);
    }

    int p2bAttached = 0;
    foreach (var speckleObject in speckleObjects)
    {
      cancellationToken.ThrowIfCancellationRequested();
      if (
        speckleObject is not TeklaObject orphanCandidate
        || !IsSubComponent(orphanCandidate)
        || (orphanCandidate.id != null && nestedSubComponentIds.Contains(orphanCandidate.id))
      )
      {
        continue;
      }

      var referenceId = GetSubComponentReferenceParentId(orphanCandidate);
      var resolvedParent = _receiveCache.Get(referenceId);
      if (resolvedParent == null)
      {
        _logger.LogWarning(
          "  Pass2b orphaned sub-component type={Type} id={Id} could not resolve parent via reference id={RefId}",
          orphanCandidate.type,
          orphanCandidate.id,
          referenceId
        );
        continue;
      }

      try
      {
        _subComponentConverter.ConvertAndAttach(orphanCandidate, resolvedParent);
        p2bAttached++;
        _logger.LogInformation(
          "  Pass2b attached orphaned sub-component type={Type} id={Id} -> parent identifier={ParentId}",
          orphanCandidate.type,
          orphanCandidate.id,
          resolvedParent.Identifier
        );
      }
#pragma warning disable CA1031
      catch (Exception ex)
      {
        _logger.LogWarning(ex, "  Pass2b failed attaching orphaned sub-component type={Type}", orphanCandidate.type);
      }
#pragma warning restore CA1031
    }
    _logger.LogInformation("Pass2b done. Attached={Attached}", p2bAttached);

    // ── Pass 3: Reconstruct Multi-Part Assemblies ───────────────────────
    foreach (var group in assemblyGroups.Values)
    {
      if (group.Count <= 1)
      {
        continue;
      }

      try
      {
        var mainEntry = group.FirstOrDefault(g => g.isMainPart);
        if (mainEntry.part is null)
        {
          mainEntry = group[0];
        }

        var assembly = mainEntry.part.GetAssembly();
        if (assembly is null)
        {
          continue;
        }

        foreach (var (_, secondaryPart) in group)
        {
          if (secondaryPart == mainEntry.part)
          {
            continue;
          }
          assembly.Add(secondaryPart);
        }
        assembly.Modify();
      }
#pragma warning disable CA1031
      catch (Exception ex)
      {
        _logger.LogWarning(ex, "  Pass3 failed reconstructing assembly group");
      }
#pragma warning restore CA1031
    }

    _teklaModel.CommitChanges();

    _logger.LogInformation("Build complete. Total baked={Baked}", bakedObjectIds.Count);
    return new HostObjectBuilderResult(bakedObjectIds, results);
  }

  /// <summary>
  /// Shows the conversion-table dialog for Revit-sourced native receives: rows come from the
  /// sender's conversion table on the root object (plus a scan of the received RevitObjects for
  /// older commits), the confirmed values override the scoped mapping provider that the ToHost
  /// converters read. Cancel aborts the receive; any unexpected failure here falls back to the
  /// previous silent behavior and never blocks the bake.
  /// </summary>
  private async SystemTask ShowConversionMappingDialogIfNeeded(Base rootObject, List<Base> speckleObjects)
  {
    MappingDialogResult? result;
    try
    {
      if (_settingsStore.Current.ReceiveMode != Speckle.Converters.TeklaShared.ReceiveMode.Native)
      {
        return;
      }

      var revitObjects = speckleObjects.OfType<RevitObject>().ToList();
      if (revitObjects.Count == 0)
      {
        return;
      }

      ConversionTable? serverTable = ConversionTable.TryParse(rootObject[RootKeys.CONVERSION_TABLE], out var parsed)
        ? parsed
        : null;

      var rows = _mappingDialogService.BuildRows(serverTable, revitObjects);
      if (rows.Count == 0)
      {
        return;
      }

      result = await _threadContext.RunOnMain(() => _mappingDialogService.ShowDialog(rows));
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
      _logger.LogWarning(ex, "Conversion mapping dialog failed; continuing with the existing mapping behavior.");
      return;
    }

    if (result is null)
    {
      throw new OperationCanceledException("Receive cancelled by the user in the conversion table dialog.");
    }

    _mappingProvider.SetOverride(result.Table);
    if (result.SaveAsDefault)
    {
      _mappingProvider.TrySaveAsDefault(result.Table);
    }
  }

  /// <summary>
  /// Shows the IFC profile-mapping dialog: rows come from scanning the already-enriched IFC
  /// DataObjects (see RevitNativeSchemaEnricher, shared with the Revit connector) for elements with
  /// no auto-resolved profile at all (piles, non-standard beam/column sections). Cancel aborts the
  /// receive; any unexpected failure here falls back to the previous silent behavior (unmapped
  /// elements fall to DirectShape via StructuralFramingHelper's safety net) and never blocks the bake.
  /// </summary>
  private async SystemTask ShowIfcProfileMappingDialogIfNeeded(List<Base> speckleObjects)
  {
    IfcProfileMappingResult? result;
    try
    {
      var ifcObjects = speckleObjects
        .OfType<DataObject>()
        .Where(d => d is not RevitObject && d is not TeklaObject)
        .ToList();
      if (ifcObjects.Count == 0)
      {
        return;
      }

      var rows = _ifcMappingDialogService.BuildRows(ifcObjects);
      if (rows.Count == 0)
      {
        return;
      }

      result = await _threadContext.RunOnMain(() => _ifcMappingDialogService.ShowDialog(rows));
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
      _logger.LogWarning(ex, "IFC profile mapping dialog failed; continuing with the existing mapping behavior.");
      return;
    }

    if (result is null)
    {
      throw new OperationCanceledException("Receive cancelled by the user in the IFC profile mapping dialog.");
    }

    _ifcMappingProvider.SetOverride(result.Table);
    if (result.SaveAsDefault)
    {
      _ifcMappingProvider.TrySaveAsDefault(result.Table);
    }
  }

  private static IEnumerable<Base> FlattenToAtomicObjects(Base obj)
  {
    if (obj is TeklaObject)
    {
      yield return obj;
      yield break;
    }

    if (obj is Collection col)
    {
      foreach (var child in col.elements)
      {
        foreach (var item in FlattenToAtomicObjects(child))
        {
          yield return item;
        }
      }
      yield break;
    }

    if (obj is RevitObject revitObject)
    {
      yield return revitObject;

      // RevitObject.elements is not surfaced by the Collection branch above (RevitObject doesn't
      // inherit Collection) - but Opening children (hosted wall/floor openings, synthetic floor
      // sketch holes) are nested here and must reach Pass 1.5 to become BooleanPart cuts.
      foreach (var child in revitObject.elements)
      {
        if (IsOpeningCategory(child))
        {
          foreach (var item in FlattenToAtomicObjects(child))
          {
            yield return item;
          }
        }
      }
      yield break;
    }

    if (obj is Speckle.Objects.Geometry.Mesh)
    {
      // Raw top-level meshes (e.g. orphaned Revit instance-definition geometry not wrapped in any
      // RevitObject/TeklaObject) have no usable placement/profile data. GeometricItemToHostConverter's
      // only option for these is a hardcoded 100mm "Generic Placeholder" HEA200 stub at the origin,
      // which just clutters the model. Skip them entirely.
      yield break;
    }

    // Atomic non-TeklaObject, non-Collection, non-RevitObject Base - yield it so it reaches
    // _converter.Convert() in Pass 1.
    yield return obj;

    // BUG FIX: an IfcElementAssembly (see RevitNativeSchemaEnricher.TryEnrichElementAssembly's
    // remarks - a pure grouping container, e.g. Revit's "Trägersystem" beam-grid, skipped from
    // conversion above) can arrive with its IfcRelAggregates member parts nested as the assembly
    // DataObject's OWN dynamic children (plain DataObject has no typed .elements property - unlike
    // Collection/RevitObject above, there's no fixed property name to check), rather than as
    // independent top-level siblings - depends entirely on the IFC import's own tree shape.
    // RevitNativeSchemaEnricher's own TraverseAll walk (a SEPARATE, fully-generic traversal used
    // only for enrichment) already finds and correctly enriches those nested members regardless of
    // which property holds them - but THIS traversal is what actually decides what reaches
    // _converter.Convert(), and it had no equivalent, so they were enriched perfectly and then
    // silently never converted at all. Confirmed live: a Trägersystem's member beams were missing
    // from the received model. Mirrors TraverseAll's own generic "any Base-typed dynamic member,
    // singly or in a collection" walk rather than guessing a specific property name.
    if (obj is DataObject dataObj && dataObj["builtInCategory"] as string == "IfcElementAssembly")
    {
      foreach (var member in EnumerateBaseMembers(dataObj))
      {
        foreach (var item in FlattenToAtomicObjects(member))
        {
          yield return item;
        }
      }
    }
  }

  private static IEnumerable<Base> EnumerateBaseMembers(Base obj)
  {
    foreach (object? value in obj.GetMembers().Values)
    {
      switch (value)
      {
        case Base childBase:
          yield return childBase;
          break;
        case System.Collections.IEnumerable enumerable and not string:
          foreach (object? item in enumerable)
          {
            if (item is Base itemBase)
            {
              yield return itemBase;
            }
          }
          break;
      }
    }
  }

  private static bool IsGrid(RevitObject ro) => ro["builtInCategory"] as string == "OST_Grids";

  // A plain (non-RevitObject, non-TeklaObject) DataObject enriched/synthesized by the IFC
  // native-reconstruction feature - see IfcGridsToTeklaGridsConverter's remarks.
  private static bool IsIfcGrid(DataObject dataObject) =>
    dataObject is not RevitObject
    && dataObject is not TeklaObject
    && dataObject["builtInCategory"] as string == "OST_Grids";

  // Uses the code-based "builtInCategory" (e.g. "OST_SWallRectOpening"), not the display-name
  // "category" field (Category.Name - localized to the sending Revit's UI language, e.g. German in
  // this environment, and so unreliable for an English substring match like this one).
  private static bool IsOpeningCategory(RevitObject ro) =>
    (ro["builtInCategory"] as string ?? "").IndexOf("Opening", StringComparison.OrdinalIgnoreCase) >= 0;

  // A plain (non-RevitObject, non-TeklaObject) DataObject synthesized by the IFC opening extractor
  // (see RevitNativeSchemaEnricher.TryBuildOpeningDataObject's remarks) - the IFC-sourced mirror of
  // IsOpeningCategory above.
  private static bool IsIfcOpeningCategory(DataObject dataObject) =>
    dataObject is not RevitObject
    && dataObject is not TeklaObject
    && (dataObject["builtInCategory"] as string ?? "").IndexOf("Opening", StringComparison.OrdinalIgnoreCase) >= 0;

  private static bool IsSubComponent(Base obj)
  {
    if (obj is RevitObject ro)
    {
      // Revit Openings are sub-components conceptually - they modify their host part via a
      // boolean cut and must be converted AFTER the host exists (see Pass 1.5).
      return IsOpeningCategory(ro);
    }

    if (obj is DataObject ifcOpeningCandidate && IsIfcOpeningCategory(ifcOpeningCandidate))
    {
      // IFC-sourced mirror of the above - see Pass 1.5b.
      return true;
    }

    if (obj is TeklaObject to)
    {
      return to.type == "BoltArray"
        || to.type == "BoltCircle"
        || to.type == "BoltXY"
        || to.type == "Weld"
        || to.type == "Seam"
        || to.type == "Fitting"
        || to.type == "BooleanPart"
        || to.type == "BOOLEAN_CUT" // old streams: type collision overwrote with enum value
        || to.type == "BOOLEAN_ADD"
        || to.type == "CutPlane"
        || to.type == "EdgeChamfer"
        || to.type == "SingleRebar"
        || to.type == "RebarGroup"
        || to.type == "RebarMesh"
        || to.type == "RebarSet";
    }
    return false;
  }

  /// <summary>
  /// Looks up the captured cross-reference id (the original Tekla GUID of the part this
  /// sub-component is logically attached to) under the property key used by its capture
  /// routine in ClassPropertyExtractor — see AddWeldProperties (main_id), AddBoltGroupProperties
  /// (mainPartId), and the various father_id captures (rebar/cut-plane/edge-chamfer).
  /// </summary>
  private static string? GetSubComponentReferenceParentId(TeklaObject teklaObject)
  {
    string key = teklaObject.type switch
    {
      "Weld" or "Seam" => "main_id",
      "BoltArray" or "BoltCircle" or "BoltXY" => "mainPartId",
      _ => "father_id",
    };

    return teklaObject.properties.TryGetValue(key, out var idObj) && idObj != null ? idObj.ToString() : null;
  }
}
