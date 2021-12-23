﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using ConcurrentCollections;
using Microsoft.AspNetCore.Mvc.Razor.Compilation;
using Microsoft.AspNetCore.Mvc.Razor.RuntimeCompilation;
using Microsoft.AspNetCore.Razor.Hosting;
using Microsoft.AspNetCore.Razor.Language;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using Statiq.Common;

namespace Statiq.Razor
{
    // RuntimeViewCompiler is internal and the Razor team has made it clear they're not interested in maintaining a public API
    // So we've got to encapsulate it and get what we need via reflection
    // This is called for document Razor files by RazorCompiler which caches those itself, it's also called directly for layouts and partials, which are cached here
    internal class StatiqViewCompiler : CachingCompiler, IViewCompiler, IViewCompilerProvider
    {
        public static readonly RazorCompiledItemLoader CompiledItemLoader = new RazorCompiledItemLoader();

        private static readonly EmitOptions AssemblyEmitOptions = new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb);

        private static readonly MethodInfo CreateCompilationMethod;

        private static readonly MethodInfo GetNormalizedPathMethod;

        private static readonly MethodInfo CreateCompilationFailedExceptionFromRazorMethod;

        private static readonly MethodInfo CreateCompilationFailedExceptionFromDiagnosticsMethod;

        static StatiqViewCompiler()
        {
            Type runtimeViewCompilerType = typeof(FileProviderRazorProjectItem).Assembly
                .GetType("Microsoft.AspNetCore.Mvc.Razor.RuntimeCompilation.RuntimeViewCompiler");
            CreateCompilationMethod = runtimeViewCompilerType.GetMethod(
                "CreateCompilation",
                BindingFlags.Instance | BindingFlags.NonPublic,
                Type.DefaultBinder,
                new Type[] { typeof(string), typeof(string) },
                null);
            GetNormalizedPathMethod = runtimeViewCompilerType.GetMethod(
                "GetNormalizedPath",
                BindingFlags.Instance | BindingFlags.NonPublic,
                Type.DefaultBinder,
                new Type[] { typeof(string) },
                null);

            Type compilationFailedExceptionFactory = typeof(FileProviderRazorProjectItem).Assembly
                .GetType("Microsoft.AspNetCore.Mvc.Razor.RuntimeCompilation.CompilationFailedExceptionFactory");
            CreateCompilationFailedExceptionFromRazorMethod = compilationFailedExceptionFactory.GetMethod(
                "Create",
                new Type[] { typeof(RazorCodeDocument), typeof(IEnumerable<RazorDiagnostic>) });
            CreateCompilationFailedExceptionFromDiagnosticsMethod = compilationFailedExceptionFactory.GetMethod(
                "Create",
                new Type[] { typeof(RazorCodeDocument), typeof(string), typeof(string), typeof(IEnumerable<Diagnostic>) });
        }

        public static Exception CreateCompilationFailedExceptionFromRazor(RazorCodeDocument codeDocument, IEnumerable<RazorDiagnostic> diagnostics) =>
            (Exception)CreateCompilationFailedExceptionFromRazorMethod.Invoke(null, new object[] { codeDocument, diagnostics });

        public static Exception CreateCompilationFailedExceptionFromDiagnostics(
            RazorCodeDocument codeDocument,
            string compilationContext,
            string assemblyName,
            IEnumerable<Diagnostic> diagnostics) =>
            (Exception)CreateCompilationFailedExceptionFromDiagnosticsMethod.Invoke(null, new object[] { codeDocument, compilationContext, assemblyName, diagnostics });

        private readonly RazorProjectEngine _projectEngine;

        private readonly Microsoft.Extensions.FileProviders.IFileProvider _fileProvider;

        private readonly IMemoryStreamFactory _memoryStreamFactory;

        public StatiqViewCompiler(
            IViewCompilerProvider innerViewCompilerProvider,
            RazorProjectEngine projectEngine,
            Microsoft.Extensions.FileProviders.IFileProvider fileProvider,
            IMemoryStreamFactory memoryStreamFactory,
            INamespacesCollection namespaces)
        {
            InnerViewCompilerProvider = innerViewCompilerProvider;
            _projectEngine = projectEngine;
            _fileProvider = fileProvider;
            _memoryStreamFactory = memoryStreamFactory;

            // Ensure that the custom phases are registered for the global view engine
            EnsurePhases(projectEngine, namespaces.ToArray(), null);
        }

        public IViewCompilerProvider InnerViewCompilerProvider { get; }

        // The 0 cache code for this compiler indicates the global view/partial compiler (other compilers have a non-zero cache code)
        public CompilationParameters CompilationParameters { get; } = new CompilationParameters();

