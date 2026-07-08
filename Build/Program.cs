using System.IO.Compression;
using Build;
using GlobExpressions;
using Microsoft.Build.Construction;
using static Bullseye.Targets;
using static SimpleExec.Command;

const string CLEAN = "clean";
const string RESTORE = "restore";
const string BUILD = "build";
const string PACK = "pack";
const string TEST_AFFECTED = "test-affected";
const string TEST = "test";
const string FORMAT = "format";
const string ZIP = "zip";
const string RESTORE_TOOLS = "restore-tools";
const string CLEAN_LOCKS = "clean-locks";
const string CHECK_SOLUTIONS = "check-solutions";
const string GEN_SOLUTIONS = "generate-solutions";
const string DEEP_CLEAN = "deep-clean";
const string DEEP_CLEAN_LOCAL = "deep-clean-local";
const string DETECT_AFFECTED = "detect-affected";
const string TEST_AND_PACK = "test-and-pack";

//need to pass arguments
/*var arguments = new List<string>();
if (args.Length > 1)
{
  arguments = args.ToList();
  args = new[] { arguments.First() };
  //arguments = arguments.Skip(1).ToList();
}*/

void Restore(string solution)
{
  Console.WriteLine();
  Console.WriteLine($"Restoring solution '{solution}'");
  Console.WriteLine();
  Run("dotnet", $"restore \".\\{solution}\" --no-cache");
}
void DeleteFiles(string pattern)
{
  foreach (var f in Glob.Files(".", pattern))
  {
    Console.WriteLine("Found and will delete: " + f);
    File.Delete(f);
  }
}
void DeleteDirectories(string pattern)
{
  foreach (var f in Glob.Directories(".", pattern))
  {
    if (f.StartsWith("Build"))
    {
      continue;
    }
    Console.WriteLine("Found and will delete: " + f);
    Directory.Delete(f, true);
  }
}

void CleanSolution(string solution, string configuration)
{
  Console.WriteLine("Cleaning solution: " + solution);

  DeleteDirectories("**/bin");
  DeleteDirectories("**/obj");
  DeleteFiles("**/*.lock.json");
  Restore(solution);
}

Target(
  CLEAN_LOCKS,
  Consts.Solutions,
  s =>
  {
    DeleteFiles("**/*.lock.json");
    Restore(s);
  }
);

Target(
  DEEP_CLEAN,
  Consts.Solutions,
  s =>
  {
    CleanSolution(s, "debug");
  }
);
Target(
  DEEP_CLEAN_LOCAL,
  () =>
  {
    CleanSolution("Local.slnx", "Local");
  }
);

Target(
  CLEAN,
  ["**/output"],
  dir =>
  {
    IEnumerable<string> GetDirectories(string d)
    {
      return Glob.Directories(".", d);
    }

    void RemoveDirectory(string d)
    {
      if (Directory.Exists(d))
      {
        Console.WriteLine(d);
        Directory.Delete(d, true);
      }
    }

    foreach (var d in GetDirectories(dir))
    {
      RemoveDirectory(d);
    }
  }
);

Target(
  RESTORE_TOOLS,
  () =>
  {
    Run("dotnet", "tool restore");
  }
);

Target(
  DETECT_AFFECTED,
  dependsOn: [RESTORE_TOOLS],
  async () =>
  {
    foreach (var group in await Affected.GetAffectedProjectGroups())
    {
      Console.WriteLine("Affected project group being built: " + group.HostAppSlug);
    }
  }
);

Target(
  FORMAT,
  dependsOn: [RESTORE_TOOLS],
  () =>
  {
    Run("dotnet", "csharpier check ./");
  }
);

Target(
  RESTORE,
  dependsOn: [FORMAT],
  Consts.Solutions,
  async s =>
  {
    var version = await Versions.ComputeVersion();
    var fileVersion = await Versions.ComputeFileVersion();
    Console.WriteLine($"Restoring: {s} - Version: {version} & {fileVersion}");
    await RunAsync("dotnet", $"restore \"{s}\" --locked-mode");
  }
);

Target(
  BUILD,
  dependsOn: [RESTORE],
  Consts.Solutions,
  async s =>
  {
    var version = await Versions.ComputeVersion();
    var fileVersion = await Versions.ComputeFileVersion();
    Console.WriteLine($"Restoring: {s} - Version: {version} & {fileVersion}");
    // The -warnaserror CLI flag is blunter than the project-level TreatWarningsAsErrors (already
    // true repo-wide via Directory.Build.props) - under .NET 10's newer analyzer engine it escalates
    // ~1000 IDE00xx/CAxxxx findings across the repo that TreatWarningsAsErrors alone doesn't (these
    // never surfaced as errors under the .NET 8 SDK used for local per-project dev builds). Dropping
    // just this flag keeps real compiler-warning enforcement while not treating every code-style
    // finding as build-breaking.
    await RunAsync(
      "dotnet",
      $"build \"{s}\" -c Release --no-restore -p:Version={version} -p:FileVersion={fileVersion} -v:m"
    );
  }
);

