using Speckle.Sdk;

namespace Speckle.Converters.IfcShared.StepParsing;

public class IfcStepParsingException : SpeckleException
{
  public IfcStepParsingException() { }

  public IfcStepParsingException(string? message)
    : base(message) { }

  public IfcStepParsingException(string? message, Exception? inner)
    : base(message, inner) { }
}
