using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using FSharp.Compiler.CodeAnalysis;
using FSharp.Compiler.Diagnostics;
using FSharp.Compiler.SimpleSourceCodeServices;
using Microsoft.FSharp.Core;
using Skua.Core.Models;

namespace Skua.Core.Scripts;

/// <summary>
/// Runtime compiler for F# scripts that mirrors the capabilities of the C# compiler wrapper.
/// </summary>
public sealed class FSharpScriptCompiler
{
    private readonly SimpleSourceCodeServices _compiler;
    private readonly HashSet<string> _defaultReferences;

    public FSharpScriptCompiler()
    {
        _compiler = new SimpleSourceCodeServices();
        _defaultReferences = BuildDefaultReferences();
    }

    /// <summary>
    /// Compiles the provided F# source code into a dynamic assembly and returns an instance of the first exported type.
    /// </summary>
    /// <param name="source">Source code for the F# script.</param>
    /// <param name="additionalReferences">Additional references that should be available to the script.</param>
    /// <param name="assemblyName">Name for the generated dynamic assembly.</param>
    /// <returns>Instance of the compiled script or null if the assembly contains no public types.</returns>
    /// <exception cref="ScriptCompileException">Thrown when compilation fails.</exception>
    public object? Compile(string source, IEnumerable<string> additionalReferences, string assemblyName)
    {
        var references = MergeReferences(additionalReferences);
        string fileName = Path.Combine(Path.GetTempPath(), $"{assemblyName}_{Guid.NewGuid():N}.fs");
        File.WriteAllText(fileName, source, Encoding.UTF8);

        try
        {
            var args = new List<string>
            {
                "fsc.exe",
                "--target:library",
                "--optimize+",
                "--tailcalls+",
                "--fullpaths",
                "--nocopyfsharpcore",
                "--deterministic-",
                "--targetprofile:netcore",
                fileName
            };

            foreach (var reference in references)
            {
                args.Add($"-r:{reference}");
            }

            var compileResult = _compiler.CompileToDynamicAssembly(
                args.ToArray(),
                dynamicAssemblyName: assemblyName,
                collectible: true);

            var errors = compileResult.Item1;
            var exitCode = compileResult.Item2;
            var assemblyOption = compileResult.Item3;

            bool hasErrors = errors.Any(error => error.Severity == FSharpErrorSeverity.Error);
            if (hasErrors || exitCode != 0 || !OptionModule.IsSome(assemblyOption))
            {
                var sb = new StringBuilder();
                foreach (var error in errors)
                {
                    sb.AppendLine(error.ToString());
                }

                if (exitCode != 0 && sb.Length == 0)
                    sb.AppendLine($"Compilation failed with exit code {exitCode}.");

                throw new ScriptCompileException(sb.ToString().Trim(), source);
            }

            var assembly = OptionModule.GetValue(assemblyOption);
            var exportedType = assembly.ExportedTypes.FirstOrDefault();
            return exportedType is null ? null : Activator.CreateInstance(exportedType);
        }
        finally
        {
            TryDelete(fileName);
        }
    }

    private static HashSet<string> BuildDefaultReferences()
    {
        var references = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddReference(string? path)
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                references.Add(path);
        }

        AddReference(typeof(object).Assembly.Location);
        AddReference(typeof(Console).Assembly.Location);
        AddReference(typeof(Enumerable).Assembly.Location);
        AddReference(typeof(ScriptManager).Assembly.Location);
        AddReference(typeof(Microsoft.FSharp.Core.Unit).Assembly.Location);

        string runtimeAssembly = typeof(System.Runtime.GCSettings).GetTypeInfo().Assembly.Location;
        if (!string.IsNullOrWhiteSpace(runtimeAssembly))
        {
            string? runtimeDir = Path.GetDirectoryName(runtimeAssembly);
            if (!string.IsNullOrWhiteSpace(runtimeDir))
            {
                AddReference(Path.Combine(runtimeDir, "System.Runtime.dll"));
                AddReference(Path.Combine(runtimeDir, "System.Private.CoreLib.dll"));
                AddReference(Path.Combine(runtimeDir, "netstandard.dll"));
            }
        }

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.IsDynamic)
                continue;

            AddReference(assembly.Location);
        }

        return references;
    }

    private HashSet<string> MergeReferences(IEnumerable<string> additionalReferences)
    {
        var merged = new HashSet<string>(_defaultReferences, StringComparer.OrdinalIgnoreCase);
        foreach (var reference in additionalReferences)
        {
            if (!string.IsNullOrWhiteSpace(reference) && File.Exists(reference))
                merged.Add(reference);
        }

        return merged;
    }

    private static void TryDelete(string fileName)
    {
        try
        {
            if (File.Exists(fileName))
                File.Delete(fileName);
        }
        catch
        {
            // Ignore cleanup failures.
        }
    }
}
