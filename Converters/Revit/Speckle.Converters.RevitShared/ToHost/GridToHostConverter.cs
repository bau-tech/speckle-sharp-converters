using Microsoft.Extensions.Logging;
using Speckle.Converters.Common;
using Speckle.Converters.Common.Objects;
using Speckle.Converters.RevitShared.Settings;
using Speckle.Sdk.Common.Exceptions;
using Speckle.Sdk.Models;

namespace Speckle.Converters.RevitShared.ToHost;

public class GridToHostConverter : ITypedConverter<Base, DB.Element>
{
  private readonly IConverterSettingsStore<RevitConversionSettings> _settingsStore;
  private readonly ITypedConverter<SOG.Line, DB.Line> _lineConverter;
  private readonly ITypedConverter<SOG.Arc, DB.Arc> _arcConverter;
  private readonly ILogger<GridToHostConverter> _logger;

  public GridToHostConverter(
    IConverterSettingsStore<RevitConversionSettings> settingsStore,
    ITypedConverter<SOG.Line, DB.Line> lineConverter,
    ITypedConverter<SOG.Arc, DB.Arc> arcConverter,
    ILogger<GridToHostConverter> logger
  )
  {
    _settingsStore = settingsStore;
    _lineConverter = lineConverter;
    _arcConverter = arcConverter;
    _logger = logger;
  }

  public DB.Element Convert(Base target)
  {
    var doc = _settingsStore.Current.Document;

    DB.Grid grid = target["location"] switch
    {
      SOG.Line line => DB.Grid.Create(doc, _lineConverter.Convert(line)),
      SOG.Arc arc => DB.Grid.Create(doc, _arcConverter.Convert(arc)),
      _ => throw new ConversionException("Native Grid requires a line or arc location."),
    };

    if (target["name"] is string name && !string.IsNullOrWhiteSpace(name))
    {
      try
      {
        grid.Name = name;
      }
      catch (Autodesk.Revit.Exceptions.ArgumentException ex)
      {
        // Grid names must be unique in the document - keep the auto-generated name on collision.
        _logger.LogWarning(ex, "Could not rename Grid to '{Name}' - name already exists, keeping auto-generated name.", name);
      }
    }

    return grid;
  }

  public object Convert(object target) => Convert((Base)target);
}
