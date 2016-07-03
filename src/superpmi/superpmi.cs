// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

///////////////////////////////////////////////////////////////////////////////
//
//  superpmi - collect/replay jit compilation.
//

using System;
using System.Diagnostics;
using System.CommandLine;
using System.IO;
using System.Collections.Generic;
using System.Runtime.Loader;
using System.Linq;
using Microsoft.DotNet.Cli.Utils;
using Microsoft.DotNet.Tools.Common;

namespace ManagedCodeGen
{
    public enum Commands
    {
        Collect,
        Replay
    }

    // Define options to be parsed 
    public class Config
    {
        private ArgumentSyntax _syntaxResult;

        private string _crossgenExe = null;
        private string _outputPath = null;
        private string _mchPath = null;
        private bool _verbose = false;
        private bool _recursive = false;
        private IReadOnlyList<string> _assemblyList = Array.Empty<string>();
        private Commands _command;

        public Config(string[] args)
        {
            _syntaxResult = ArgumentSyntax.Parse(args, syntax =>
            {

                // Install command section.
                syntax.DefineCommand("collect", ref _command, Commands.Collect, "Install tool in config.");
                syntax.DefineOption("c|crossgen", ref _crossgenExe, "The crossgen compiler exe.");
                syntax.DefineOption("o|output", ref _outputPath, "The output path.");
                syntax.DefineOption("v|verbose", ref _verbose, "Enable verbose output.");
                syntax.DefineOption("r|recursive", ref _recursive, "Search directories recursivly.");

                // Warning!! - Parameters must occur after options to preserve parsing semantics.

                syntax.DefineParameterList("assembly", ref _assemblyList, "The list of assemblies or directories to scan for assemblies.");

                // Install command section.
                syntax.DefineCommand("replay", ref _command, Commands.Replay, "Replay compilation from .mch file.");
                syntax.DefineOption("mch", ref _mchPath, "Path to .mch file.");
            });

            // Run validation code on parsed input to ensure we have a sensible scenario.

            validate();
        }

        // Validate supported scenarios
        //
        private void validate()
        {
            // TODO insert argument validation.
        }

        public bool DoVerboseOutput { get { return _verbose; } }
        public bool Recursive { get { return _recursive; } }
        public string CrossgenExecutable { get { return _crossgenExe; } }
        public string OutputPath { get { return _outputPath; } }
        public string MCHPath { get { return _mchPath; } }
        public IReadOnlyList<string> AssemblyList { get { return _assemblyList; } }
        public bool HasUserAssemblies { get { return AssemblyList.Count > 0; } }
        public Commands DoCommand { get { return _command; } }
    }

    public class AssemblyInfo
    {
        public string Name { get; set; }
        // Contains path to assembly.
        public string Path { get; set; }
        // Contains relative path within output directory for given assembly.
        // This allows for different output directories per tool.
        public string OutputPath { get; set; }
    }

    public class superpmi
    {
        public static int Main(string[] args)
        {
            // Error count will be returned.  Start at 0 - this will be incremented
            // based on the error counts derived from the DisasmEngine executions.
            int errorCount = 0;

            // Parse and store comand line options.
            var config = new Config(args);

            switch (config.DoCommand)
            {
                case Commands.Collect:
                {
                    // Builds assemblyInfoList on jitdasm

                    List<AssemblyInfo> assemblyWorkList = GenerateAssemblyWorklist(config);

                    // For each assembly generate a collection.
            
                    CollectionEngine collection = new CollectionEngine(config.CrossgenExecutable, config, config.OutputPath, assemblyWorkList);
                    collection.Collect();

                    if (collection.ErrorCount > 0)
                    {
                        Console.Error.WriteLine("{0} errors compiling set.", collection.ErrorCount);
                        errorCount += collection.ErrorCount;
                    }
                }
                break;

                case Commands.Replay:
                {
                    // TODO - replay mch.
                    errorCount = -1;
                }
                break;
            }

            return errorCount;
        }

