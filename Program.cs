using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml;

namespace Net5ProjectMigrator
{
    class Program
    {
        private const string PKG_CONFIG_CONSTANT = "packages.config";
        private const string CSPROJ_SEARCH_CONSTANT = "*.csproj";
        private const string SCHEMA_MSBUILD = "http://schemas.microsoft.com/developer/msbuild/2003";
        private const string SCHEMA_NUGET = "http://schemas.microsoft.com/packaging/2011/08/nuspec.xsd";

        private static bool _verbose = false;
        private static bool _recurse = false;
        private static bool _whatIf = false;
        static void Main(string[] args)
        {
            //todo: build a cmdlet that calls this, allows for options like recurse, etc.
            //final step would be to call dotnet restore / build against the sln and verify successful build

            //Establish root command
            var cmd = new RootCommand
            {
                new Option<string>(
                    "--path",
                    description:"Root project path",
                    getDefaultValue: ()=> Directory.GetCurrentDirectory()
                ),
                new Option(
                    new[] {"--recurse", "-r"},
                    description: "Use recurse to update all projects in a solution folder"
                ),
                new Option(
                    new[]{"--verbose", "-v"},
                    description: "Specify using verbose output"
                ),
                new Option(
                    new[]{"--whatif", "-w"},
                    description: "Run to test migration result"
                ),
                new Option<bool>(
                    "--makePkg",
                    description: "Specify to generate package on build",
                    getDefaultValue: ()=> false
                ),
                new Option<string>(
                    "--nuspecPath",
                    description: "Specify preexisting nuspec file for package generation detais",
                    getDefaultValue: ()=> string.Empty
                )
            };

            //Set command handler
            cmd.Handler = CommandHandler.Create<string, bool, bool, bool, bool, string>((path, recurse, verbose, whatIf, makePkgOnBuild, nuspecPath) =>
            {
                StringBuilder sb = new StringBuilder();
                sb.Append($"Executing .net migration with following parameters: ");
                sb.AppendLine($"Root Path: {path}");
                if (recurse)
                    sb.AppendLine($"--recurse ");
                if (verbose)
                    sb.AppendLine($"--verbose ");
                if (makePkgOnBuild)
                    sb.AppendLine($"--makePkg ");
                if (!String.IsNullOrEmpty(nuspecPath))
                    sb.AppendLine($" {nuspecPath} ");
                if (whatIf)
                    sb.AppendLine($"--whatif");

                //Set locals
                _verbose = verbose;
                _recurse = recurse;
                _whatIf = whatIf;

                //Consider color formatting
                Console.WriteLine(sb.ToString());

                if (_recurse)
                {
                    //Find all csprojs
                    List<string> foundProjPaths = FindAllCSProj(path);
                    
                    //Upgrade each project //TODO: nuspecpath per proj?
                    foreach (string projPath in foundProjPaths)
                    {
                        UpgradeProject(projPath, makePkgOnBuild, nuspecPath);
                    }
                }
                else
                    UpgradeProject(path, makePkgOnBuild, nuspecPath);
            });

            //Invoke the command
            cmd.Invoke(args);
        }

        /// <summary>
        /// Given a root path, find all csproj files
        /// </summary>
        /// <param name="rootPath">Starting directory</param>
        /// <returns>Returns a list of csproj file paths</returns>
        private static List<string> FindAllCSProj(string rootPath)
        {
            if (_verbose)
            {
                Console.WriteLine($"Recursing through: {rootPath}");
            }
            var foundPaths = new List<string>();

            foundPaths = Directory.EnumerateFiles(rootPath, CSPROJ_SEARCH_CONSTANT, SearchOption.AllDirectories).ToList();

            Console.WriteLine($"Found {foundPaths.Count} projects in root directory");

            return foundPaths;
        }