        // This is the reason this whole class exists - it's the only place and way to intercept layout and partial compilation
        public async Task<CompiledViewDescriptor> CompileAsync(string relativePath)
        {
            // Get the project item which contains the absolute physical path and then get the file
            string normalizedPath = GetNormalizedPath(relativePath);
            List<IChangeToken> expirationTokens = new List<IChangeToken>
            {
                _fileProvider.Watch(normalizedPath),
            };
            FileProviderRazorProjectItem projectItem = (FileProviderRazorProjectItem)_projectEngine.FileSystem.GetItem(normalizedPath, fileKind: null);
            IFile file = (projectItem.FileInfo as StatiqFileInfo)?.File; // Might be a NotFoundFileInfo
            if (!projectItem.Exists || file is null)
            {
                IExecutionContext.Current.LogDebug($"Requested Razor view {normalizedPath} does not exist");
                return new CompiledViewDescriptor
                {
                    RelativePath = normalizedPath,
                    ExpirationTokens = expirationTokens
                };
            }

            // Check the cache
            CompilerCacheKey cacheKey = CompilerCacheKey.Get(null, await file.GetCacheCodeAsync());
            CompilationResult compilationResult = GetOrAddCachedCompilation(cacheKey, _ =>
            {
                RazorCodeDocument codeDocument = _projectEngine.Process(projectItem);
                RazorCSharpDocument cSharpDocument = codeDocument.GetCSharpDocument();
                if (cSharpDocument.Diagnostics.Count > 0)
                {
                    throw CreateCompilationFailedExceptionFromRazor(codeDocument, cSharpDocument.Diagnostics);
                }
                IExecutionContext.Current.LogDebug($"Compiling " + projectItem.FilePath);
                return CompileAndEmit(codeDocument, cSharpDocument.GeneratedCode);
            });

            // Create a view descriptor from the result
            return new CompiledViewDescriptor(compilationResult.CompiledItem)
            {
                RelativePath = normalizedPath,
                ExpirationTokens = expirationTokens
            };
        }

        public IViewCompiler GetCompiler() => this;

        public CSharpCompilation CreateCompilation(string generatedCode, string assemblyName) =>
            (CSharpCompilation)CreateCompilationMethod.Invoke(InnerViewCompilerProvider.GetCompiler(), new object[] { generatedCode, assemblyName });

        public string GetNormalizedPath(string relativePath) =>
            (string)GetNormalizedPathMethod.Invoke(InnerViewCompilerProvider.GetCompiler(), new object[] { relativePath });

        // Adapted from RuntimeViewCompiler.CompileAndEmit() (Microsoft.AspNetCore.Mvc.Razor.RuntimeCompilation.dll) to save assembly to disk for caching
        // Also called from RazorCompiler for consistency (from the single global StatiqViewCompiler instance for access to the InnerViewCompilerProvider)
        public CompilationResult CompileAndEmit(RazorCodeDocument codeDocument, string generatedCode)
        {
            // Create the compilation
            string assemblyName = Path.GetRandomFileName();
            CSharpCompilation compilation = CreateCompilation(generatedCode, assemblyName);

            // Emit the compilation to memory streams (disposed later at the end of this execution round)
            MemoryStream assemblyStream = _memoryStreamFactory?.GetStream() ?? new MemoryStream();
            MemoryStream pdbStream = _memoryStreamFactory?.GetStream() ?? new MemoryStream();
            EmitResult result = compilation.Emit(
                assemblyStream,
                pdbStream,
                options: AssemblyEmitOptions);

            // Log diagnostics and throw if there are errors
            foreach (Diagnostic diagnostic in result.Diagnostics.Where(x => !x.IsSuppressed))
            {
                LogLevel logLevel = diagnostic.Severity switch
                {
                    DiagnosticSeverity.Error => LogLevel.Error,
                    DiagnosticSeverity.Warning => LogLevel.Warning,
                    DiagnosticSeverity.Info => LogLevel.Information,
                    _ => LogLevel.Debug
                };
                IExecutionContext.Current.Log(logLevel, diagnostic.ToString());
            }
            if (!result.Success)
            {
                throw CreateCompilationFailedExceptionFromDiagnostics(codeDocument, generatedCode, assemblyName, result.Diagnostics);
            }

            // Load the assembly from the streams
            assemblyStream.Seek(0, SeekOrigin.Begin);
            pdbStream.Seek(0, SeekOrigin.Begin);
            Assembly assembly = Assembly.Load(assemblyStream.ToArray(), pdbStream.ToArray());

            // Get the Razor item and return
            RazorCompiledItem razorCompiledItem = CompiledItemLoader.LoadItems(assembly).SingleOrDefault();
            return new CompilationResult(assemblyName, assemblyStream, pdbStream, assembly, razorCompiledItem);
        }
    }
}