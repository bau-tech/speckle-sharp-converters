using Speckle.Converters.IfcShared.Geometry;
using Speckle.Converters.IfcShared.StepParsing;

namespace Speckle.Converters.IfcShared.Extraction;

/// <summary>
/// Resolves the project's declared length unit (<c>IfcProject.UnitsInContext</c> -&gt;
/// <c>IfcUnitAssignment</c>) to a single scale-to-millimetres factor. Added after a live receive
/// against a real ArchiCAD-exported file confirmed its length unit is <c>METRE</c>, not
/// <c>MILLIMETRE</c> - an assumption every extractor in this project silently made until now (the
/// Revit and Tekla files this feature was originally built against both happen to use millimetre, so
/// it was never actually wrong until a third tool's file was checked).
/// </summary>
/// <remarks>
/// This factor is applied EXACTLY ONCE, by <c>RevitNativeSchemaEnricher</c>, to each fully-composed
/// world value (a placement-transformed point, or a raw dimension like a wall's thickness/depth) - it
/// is deliberately NOT threaded through any of the STEP-parsing/placement-composition code
/// (<see cref="IfcGeometryReaders"/>, <see cref="IfcPlacement"/>, every other extractor here), which
/// keeps working entirely in the file's own raw units throughout. This is safe, not an approximation:
/// every placement composition in this project is an affine transform built from unit-length basis
/// vectors (<c>Origin + XAxis*x + YAxis*y + ZAxis*z</c> - see <c>IfcPlacement.TransformPoint</c>).
/// <c>Origin</c> and the local <c>x</c>/<c>y</c>/<c>z</c> inputs are the only length-valued quantities
/// involved, so multiplying the whole composed result by a constant scale is identical to scaling
/// every one of those inputs individually first - scaling the final output once is mathematically
/// equivalent to (and far less invasive than) threading a scale factor through every intermediate
/// Origin/local-coordinate read across the entire extraction pipeline.
/// </remarks>
public static class IfcUnitResolver
{
  private const int MAX_UNIT_RESOLUTION_DEPTH = 8;

  /// <summary>
  /// Resolves the scale factor to convert the file's own raw length values to millimetres. Defaults to
  /// <c>1.0</c> (this project's original hardcoded assumption) if the length unit can't be resolved for
  /// any reason - a graceful degrade back to prior behavior for a file with no readable
  /// <c>IfcProject</c>/<c>IfcUnitAssignment</c>, not a harder failure that would disable enrichment
  /// entirely.
  /// </summary>
  public static double ResolveLengthScaleToMillimeters(StepGraph graph) =>
    TryResolveLengthScaleToMillimeters(graph, out double scale) ? scale : 1.0;

  private static bool TryResolveLengthScaleToMillimeters(StepGraph graph, out double scale)
  {
    scale = 1.0;

    if (
      !TryFindProjectUnitAssignmentId(graph, out uint unitAssignmentId)
      || !graph.Lookup.TryGetValue(unitAssignmentId, out var assignmentNode)
      || !assignmentNode.Entity.IsEntityType("IFCUNITASSIGNMENT")
      || assignmentNode.Entity.Count == 0
      || assignmentNode.Entity[0] is not StepList units
    )
    {
      return false;
    }

    // IfcUnitAssignment's Units is a heterogeneous set (length/area/volume/mass/... units) - find
    // whichever one is the LENGTHUNIT, ignore the rest.
    foreach (var unitValue in units.Values)
    {
      if (IfcGeometryReaders.AsId(unitValue) is { } unitId && TryResolveLengthUnitScale(graph, unitId, 0, out scale))
      {
        return true;
      }
    }

    return false;
  }