        /// <summary>
        /// Upgrade a csproj file to net 5.0
        /// </summary>
        /// <param name="projPath">File path to the csproj file</param>
        /// <param name="makePackageOnBuild">Specify whether or not to generate a nupkg on build</param>
        /// <param name="nuspecPath">File path to pre-existing nuspec definition</param>
        /// <param name="verbose">Specify verbose output</param>
        /// <param name="whatIf">If true, runs without committing changes</param>
        public static void UpgradeProject(string projPath, bool makePackageOnBuild, string nuspecPath)
        {
            DateTime startTime = DateTime.Now;
            Console.WriteLine($"Beginning migration for {projPath} at: {startTime.ToString("yyyy/MM/dd HH:mm:ss.FFF")}");

            //TODO:Determine if exe/test project
            StringBuilder builder = new StringBuilder();

            //Build initial project definition lines
            builder.AppendLine("<Project Sdk=\"Microsoft.Net.Sdk\">");
            builder.AppendLine($"\t<PropertyGroup>{Environment.NewLine}\t\t<TargetFramework>net5.0</TargetFramework>");

            //Load project as xml
            XmlDocument doc = GetProjectAsXml(projPath, builder);
            XmlNamespaceManager msbNameSpace = new XmlNamespaceManager(doc.NameTable);
            msbNameSpace.AddNamespace("msbld", SCHEMA_MSBUILD);

            //Build output type
            string outputType = GetOutputType(doc, msbNameSpace);
            BuildOutputTag(builder, outputType);

            if (makePackageOnBuild)
                BuildNuspecInfo(doc, nuspecPath, builder);

            //Close header property group
            CloseHeaderSection(builder);

            //Build project references
            BuildProjectReferences(doc, msbNameSpace, builder);

            //Build Package References
            string packagesConfigPath = GetPackagesConfigPath(projPath);
            bool isTestProj = DetermineIfTestProj(doc, msbNameSpace);
            if (!String.IsNullOrEmpty(packagesConfigPath))
                BuildPackageReferences(packagesConfigPath, isTestProj, builder);

            //Build AssemblyInfo exclusion
            //TODO: Look into better method. Possible file delete of AssemblyInfo.cs
            ExcludeAssemblyInfo(builder);


            //Find and build pre/post events
            BuildPreBuildEvents(doc, msbNameSpace, builder);

            BuildPostBuildEvents(doc, msbNameSpace, builder);

            //Close out project tag
            builder.AppendLine($"</Project>");

            //Save update to csproj
            if (!_whatIf)
            {
                Save(builder, projPath);
            }

            Console.WriteLine("Final project output:");
            Console.WriteLine(builder.ToString());
            DateTime stopTime = DateTime.Now;

            Console.ForegroundColor = ConsoleColor.Green;
            Console.Write($"Successfully built new project format. Completed at: {stopTime.ToString("yyyy/MM/dd HH:mm:ss.FFF")} in: ");
            Console.ForegroundColor = ConsoleColor.Blue;
            Console.WriteLine($"{(stopTime - startTime).TotalMilliseconds} ms");
            Console.ResetColor();
        }

        public static XmlDocument GetProjectAsXml(string projPath, StringBuilder newCSProjBuilder)
        {
            Stopwatch sw = new Stopwatch();
            if (_verbose) 
            {
                Console.WriteLine("Fetching XmlDocument data...");
                sw.Start();
            }

            XmlDocument doc = new XmlDocument();
            doc.Load(projPath);

            if (_verbose)
            {
                sw.Stop();
                Console.WriteLine("Finished xml retrieval in: ");
                Console.ForegroundColor = ConsoleColor.Blue;
                Console.WriteLine($"{sw.ElapsedMilliseconds} ms");
                Console.ResetColor();
            }

            return doc;
        }