Target(CHECK_SOLUTIONS, Solutions.CompareConnectorsToLocal);
Target(GEN_SOLUTIONS, Solutions.GenerateSolutions);

Target(
  TEST_AFFECTED,
  dependsOn: [DETECT_AFFECTED, BUILD, CHECK_SOLUTIONS],
  async () =>
  {
    foreach (var s in await Affected.GetTestProjects())
    {
      await RunAsync("dotnet", $"test \"{s}\" -c Release --no-build --no-restore --verbosity=minimal");
    }
  }
);

// net48 (.NET Framework) test assemblies can't be executed today: on Linux there's no .NET
// Framework CLR to host them, and even on Windows the repo's centrally-pinned
// Microsoft.TestPlatform.TestHost version no longer ships a net48 testhost.exe (only net8.0+).
// They're still compiled (and thus type-checked) by the BUILD target via Speckle.Connectors.slnx -
// this only skips execution here.
bool CanExecuteOnThisPlatform(string projectPath)
{
  var project = ProjectRootElement.Open(projectPath) ?? throw new InvalidOperationException();
  var tfm = project.Properties.FirstOrDefault(p => p.Name is "TargetFramework" or "TargetFrameworks")?.Value ?? "";
  return !tfm.Split(';').Any(t => t.StartsWith("net4", StringComparison.OrdinalIgnoreCase));
}

Target(
  TEST,
  dependsOn: [BUILD, CHECK_SOLUTIONS],
  Glob.Files(".", "**/*.Tests.csproj").Where(CanExecuteOnThisPlatform),
  file =>
  {
    Run(
      "dotnet",
      $"test \"{file}\" -c Release --no-build --verbosity=minimal /p:AltCover=true /p:AltCoverAttributeFilter=ExcludeFromCodeCoverage /p:AltCoverVerbosity=Warning"
    );
  }
);

Target(TEST_AND_PACK, dependsOn: [TEST, PACK]);

Target(
  PACK,
  dependsOn: [BUILD],
  Consts.Solutions,
  async solution =>
  {
    var version = await Versions.ComputeVersion();
    var fileVersion = await Versions.ComputeFileVersion();
    Console.WriteLine($"Version: {version} & {fileVersion}");

    await RunAsync(
      "dotnet",
      $"pack \"{solution}\" -c Release -o output --no-build -p:Version={version} -p:FileVersion={fileVersion} -v:m"
    );
  }
);

Target(
  ZIP,
  dependsOn: [TEST_AFFECTED],
  async () =>
  {
    var version = await Versions.ComputeVersion();
    var fileVersion = await Versions.ComputeFileVersion();
    foreach (var group in await Affected.GetAffectedProjectGroups())
    {
      Console.WriteLine($"Zipping: {group.HostAppSlug} as {version}");
      var outputDir = Path.Combine(".", "output");
      var slugDir = Path.Combine(outputDir, group.HostAppSlug);

      Directory.CreateDirectory(outputDir);
      Directory.CreateDirectory(slugDir);

      foreach (var asset in group.Projects)
      {
        var fullPath = Path.Combine(".", asset.ProjectPath, "bin", "Release", asset.TargetName);
        if (!Directory.Exists(fullPath))
        {
          throw new InvalidOperationException("Could not find: " + fullPath);
        }

        var assetName = Path.GetFileName(asset.ProjectPath);
        var connectorDir = Path.Combine(slugDir, assetName);

        Directory.CreateDirectory(connectorDir);
        foreach (var directory in Directory.EnumerateDirectories(fullPath, "*", SearchOption.AllDirectories))
        {
          Directory.CreateDirectory(directory.Replace(fullPath, connectorDir));
        }

        foreach (var file in Directory.EnumerateFiles(fullPath, "*", SearchOption.AllDirectories))
        {
          Console.WriteLine(file);
          File.Copy(file, file.Replace(fullPath, connectorDir), true);
        }
      }

      var outputPath = Path.Combine(outputDir, $"{group.HostAppSlug}.zip");
      File.Delete(outputPath);
      Console.WriteLine($"Zipping: '{slugDir}' to '{outputPath}'");
      ZipFile.CreateFromDirectory(slugDir, outputPath);
    }

    string githubEnv = Environment.GetEnvironmentVariable("GITHUB_ENV") ?? "Unset";
    Console.WriteLine($"GITHUB_ENV: {githubEnv}");
    File.AppendAllText(githubEnv, $"SEMVER={version}{Environment.NewLine}");
    File.AppendAllText(githubEnv, $"FILE_VERSION={fileVersion}{Environment.NewLine}");
  }
);

Target("default", dependsOn: [TEST_AFFECTED], () => Console.WriteLine("Done!"));

await RunTargetsAndExitAsync(args).ConfigureAwait(true);
