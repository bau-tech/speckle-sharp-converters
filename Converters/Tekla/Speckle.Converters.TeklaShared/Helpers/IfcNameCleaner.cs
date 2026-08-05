namespace Speckle.Converters.TeklaShared.Helpers;

/// <summary>
/// Strips the trailing Revit-IFC-export instance tag from an element's Name, e.g. turning
/// "IPE Träger:IPE 330:2437434" into "IPE Träger:IPE 330". Revit's default IFC exporter appends
/// ":{ElementId}" to every Name - confirmed directly in a real export (IfcBeam/IfcColumn/IfcWall/
/// IfcSlab Name = "{Family}:{Type}:{Tag}", while the SAME element's ObjectType, used elsewhere as
/// ifcTypeName, is already the clean "{Family}:{Type}" with no tag). That tag is a meaningless,
/// ever-changing internal element id with no value as a Tekla part name, and makes every re-export of
/// the same element look like a differently-named part. Only strips a trailing ":digits" segment
/// specifically (never guesses at any other naming convention) - a name that doesn't end in a bare
/// numeric tag (custom-renamed elements, non-Revit exporters) passes through completely unchanged.
/// </summary>
public static class IfcNameCleaner
{
  public static string StripTrailingTag(string name)
  {
    int lastColon = name.LastIndexOf(':');
    if (lastColon < 0 || lastColon == name.Length - 1)
    {
      return name;
    }

    string suffix = name[(lastColon + 1)..];
    return suffix.Length > 0 && suffix.All(char.IsDigit) ? name[..lastColon] : name;
  }
}
