using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Speckle.Converters.Common;
using Speckle.Converters.Common.Registration;
using Speckle.Converters.TeklaShared.Helpers;
using Speckle.Converters.TeklaShared.Helpers.ProfileMapping;
using Speckle.Converters.TeklaShared.ToHost;
using Speckle.Converters.TeklaShared.ToSpeckle.Helpers;
using Speckle.Converters.TeklaShared.ToSpeckle.TopLevel;
using Speckle.Objects.Data;
using Speckle.Sdk;
using Speckle.Sdk.Models;
using Tekla.Structures.Datatype;

namespace Speckle.Converters.TeklaShared;

public static class ServiceRegistration
{
  public static IServiceCollection AddTeklaConverters(this IServiceCollection serviceCollection)
  {
    var converterAssembly = Assembly.GetExecutingAssembly();

    serviceCollection.AddTransient<ModelObjectToSpeckleConverter>();

    serviceCollection.AddScoped<DisplayValueExtractor>();
    serviceCollection.AddScoped<ClassPropertyExtractor>();
    serviceCollection.AddScoped<ReportPropertyExtractor>();
    serviceCollection.AddScoped<UserDefinedAttributesExtractor>();
    serviceCollection.AddScoped<PropertiesExtractor>();
    serviceCollection.AddScoped<LocationExtractor>();
    serviceCollection.AddScoped<TeklaOutgoingApplicationIdResolver>();

    serviceCollection.AddRootCommon<TeklaRootToSpeckleConverter>(converterAssembly);
    serviceCollection.AddApplicationConverters<TeklaToSpeckleUnitConverter, Distance.UnitType>(converterAssembly);
    serviceCollection.AddScoped<
      IConverterSettingsStore<TeklaConversionSettings>,
      ConverterSettingsStore<TeklaConversionSettings>
    >();

    serviceCollection.AddScoped<IRootToHostConverter, TeklaRootToHostConverter>();
    serviceCollection.AddScoped<TeklaReceiveCache>();
    serviceCollection.AddScoped<TeklaExistingBeamIndex>();
    serviceCollection.AddScoped<TeklaExistingContourPlateIndex>();
    serviceCollection.AddScoped<SubComponentToHostConverter>();
    serviceCollection.AddScoped<GeometricItemToHostConverter>();
    serviceCollection.AddScoped<ITypedConverter<Base, TSM.ModelObject>, BuiltElementToHostConverter>();
    serviceCollection.AddScoped<RevitProfileMaterialMappingProvider>();
    serviceCollection.AddScoped<TeklaCatalogValidator>();
    serviceCollection.AddScoped<ConversionWarningCollector>();

    // Register concrete types so BuiltElementToHostConverter can inject them directly.
    serviceCollection.AddScoped<BuiltElementBeamToHostConverter>();
    serviceCollection.AddScoped<BuiltElementColumnToHostConverter>();
    // Register BeamToHostConverter explicitly as its interface — FindMatchingInterface only
    // matches I{ClassName} by name so ITypedConverter<TeklaObject, TSM.Beam> is never
    // auto-discovered, causing TeklaRootToHostConverter to fail DI resolution at runtime.
    serviceCollection.AddScoped<ITypedConverter<TeklaObject, TSM.Beam>, BeamToHostConverter>();
    serviceCollection.AddScoped<ITypedConverter<TeklaObject, TSM.ContourPlate>, ContourPlateToHostConverter>();
    serviceCollection.AddScoped<ITypedConverter<TeklaObject, TSM.PolyBeam>, PolyBeamToHostConverter>();
    serviceCollection.AddScoped<ITypedConverter<TeklaObject, TSM.BentPlate>, BentPlateToHostConverter>();
    serviceCollection.AddScoped<ITypedConverter<TeklaObject, TSM.SpiralBeam>, SpiralBeamToHostConverter>();
    serviceCollection.AddScoped<ITypedConverter<TeklaObject, TSM.LoftedPlate>, LoftedPlateToHostConverter>();
    serviceCollection.AddScoped<ITypedConverter<TeklaObject, TSM.Grid>, GridToHostConverter>();
    serviceCollection.AddScoped<ITypedConverter<TeklaObject, TSM.RadialGrid>, RadialGridToHostConverter>();
    serviceCollection.AddScoped<ITypedConverter<RevitObject, TSM.ContourPlate>, RevitFloorToContourPlateConverter>();
    serviceCollection.AddScoped<ITypedConverter<RevitObject, TSM.Part>, RevitColumnBeamToTeklaBeamConverter>();
    serviceCollection.AddScoped<RevitWallToTeklaBeamConverter>();
    serviceCollection.AddScoped<RevitFoundationToTeklaConverter>();
    serviceCollection.AddScoped<RevitOpeningToBooleanPartConverter>();
    serviceCollection.AddScoped<RevitGridsToTeklaGridsConverter>();

    serviceCollection.AddMatchingInterfacesAsTransient(converterAssembly);

    return serviceCollection;
  }
}