  // IfcProject is normally the only instance of its type in a file - scan for it directly rather than
  // assuming a fixed express id, since this varies per file/exporter.
  private static bool TryFindProjectUnitAssignmentId(StepGraph graph, out uint unitAssignmentId)
  {
    unitAssignmentId = 0;

    foreach (var node in graph.Nodes)
    {
      if (!node.Entity.IsEntityType("IFCPROJECT"))
      {
        continue;
      }

      // IfcProject(GlobalId, OwnerHistory, Name, Description, ObjectType, LongName, Phase,
      // RepresentationContexts, UnitsInContext)
      if (node.Entity.Count <= 8 || IfcGeometryReaders.AsId(node.Entity[8]) is not { } id)
      {
        return false;
      }

      unitAssignmentId = id;
      return true;
    }

    return false;
  }

  private static bool TryResolveLengthUnitScale(StepGraph graph, uint unitId, int depth, out double scale)
  {
    scale = 1.0;

    if (depth >= MAX_UNIT_RESOLUTION_DEPTH || !graph.Lookup.TryGetValue(unitId, out var node))
    {
      return false;
    }

    if (node.Entity.IsEntityType("IFCSIUNIT"))
    {
      // IfcSIUnit(Dimensions=*, UnitType, Prefix, Name)
      if (
        node.Entity.Count < 4
        || node.Entity[1] is not StepSymbol unitType
        || !unitType.Name.ToString().Equals("LENGTHUNIT", StringComparison.OrdinalIgnoreCase)
      )
      {
        return false;
      }

      string? prefix = node.Entity[2] is StepSymbol prefixSymbol ? prefixSymbol.Name.ToString() : null;
      scale = SiPrefixToMillimetreScale(prefix);
      return true;
    }

    if (node.Entity.IsEntityType("IFCCONVERSIONBASEDUNIT"))
    {
      // IfcConversionBasedUnit(Dimensions, UnitType, Name, ConversionFactor)
      if (
        node.Entity.Count < 4
        || node.Entity[1] is not StepSymbol unitType
        || !unitType.Name.ToString().Equals("LENGTHUNIT", StringComparison.OrdinalIgnoreCase)
        || IfcGeometryReaders.AsId(node.Entity[3]) is not { } measureWithUnitId
        || !graph.Lookup.TryGetValue(measureWithUnitId, out var measureNode)
        || !measureNode.Entity.IsEntityType("IFCMEASUREWITHUNIT")
        || measureNode.Entity.Count < 2
      )
      {
        return false;
      }

      // IfcMeasureWithUnit(ValueComponent, UnitComponent) - ValueComponent is an inline defined-type
      // measure (e.g. IFCLENGTHMEASURE(304.8) or IFCRATIOMEASURE(304.8), tool-dependent), parsed the
      // same way IfcBoundaryExtractor's TryReadParameterAngle handles IFCPARAMETERVALUE. UnitComponent
      // is the unit that ValueComponent is itself expressed in - recurse to resolve its own scale (e.g.
      // Tekla's 'FOOT' unit is defined as 304.8 [of the SI millimetre unit]).
      if (
        measureNode.Entity[0] is not StepEntity valueEntity
        || valueEntity.Attributes.Values.Count == 0
        || !IfcGeometryReaders.TryReadNumber(valueEntity.Attributes.Values[0], out double conversionValue)
        || IfcGeometryReaders.AsId(measureNode.Entity[1]) is not { } baseUnitId
        || !TryResolveLengthUnitScale(graph, baseUnitId, depth + 1, out double baseScale)
      )
      {
        return false;
      }

      scale = conversionValue * baseScale;
      return true;
    }

    // e.g. IfcDerivedUnit/IfcMonetaryUnit reached by mistake (not a length unit at all) - never guessed.
    return false;
  }

  private static double SiPrefixToMillimetreScale(string? prefix) =>
    prefix?.ToUpperInvariant() switch
    {
      "MILLI" => 1.0,
      "CENTI" => 10.0,
      "DECI" => 100.0,
      "KILO" => 1_000_000.0,
      null or "" => 1000.0, // no prefix - the base SI length unit, METRE
      // An exotic/unrecognized prefix (MEGA, MICRO, NANO, ...) - vanishingly unlikely for a real
      // building's length unit; fall back to METRE-scale rather than silently guessing a specific
      // factor for something this rare.
      _ => 1000.0,
    };
}