        public static List<AssemblyInfo> GenerateAssemblyWorklist(Config config)
        {
            bool verbose = config.DoVerboseOutput;
            List<string> assemblyList = new List<string>();
            List<AssemblyInfo> assemblyInfoList = new List<AssemblyInfo>();

            if (config.HasUserAssemblies)
            {
                // Append command line assemblies
                assemblyList.AddRange(config.AssemblyList);
            }

            // Process worklist and produce the info needed for the disasm engines.
            foreach (var path in assemblyList)
            {
                FileAttributes attr;

                if (File.Exists(path) || Directory.Exists(path))
                {
                    attr = File.GetAttributes(path);
                }
                else
                {
                    Console.WriteLine("Can't find assembly or directory at {0}", path);
                    continue;
                }

                if ((attr & FileAttributes.Directory) == FileAttributes.Directory)
                {
                    if (verbose)
                    {
                        Console.WriteLine("Processing directory: {0}", path);
                    }

                    // For the directory case create a stack and recursively find any
                    // assemblies for compilation.
                    List<AssemblyInfo> directoryAssemblyInfoList = IdentifyAssemblies(path,
                        config);

                    // Add info generated at this directory
                    assemblyInfoList.AddRange(directoryAssemblyInfoList);
                }
                else
                {
                    // This is the file case.

                    AssemblyInfo info = new AssemblyInfo
                    {
                        Name = Path.GetFileName(path),
                        Path = Path.GetDirectoryName(path),
                        OutputPath = ""
                    };

                    assemblyInfoList.Add(info);
                }
            }

            return assemblyInfoList;
        }

        // Check to see if the passed filePath is to an assembly.
        private static bool IsAssembly(string filePath)
        {
            try
            {
                System.Reflection.AssemblyName diffAssembly =
                    System.Runtime.Loader.AssemblyLoadContext.GetAssemblyName(filePath);
            }
            catch (System.IO.FileNotFoundException)
            {
                // File not found - not an assembly
                // TODO - should we log this case?
                return false;
            }
            catch (System.BadImageFormatException)
            {
                // Explictly not an assembly.
                return false;
            }
            catch (System.IO.FileLoadException)
            {
                // This is an assembly but it just happens to be loaded.
                // (leave true in so as not to rely on fallthrough)
                return true;
            }

            return true;
        }

        // Recursivly search for assemblies from a root path.
        private static List<AssemblyInfo> IdentifyAssemblies(string rootPath, Config config)
        {
            List<AssemblyInfo> assemblyInfoList = new List<AssemblyInfo>();
            string fullRootPath = Path.GetFullPath(rootPath);
            SearchOption searchOption = (config.Recursive) ?
                SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

            // Get files that could be assemblies, but discard currently
            // ngen'd assemblies.
            var subFiles = Directory.EnumerateFiles(rootPath, "*", searchOption)
                .Where(s => (s.EndsWith(".exe") || s.EndsWith(".dll"))
                    && !s.Contains(".ni."));

            foreach (var filePath in subFiles)
            {
                if (config.DoVerboseOutput)
                {
                    Console.WriteLine("Scaning: {0}", filePath);
                }

                // skip if not an assembly
                if (!IsAssembly(filePath))
                {
                    continue;
                }

                string fileName = Path.GetFileName(filePath);
                string directoryName = Path.GetDirectoryName(filePath);
                string fullDirectoryName = Path.GetFullPath(directoryName);
                string outputPath = fullDirectoryName.Substring(fullRootPath.Length).TrimStart(Path.DirectorySeparatorChar);

                AssemblyInfo info = new AssemblyInfo
                {
                    Name = fileName,
                    Path = directoryName,
                    OutputPath = outputPath
                };

                assemblyInfoList.Add(info);
            }

            return assemblyInfoList;
        }