        public static string GetPackagesConfigPath(string projPath)
        {
            if (_verbose)
                Console.WriteLine("Fetching packages.config");

            //Get dir for csproj file
            string projectPath = Path.GetDirectoryName(projPath);

            //Create path for packages.config
            string combinedPath = Path.Combine(projectPath, PKG_CONFIG_CONSTANT);

            if (_verbose)
            {
                if (File.Exists(combinedPath))
                    Console.WriteLine($"Found packages.config: {combinedPath}");
                else
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"Warning: packages.config not found in: {projectPath}");
                    Console.ResetColor();

                    combinedPath = String.Empty;
                }
            }
            return combinedPath;
        }

        public static void BuildProjectReferences(XmlDocument doc, XmlNamespaceManager ns, StringBuilder builder)
        {
            Stopwatch sw = new Stopwatch();
            if (_verbose)
            {
                Console.WriteLine("Building project references...");
                sw.Start();
            }

            builder.AppendLine($"\t<ItemGroup>");
            int refProjCount = 0;
            foreach (XmlNode node in doc.SelectNodes(@"//msbld:ItemGroup/msbld:ProjectReference", ns))
            {
                refProjCount++;

                //Select 'Include' attribute value.
                var attr = node.Attributes["Include"];
                if (attr != null)
                    builder.AppendLine($"\t<ProjectReference Include=\"{attr.Value}\" />");
            }

            builder.AppendLine($"\t</ItemGroup>");

            if (_verbose)
            {
                sw.Stop();
                Console.Write("Finished building projet references in: ");
                Console.ForegroundColor = ConsoleColor.Blue;
                Console.WriteLine($"{sw.ElapsedMilliseconds} ms");
                Console.ResetColor();
                Console.WriteLine($"Referenced Projects: {refProjCount}");
            }
        }

        public static void BuildPreBuildEvents(XmlDocument doc, XmlNamespaceManager nms, StringBuilder builder)
        {
            if (_verbose)
            {
                Console.WriteLine($"Fetching pre build event");
            }

            //Find pre build events
            XmlNode preBuildNode =  doc.SelectSingleNode("//msbld:Target[@Name='BeforeBuild']", nms);

            if (preBuildNode != null)
            {
                if (_verbose)
                {
                    Console.WriteLine($"Generating prebuild tag using details: {preBuildNode.Value}");
                }

                builder.AppendLine("\t<Target Name=\"BeforeBuild\" BeforeTargets=\"PreBuildEvent\">");
                builder.AppendLine($"\t\t<Exec Command=\"{preBuildNode.Value}\"/>");
                builder.AppendLine("\t</Target>");

                if (_verbose)
                {
                    Console.WriteLine("Finished building PreBuild tag");
                }
            }
            else
            {
                if (_verbose)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine("No PreBuild event target found.");
                    Console.ResetColor();
                }
            }
        }

        public static void BuildPostBuildEvents(XmlDocument doc, XmlNamespaceManager nms, StringBuilder builder)
        {
            if (_verbose)
            {
                Console.WriteLine($"Fetching post build event");
            }

            //Find pre build events
            XmlNode preBuildNode = doc.SelectSingleNode("//msbld:Target[@Name='AfterBuild']", nms);

            if (preBuildNode != null)
            {
                if (_verbose)
                {
                    Console.WriteLine($"Generating prebuild tag using details: {preBuildNode.Value}");
                }

                builder.AppendLine("\t<Target Name=\"AfterBuild\" BeforeTargets=\"PostBuildEvent\">");
                builder.AppendLine($"\t\t<Exec Command=\"{preBuildNode.Value}\"/>");
                builder.AppendLine("\t</Target>");

                if (_verbose)
                {
                    Console.WriteLine("Finished building PostBuild tag");
                }
            }
            else
            {
                if (_verbose)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine("No PostBuild event target found.");
                    Console.ResetColor();
                }
            }
        }

        public static void BuildOutputTag(StringBuilder builder, string outputType)
        {
            if (_verbose)
                Console.WriteLine("Building Output tag");

            builder.AppendLine($"\t\t<OutputType>{outputType}</OutputType>");
        }

        public static void BuildPackageReferences(string pkgConfigPath, bool includeMSTest, StringBuilder builder)
        {
            Stopwatch sw = new Stopwatch();
            if (_verbose)
            {
                Console.WriteLine($"Building package references from: {pkgConfigPath}. Include MSTest conversion: {includeMSTest}");
                sw.Start();
            }

            XmlDocument pkgConfig = new XmlDocument();
            pkgConfig.Load(pkgConfigPath);

            //possibly add namespace manager
            builder.AppendLine($"\t<ItemGroup>");

            int pkgCount = 0;
            foreach(XmlNode node in pkgConfig.SelectNodes("//packages/package"))
            {
                pkgCount++;
                var id = node.Attributes["id"];
                var version = node.Attributes["version"];
                if (id != null && version != null)
                    builder.AppendLine($"\t\t<PackageReference Include=\"{id.Value}\" Version=\"{version.Value}\" />");
            }

            //TODO: potential issue with existing test package refs
            if (includeMSTest)
            {
                builder.AppendLine("\t\t<PackageReference Include=\"Microsoft.NET.Test.Sdk\" Version=\"15.3.0\" />");
                builder.AppendLine("\t\t<PackageReference Include=\"MSTest.TestAdapter\" Version=\"1.1.18\" />");
                builder.AppendLine("\t\t<PackageReference Include=\"MSTest.TestFramework\" Version=\"1.1.18\" />");
            }

            builder.AppendLine($"\t</ItemGroup>");
            if (_verbose)
            {
                sw.Stop();
                Console.Write("Finished building package references in: ");
                Console.ForegroundColor = ConsoleColor.Blue;
                Console.WriteLine($"{sw.ElapsedMilliseconds} ms");
                Console.ResetColor();
                Console.WriteLine($"Packages ported: {pkgCount}");
            }
        }

        private static void Save(StringBuilder builder, string path)
        {
            try
            {
                File.WriteAllText(path, builder.ToString());
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine(ex.Message);

                if (_verbose)
                    Console.WriteLine(ex.StackTrace);

                Console.ResetColor();
            }
        }

        private static string GetOutputType(XmlDocument doc, XmlNamespaceManager nms)
        {
            //TODO: compare legacy to updated types - may need conversion
            // expect library, exe, consoleapp etc.
            //how does this compare to netcoreapp, exe, dll etc
            var node = doc.SelectSingleNode("//msbld:OutputType", nms);

            if (node != null)
            {
                return node.Value;
            }

            return "Library";
        }
        private static bool DetermineIfTestProj(XmlDocument doc, XmlNamespaceManager nms)
        {
            bool isTest = false;

            foreach (XmlNode node in doc.SelectNodes("//msbld:ItemGroup/msbld:Reference", nms))
            {
                var attr = node.Attributes["Include"].Value;
                if (attr.Contains("VisualStudio.QualityTools") || attr.Contains("VisualStudio.TestTools"))
                {
                    isTest = true;
                    break;
                }
            }

            if (_verbose)
            {
                Console.WriteLine(isTest ? "project is in Test format" : "project is not in Test format");
            }

            return isTest;
        }
        private static void BuildNuspecInfo(XmlDocument doc, string nuspecPath, StringBuilder builder)
        {
            Stopwatch sw = new Stopwatch();
            if (_verbose)
            {
                Console.WriteLine($"Building nuspec values. Importing from: {nuspecPath}");
                sw.Start();
            }
            //create xmldoc for nuspec path and pull out relevant info for building the nuget tags
            XmlDocument nuspec = new XmlDocument();
            nuspec.Load(nuspecPath);

            XmlNamespaceManager nms = new XmlNamespaceManager(nuspec.NameTable);
            nms.AddNamespace("pkg", SCHEMA_NUGET);

            //Find Id
            string id = nuspec.SelectSingleNode("//pkg:id", nms).Value;
            //Find version
            string version = nuspec.SelectSingleNode("//pkg:version", nms).Value;
            //Find title
            string title = nuspec.SelectSingleNode("//pkg:title", nms).Value;
            //Find authors
            string authors = nuspec.SelectSingleNode("//pkg:authors", nms).Value;
            //Find description
            string desc = nuspec.SelectSingleNode("//pkg:description", nms).Value;
            //Todo: Find dependencies
            //Todo: File Inclusion such as html, css, js etc

            builder.AppendLine($"\t\t<id>{id}</id>");
            builder.AppendLine($"\t\t<version>{version}</version>");
            builder.AppendLine($"\t\t<title>{title}</title>");
            builder.AppendLine($"\t\t<authors>{authors}</authors>");
            builder.AppendLine($"\t\t<description>{desc}</description");

            if (_verbose)
            {
                sw.Stop();
                Console.Write("Finished nuspec import in: ");
                Console.ForegroundColor = ConsoleColor.Blue;
                Console.WriteLine($"{sw.ElapsedMilliseconds} ms");
                Console.ResetColor();
            }
        }

        private static void CloseHeaderSection(StringBuilder builder)
        {
            builder.AppendLine($"\t</PropertyGroup>");
        }
        private static void ExcludeAssemblyInfo(StringBuilder builder)
        {
            builder.AppendLine($"\t<ItemGroup>{Environment.NewLine}\t\t<None Exclude=\"Properties\\AssemblyInfo.cs\" />{Environment.NewLine}\t</ItemGroup>");
        }
    }
}