        private class CollectionEngine
        {
            private string _executablePath;
            private Config _config;
            private string _rootPath = null;
            private List<AssemblyInfo> _assemblyInfoList;
            public bool verbose = false;
            private int _errorCount = 0;

            public int ErrorCount { get { return _errorCount; } }

            public CollectionEngine(string executable, Config config, string outputPath,
                List<AssemblyInfo> assemblyInfoList)
            {
                _config = config;
                _executablePath = executable;
                _rootPath = outputPath;
                _assemblyInfoList = assemblyInfoList;

                this.verbose = config.DoVerboseOutput;
            }

            public void Collect()
            {
                // Build a command per assembly to generate the asm output.
                foreach (var assembly in _assemblyInfoList)
                {
                    string fullPathAssembly = Path.Combine(assembly.Path, assembly.Name);

                    if (!File.Exists(fullPathAssembly))
                    {
                        // Assembly not found.  Produce a warning and skip this input.
                        Console.WriteLine("Skipping. Assembly not found: {0}", fullPathAssembly);
                        continue;
                    }

                    List<string> commandArgs = new List<string>() { fullPathAssembly };
                    Command generateCmd = null;

                    try 
                    {
                        generateCmd = Command.Create(_executablePath, commandArgs);
                    }
                    catch (CommandUnknownException e)
                    {
                        Console.Error.WriteLine("\nError: {0} command not found!\n", e);
                        Environment.Exit(-1);
                    }

                    // Pick up ambient COMPlus settings.
                    foreach (string envVar in Environment.GetEnvironmentVariables().Keys)
                    {
                        if (envVar.IndexOf("COMPlus_") == 0)
                        {
                            string value = Environment.GetEnvironmentVariable(envVar);
                            if (this.verbose)
                            {
                                Console.WriteLine("Incorporating ambient setting: {0}={1}", envVar, value);
                            }
                            generateCmd.EnvironmentVariable(envVar, value);
                        }
                    }

                    // TODO - set this up for superpmi collection.
                    generateCmd.EnvironmentVariable("COMPlus_NgenDisasm", "*");
                    generateCmd.EnvironmentVariable("COMPlus_NgenUnwindDump", "*");
                    generateCmd.EnvironmentVariable("COMPlus_NgenEHDump", "*");
                    generateCmd.EnvironmentVariable("COMPlus_JitDiffableDasm", "1");

                    if (this.verbose)
                    {
                        Console.WriteLine("Running: {0} {1}", _executablePath, String.Join(" ", commandArgs));
                    }

                    CommandResult result;

                    if (_rootPath != null)
                    {
                        // Generate path to the output file
                        var assemblyFileName = Path.ChangeExtension(assembly.Name, ".mch");
                        var path = Path.Combine(_rootPath, assembly.OutputPath, assemblyFileName);

                        PathUtility.EnsureParentDirectory(path);

                        // Redirect stdout/stderr to disasm file and run command.
                        using (var outputStream = System.IO.File.Create(path))
                        {
                            using (var outputStreamWriter = new StreamWriter(outputStream))
                            {
                                // Forward output and error to file.
                                generateCmd.ForwardStdOut(outputStreamWriter);
                                generateCmd.ForwardStdErr(outputStreamWriter);
                                result = generateCmd.Execute();
                            }
                        }
                        
                        if (result.ExitCode != 0)
                        {
                            Console.Error.WriteLine("Error running {0} on {1}", _executablePath, fullPathAssembly);
                            _errorCount++;

                            // If the tool still produced a output file rename it to indicate
                            // the error in the file system.
                            if (File.Exists(path))
                            {
                                // Change file to *.err.
                                string errorPath = Path.ChangeExtension(path, ".err");

                                // If this is a rerun to the same output, overwrite with current
                                // error output.
                                if (File.Exists(errorPath))
                                {
                                    File.Delete(errorPath);
                                }

                                File.Move(path, errorPath);
                            }
                        }
                    }
                }
            }
        }
    }
}
