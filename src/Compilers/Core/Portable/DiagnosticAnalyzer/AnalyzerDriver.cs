﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using Microsoft.CodeAnalysis.Collections;
using Roslyn.Utilities;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.CodeAnalysis.Diagnostics
{
    /// <summary>
    /// Driver to execute diagnostic analyzers for a given compilation.
    /// It uses a <see cref="AsyncQueue{TElement}"/> of <see cref="CompilationEvent"/>s to drive its analysis.
    /// </summary>
    internal abstract class AnalyzerDriver : IDisposable
    {
        internal static readonly ConditionalWeakTable<Compilation, SuppressMessageAttributeState> SuppressMessageStateByCompilation = new ConditionalWeakTable<Compilation, SuppressMessageAttributeState>();
        private static readonly Task EmptyTask = Task.FromResult(false);

        // Protect against vicious analyzers that provide large values for SymbolKind.
        private const int MaxSymbolKind = 100;

        private readonly Action<Diagnostic> addDiagnostic;
        private readonly ImmutableArray<DiagnosticAnalyzer> analyzers;
        private readonly CancellationTokenRegistration queueRegistration;
        protected readonly AnalyzerOptions analyzerOptions;
        internal readonly Func<Exception, DiagnosticAnalyzer, bool> continueOnAnalyzerException;

        // Lazy fields initialized in Initialize() API
        private Compilation compilation;
        internal HostCompilationStartAnalysisScope compilationAnalysisScope;
        private ImmutableArray<ImmutableArray<SymbolAnalyzerAction>> declarationAnalyzerActionsByKind;
        
        /// <summary>
        /// Primary driver task which processes all <see cref="CompilationEventQueue"/> events, runs analyzer actions and signals completion of <see cref="DiagnosticQueue"/> at the end.
        /// </summary>
        private Task primaryTask;

        /// <summary>
        /// The compilation queue to create the compilation with via WithEventQueue.
        /// </summary>
        public AsyncQueue<CompilationEvent> CompilationEventQueue
        {
            get; private set;
        }

        /// <summary>
        /// An async queue that is fed the diagnostics as they are computed.
        /// </summary>
        public AsyncQueue<Diagnostic> DiagnosticQueue
        {
            get; private set;
        }

        /// <summary>
        /// Initializes the compilation for the analyzer driver.
        /// It also computes and initializes <see cref="compilationAnalysisScope"/> and <see cref="declarationAnalyzerActionsByKind"/>.
        /// Finally, it initializes and starts the <see cref="primaryTask"/> for the driver.
        /// </summary>
        /// <remarks>
        /// NOTE: This method must only be invoked from <see cref="AnalyzerDriver.Create(Compilation, ImmutableArray{DiagnosticAnalyzer}, AnalyzerOptions, out Compilation, CancellationToken)"/>.
        /// </remarks>
        private void Initialize(Compilation comp, CancellationToken cancellationToken)
        {
            try
            {
                Debug.Assert(this.compilation == null);
                Debug.Assert(comp.EventQueue == this.CompilationEventQueue);

                this.compilation = comp;

                // Compute the set of effective actions based on suppression, and running the initial analyzers
                var sessionAnalysisScope = GetSessionAnalysisScope(this.analyzers, comp.Options, addDiagnostic, continueOnAnalyzerException, cancellationToken);
                this.compilationAnalysisScope = GetCompilationAnalysisScope(sessionAnalysisScope, comp, analyzerOptions, addDiagnostic, continueOnAnalyzerException, cancellationToken);
                this.declarationAnalyzerActionsByKind = MakeDeclarationAnalyzersByKind();

                var syntaxTreeAnalyzersTask = ExecuteSyntaxTreeAnalyzers(cancellationToken);

                // create the primary driver task.
                cancellationToken.ThrowIfCancellationRequested();
                this.primaryTask = Task.Run(async () =>
                    {
                        await ProcessCompilationEventsAsync(cancellationToken).ConfigureAwait(false);
                        await syntaxTreeAnalyzersTask.ConfigureAwait(false);
                    }, cancellationToken)
                    .ContinueWith(c => DiagnosticQueue.TryComplete(), cancellationToken, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            finally
            {
                if (this.primaryTask == null)
                {
                    // Set primaryTask to be a cancelled task.
                    var tcs = new TaskCompletionSource<int>();
                    tcs.SetCanceled();
                    this.primaryTask = tcs.Task;

                    // Try to set the DiagnosticQueue to be complete.
                    this.DiagnosticQueue.TryComplete();
                }
            }
        }

        private Task ExecuteSyntaxTreeAnalyzers(CancellationToken cancellationToken)
        {
            // Invoke the syntax tree analyzers
            var syntaxTreeAnalyzersBuilder = ArrayBuilder<Task>.GetInstance();
            foreach (var tree in this.compilation.SyntaxTrees)
            {
                foreach (var syntaxTreeAction in this.compilationAnalysisScope.SyntaxTreeActions)
                {
                    var runningAsynchronously = Task.Run(() =>
                    {
                        // Catch Exception from executing the action
                        AnalyzerDriverHelper.ExecuteAndCatchIfThrows(syntaxTreeAction.Analyzer, addDiagnostic, continueOnAnalyzerException, () =>
                        {
                            var context = new SyntaxTreeAnalysisContext(tree, analyzerOptions, addDiagnostic, cancellationToken);
                            syntaxTreeAction.Action(context);
                        }, cancellationToken);
                    }, cancellationToken);

                    syntaxTreeAnalyzersBuilder.Add(runningAsynchronously);
                }
            }

            return Task.WhenAll(syntaxTreeAnalyzersBuilder.ToArrayAndFree());
        }

        /// <summary>
        /// Create an <see cref="AnalyzerDriver"/> and attach it to the given compilation. 
        /// </summary>
        /// <param name="compilation">The compilation to which the new driver should be attached.</param>
        /// <param name="analyzers">The set of analyzers to include in the analysis.</param>
        /// <param name="options">Options that are passed to analyzers.</param>
        /// <param name="newCompilation">The new compilation with the analyzer driver attached.</param>
        /// <param name="cancellationToken">A cancellation token that can be used to abort analysis.</param>
        /// <returns>A newly created analyzer driver</returns>
        /// <remarks>
        /// Note that since a compilation is immutable, the act of creating a driver and attaching it produces
        /// a new compilation. Any further actions on the compilation should use the new compilation.
        /// </remarks>
        public static AnalyzerDriver Create(Compilation compilation, ImmutableArray<DiagnosticAnalyzer> analyzers, AnalyzerOptions options, out Compilation newCompilation, CancellationToken cancellationToken)
        {
            if (compilation == null)
            {
                throw new ArgumentNullException(nameof(compilation));
            }

            if (analyzers.IsDefaultOrEmpty)
            {
                throw new ArgumentException(CodeAnalysisResources.ArgumentCannotBeEmpty, nameof(analyzers));
            }

            if (analyzers.Any(a => a == null))
            {
                throw new ArgumentException(CodeAnalysisResources.ArgumentElementCannotBeNull, nameof(analyzers));
            }

            return Create(compilation, analyzers, options, out newCompilation, continueOnAnalyzerException: null, cancellationToken: cancellationToken);
        }

        // internal for testing purposes
        internal static AnalyzerDriver Create(Compilation compilation, ImmutableArray<DiagnosticAnalyzer> analyzers, AnalyzerOptions options, out Compilation newCompilation, Func<Exception, DiagnosticAnalyzer, bool> continueOnAnalyzerException, CancellationToken cancellationToken)
        {
            options = options ?? AnalyzerOptions.Empty;
            AnalyzerDriver analyzerDriver = compilation.AnalyzerForLanguage(analyzers, options, continueOnAnalyzerException, cancellationToken);
            newCompilation = compilation.WithEventQueue(analyzerDriver.CompilationEventQueue);
            analyzerDriver.Initialize(newCompilation, cancellationToken);
            return analyzerDriver;
        }

        /// <summary>
        /// Create an analyzer driver.
        /// </summary>
        /// <param name="analyzers">The set of analyzers to include in the analysis</param>
        /// <param name="options">Options that are passed to analyzers</param>
        /// <param name="cancellationToken">a cancellation token that can be used to abort analysis</param>
        /// <param name="continueOnAnalyzerException">Delegate which is invoked when an analyzer throws an exception.
        /// If a non-null delegate is provided and it returns true, then the exception is handled and converted into a diagnostic and driver continues with other analyzers.
        /// Otherwise if it returns false, then the exception is not handled by the driver.
        /// If null, then the driver always handles the exception.
        /// </param>
        protected AnalyzerDriver(ImmutableArray<DiagnosticAnalyzer> analyzers, AnalyzerOptions options, CancellationToken cancellationToken, Func<Exception, DiagnosticAnalyzer, bool> continueOnAnalyzerException = null)
        {
            this.analyzers = analyzers;
            this.analyzerOptions = options;

            this.CompilationEventQueue = new AsyncQueue<CompilationEvent>();
            this.DiagnosticQueue = new AsyncQueue<Diagnostic>();
            this.addDiagnostic = GetDiagnosticSinkWithSuppression();
            this.queueRegistration = cancellationToken.Register(() =>
            {
                this.CompilationEventQueue.TryComplete();
                this.DiagnosticQueue.TryComplete();
            });

            Func<Exception, DiagnosticAnalyzer, bool> defaultExceptionHandler = (exception, analyzer) => true;
            this.continueOnAnalyzerException = continueOnAnalyzerException ?? defaultExceptionHandler;
        }

        /// <summary>
        /// Returns all diagnostics computed by the analyzers since the last time this was invoked.
        /// If <see cref="CompilationEventQueue"/> has been completed with all compilation events, then it waits for
        /// <see cref="WhenCompletedTask"/> task for the driver to finish processing all events and generate remaining analyzer diagnostics.
        /// </summary>
        public async Task<ImmutableArray<Diagnostic>> GetDiagnosticsAsync()
        {
            var allDiagnostics = DiagnosticBag.GetInstance();
            if (CompilationEventQueue.IsCompleted)
            {
                await this.WhenCompletedTask.ConfigureAwait(false);
            }

            Diagnostic d;
            while (DiagnosticQueue.TryDequeue(out d))
            {
                allDiagnostics.Add(d);
            }

            var diagnostics = allDiagnostics.ToReadOnlyAndFree();

            // Verify that the diagnostics are already filtered.
            Debug.Assert(this.compilation == null || 
                diagnostics.All(diag => this.compilation.FilterDiagnostic(diag)?.Severity == diag.Severity));

            return diagnostics;
        }

        /// <summary>
        /// Return a task that completes when the driver is done producing diagnostics.
        /// </summary>
        public Task WhenCompletedTask => this.primaryTask;

        private ImmutableArray<ImmutableArray<SymbolAnalyzerAction>> MakeDeclarationAnalyzersByKind()
        {
            var analyzersByKind = new List<ArrayBuilder<SymbolAnalyzerAction>>();
            foreach (var symbolAction in this.compilationAnalysisScope.SymbolActions)
            {
                var kinds = symbolAction.Kinds;
                foreach (int kind in kinds.Distinct())
                {
                    if (kind > MaxSymbolKind) continue; // protect against vicious analyzers
                    while (kind >= analyzersByKind.Count)
                    {
                        analyzersByKind.Add(ArrayBuilder<SymbolAnalyzerAction>.GetInstance());
                    }

                    analyzersByKind[kind].Add(symbolAction);
                }
            }

            return analyzersByKind.Select(a => a.ToImmutableAndFree()).ToImmutableArray();
        }

        private async Task ProcessCompilationEventsAsync(CancellationToken cancellationToken)
        {
            while (!CompilationEventQueue.IsCompleted || CompilationEventQueue.Count > 0)
            {
                CompilationEvent e;
                try
                {
                    e = await CompilationEventQueue.DequeueAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (TaskCanceledException)
                {
                    // When the queue is completed with a pending DequeueAsync return then a 
                    // TaskCanceledException will be thrown.  This just signals the queue is 
                    // complete and we should finish processing it.
                    Debug.Assert(CompilationEventQueue.IsCompleted, "DequeueAsync should never throw unless the AsyncQueue<T> is completed.");
                    break;
                }

                if (e.Compilation != this.compilation)
                {
                    Debug.Assert(false, "CompilationEvent with a different compilation then driver's compilation?");
                    continue;
                }

                try
                {
                    await ProcessEventAsync(e, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // when just a single operation is cancelled, we continue processing events.
                    // TODO: what is the desired behavior in this case?
                }
            }
        }

        private async Task ProcessEventAsync(CompilationEvent e, CancellationToken cancellationToken)
        {
            var symbolEvent = e as SymbolDeclaredCompilationEvent;
            if (symbolEvent != null)
            {
                await ProcessSymbolDeclared(symbolEvent, cancellationToken).ConfigureAwait(false);
                return;
            }

            var completedEvent = e as CompilationUnitCompletedEvent;
            if (completedEvent != null)
            {
                await ProcessCompilationUnitCompleted(completedEvent, cancellationToken).ConfigureAwait(false);
                return;
            }

            var endEvent = e as CompilationCompletedEvent;
            if (endEvent != null)
            {
                await ProcessCompilationCompletedAsync(endEvent, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (e is CompilationStartedEvent)
            {
                // Ignore CompilationStartedEvent.
                return;
            }

            throw new InvalidOperationException("Unexpected compilation event of type " + e.GetType().Name);
        }

        private Task ProcessSymbolDeclared(SymbolDeclaredCompilationEvent symbolEvent, CancellationToken cancellationToken)
        {
            try
            {
                return AnalyzeSymbol(symbolEvent, cancellationToken);
            }
            finally
            {
                symbolEvent.FlushCache();
            }
        }

        private Task AnalyzeSymbol(SymbolDeclaredCompilationEvent symbolEvent, CancellationToken cancellationToken)
        {
            var symbol = symbolEvent.Symbol;
            var isGlobalNamespace = symbol.Kind == SymbolKind.Namespace && ((INamespaceSymbol)symbol).IsGlobalNamespace;

            // Skip implicitly declared symbols.
            // For global namespace, we don't want to execute symbol actions, but do want to execute syntax actions for global syntax nodes.
            if (symbol.IsImplicitlyDeclared && !isGlobalNamespace)
            {
                return EmptyTask;
            }

            Action<Diagnostic> addDiagnosticForSymbol = GetDiagnosticSinkWithSuppression(symbol);
            var tasks = ArrayBuilder<Task>.GetInstance();

            // Invoke symbol analyzers only for source symbols.
            var declaringSyntaxRefs = symbol.DeclaringSyntaxReferences;
            if (!isGlobalNamespace && (int)symbol.Kind < declarationAnalyzerActionsByKind.Length && declaringSyntaxRefs.Any(s => s.SyntaxTree != null))
            {
                foreach (var da in declarationAnalyzerActionsByKind[(int)symbol.Kind])
                {
                    // TODO: is the overhead of creating tasks here too high compared to the cost of running them sequentially?
                    tasks.Add(Task.Run(() =>
                    {
                        // Catch Exception from analyzing the symbol
                        AnalyzerDriverHelper.ExecuteAndCatchIfThrows(da.Analyzer, addDiagnostic, continueOnAnalyzerException, () =>
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var symbolContext = new SymbolAnalysisContext(symbol, compilation, this.analyzerOptions, addDiagnosticForSymbol, cancellationToken);
                            da.Action(symbolContext);
                        }, cancellationToken);
                    }, cancellationToken));
                }
            }

            foreach (var decl in declaringSyntaxRefs)
            {
                tasks.Add(AnalyzeDeclaringReferenceAsync(symbolEvent, decl, addDiagnostic, cancellationToken));
            }

            return Task.WhenAll(tasks.ToArrayAndFree());
        }

        protected abstract Task AnalyzeDeclaringReferenceAsync(SymbolDeclaredCompilationEvent symbolEvent, SyntaxReference decl, Action<Diagnostic> addDiagnostic, CancellationToken cancellationToken);

        private Task ProcessCompilationUnitCompleted(CompilationUnitCompletedEvent completedEvent, CancellationToken cancellationToken)
        {
            // When the compiler is finished with a compilation unit, we can run user diagnostics which
            // might want to ask the compiler for all the diagnostics in the source file, for example
            // to get information about unnecessary usings.

            try
            {
                var tasks = ArrayBuilder<Task>.GetInstance();
                var semanticModel = completedEvent.SemanticModel;
                foreach (var da in this.compilationAnalysisScope.SemanticModelActions)
                {
                    // TODO: is the overhead of creating tasks here too high compared to the cost of running them sequentially?
                    tasks.Add(Task.Run(() =>
                    {
                        // Catch Exception from da.Action
                        AnalyzerDriverHelper.ExecuteAndCatchIfThrows(da.Analyzer, addDiagnostic, continueOnAnalyzerException, () =>
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var semanticModelContext = new SemanticModelAnalysisContext(semanticModel, this.analyzerOptions, addDiagnostic, cancellationToken);
                            da.Action(semanticModelContext);
                        }, cancellationToken);
                    }, cancellationToken));
                }

                return Task.WhenAll(tasks.ToArrayAndFree());
            }
            finally
            {
                completedEvent.FlushCache();
            }
        }

        private async Task ProcessCompilationCompletedAsync(CompilationCompletedEvent endEvent, CancellationToken cancellationToken)
        {
            try
            {
                var tasks = ArrayBuilder<Task>.GetInstance();
                foreach (var da in this.compilationAnalysisScope.CompilationEndActions)
                {
                    // TODO: is the overhead of creating tasks here too high compared to the cost of running them sequentially?
                    tasks.Add(Task.Run(() =>
                    {
                        // Catch Exception from da.Action
                        AnalyzerDriverHelper.ExecuteAndCatchIfThrows(da.Analyzer, addDiagnostic, continueOnAnalyzerException, () =>
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                var compilationContext = new CompilationEndAnalysisContext(compilation, this.analyzerOptions, addDiagnostic, cancellationToken);
                                da.Action(compilationContext);
                            }, cancellationToken);
                    }, cancellationToken));
                }

                await Task.WhenAll(tasks.ToArrayAndFree()).ConfigureAwait(false);
            }
            finally
            {
                endEvent.FlushCache();
            }
        }

        internal protected Action<Diagnostic> GetDiagnosticSinkWithSuppression(ISymbol symbolOpt = null)
        {
            return diagnostic =>
            {
                var d = compilation.FilterDiagnostic(diagnostic);
                if (d != null)
                {
                    var suppressMessageState = SuppressMessageStateByCompilation.GetValue(compilation, (c) => new SuppressMessageAttributeState(c));
                    if (!suppressMessageState.IsDiagnosticSuppressed(d, symbolOpt: symbolOpt))
                    {
                        DiagnosticQueue.Enqueue(d);
                    }
                }
            };
        }

        private static HostSessionStartAnalysisScope GetSessionAnalysisScope(
            IEnumerable<DiagnosticAnalyzer> analyzers,
            CompilationOptions compilationOptions,
            Func<DiagnosticAnalyzer, CompilationOptions, Action<Diagnostic>, Func<Exception, DiagnosticAnalyzer, bool>, CancellationToken, bool> isAnalyzerSuppressed,
            Action<Diagnostic> addDiagnostic,
            Func<Exception, DiagnosticAnalyzer, bool> continueOnAnalyzerException,
            CancellationToken cancellationToken)
        {
            HostSessionStartAnalysisScope sessionScope = new HostSessionStartAnalysisScope();

            foreach (DiagnosticAnalyzer analyzer in analyzers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!isAnalyzerSuppressed(analyzer, compilationOptions, addDiagnostic, continueOnAnalyzerException, cancellationToken))
                {
                    AnalyzerDriverHelper.ExecuteAndCatchIfThrows(analyzer, addDiagnostic, continueOnAnalyzerException, () =>
                    {
                        // The Initialize method should be run asynchronously in case it is not well behaved, e.g. does not terminate.
                        analyzer.Initialize(new AnalyzerAnalysisContext(analyzer, sessionScope));
                    }, cancellationToken);
                }
            }

            return sessionScope;
        }

        private static HostSessionStartAnalysisScope GetSessionAnalysisScope(IEnumerable<DiagnosticAnalyzer> analyzers, CompilationOptions compilationOptions, Action<Diagnostic> addDiagnostic, Func<Exception, DiagnosticAnalyzer, bool> continueOnAnalyzerException, CancellationToken cancellationToken)
        {
            return GetSessionAnalysisScope(analyzers, compilationOptions, IsDiagnosticAnalyzerSuppressed, addDiagnostic, continueOnAnalyzerException, cancellationToken);
        }

        private static void VerifyArguments(
            IEnumerable<ISymbol> symbols,
            Compilation compilation,
            AnalyzerActions actions,
            AnalyzerOptions analyzerOptions,
            Action<Diagnostic> addDiagnostic,
            Func<Exception, DiagnosticAnalyzer, bool> continueOnAnalyzerException)
        {
            if (symbols == null)
            {
                throw new ArgumentNullException(nameof(symbols));
            }

            if (symbols.Any(s => s == null))
            {
                throw new ArgumentException(CodeAnalysisResources.ArgumentElementCannotBeNull, nameof(symbols));
            }

            VerifyArguments(compilation, actions, analyzerOptions, addDiagnostic, continueOnAnalyzerException);
        }

        private static void VerifyArguments(
            Compilation compilation,
            AnalyzerActions actions,
            AnalyzerOptions analyzerOptions,
            Action<Diagnostic> addDiagnostic,
            Func<Exception, DiagnosticAnalyzer, bool> continueOnAnalyzerException)
        {
            if (compilation == null)
            {
                throw new ArgumentNullException(nameof(compilation));
            }

            VerifyArguments(actions, analyzerOptions, addDiagnostic, continueOnAnalyzerException);
        }

        internal static void VerifyArguments(
            SemanticModel semanticModel,
            AnalyzerActions actions,
            AnalyzerOptions analyzerOptions,
            Action<Diagnostic> addDiagnostic,
            Func<Exception, DiagnosticAnalyzer, bool> continueOnAnalyzerException)
        {
            if (semanticModel == null)
            {
                throw new ArgumentNullException(nameof(semanticModel));
            }

            VerifyArguments(actions, analyzerOptions, addDiagnostic, continueOnAnalyzerException);
        }

        private static void VerifyArguments(
            SyntaxTree syntaxTree,
            AnalyzerActions actions,
            AnalyzerOptions analyzerOptions,
            Action<Diagnostic> addDiagnostic,
            Func<Exception, DiagnosticAnalyzer, bool> continueOnAnalyzerException)
        {
            if (syntaxTree == null)
            {
                throw new ArgumentNullException(nameof(syntaxTree));
            }

            VerifyArguments(actions, analyzerOptions, addDiagnostic, continueOnAnalyzerException);
        }

        private static void VerifyArguments(
            Compilation compilation,
            AnalyzerActions actions,
            AnalyzerOptions analyzerOptions,
            DiagnosticAnalyzer analyzer,
            Action<Diagnostic> addDiagnostic,
            Func<Exception, DiagnosticAnalyzer, bool> continueOnAnalyzerException)
        {
            if (compilation == null)
            {
                throw new ArgumentNullException(nameof(compilation));
            }

            if (analyzer == null)
            {
                throw new ArgumentNullException(nameof(analyzer));
            }

            VerifyArguments(actions, analyzerOptions, addDiagnostic, continueOnAnalyzerException);
        }

        internal static void VerifyArguments(
            AnalyzerActions actions,
            AnalyzerOptions analyzerOptions,
            Action<Diagnostic> addDiagnostic,
            Func<Exception, DiagnosticAnalyzer, bool> continueOnAnalyzerException)
        {
            if (actions == null)
            {
                throw new ArgumentNullException(nameof(actions));
            }

            VerifyArguments(analyzerOptions, addDiagnostic, continueOnAnalyzerException);
        }

        internal protected static void VerifyArguments(
            AnalyzerOptions analyzerOptions,
            Action<Diagnostic> addDiagnostic,
            Func<Exception, DiagnosticAnalyzer, bool> continueOnAnalyzerException)
        {
            if (analyzerOptions == null)
            {
                throw new ArgumentNullException(nameof(analyzerOptions));
            }

            if (addDiagnostic == null)
            {
                throw new ArgumentNullException(nameof(addDiagnostic));
            }

            if (continueOnAnalyzerException == null)
            {
                throw new ArgumentNullException(nameof(continueOnAnalyzerException));
            }
        }

        internal protected static void VerifyArguments(
            DiagnosticAnalyzer analyzer,
            Action<Diagnostic> addDiagnostic,
            Func<Exception, DiagnosticAnalyzer, bool> continueOnAnalyzerException)
        {
            if (analyzer == null)
            {
                throw new ArgumentNullException(nameof(analyzer));
            }

            if (addDiagnostic == null)
            {
                throw new ArgumentNullException(nameof(addDiagnostic));
            }

            if (continueOnAnalyzerException == null)
            {
                throw new ArgumentNullException(nameof(continueOnAnalyzerException));
            }
        }

        private static HostCompilationStartAnalysisScope GetCompilationAnalysisScope(HostSessionStartAnalysisScope session, Compilation compilation, AnalyzerOptions analyzerOptions, Action<Diagnostic> addDiagnostic, Func<Exception, DiagnosticAnalyzer, bool> continueOnAnalyzerException, CancellationToken cancellationToken)
        {
            HostCompilationStartAnalysisScope compilationScope = new HostCompilationStartAnalysisScope(session);

            foreach (CompilationStartAnalyzerAction startAction in session.CompilationStartActions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                AnalyzerDriverHelper.ExecuteAndCatchIfThrows(startAction.Analyzer, addDiagnostic, continueOnAnalyzerException, () =>
                {
                    startAction.Action(new AnalyzerCompilationStartAnalysisContext(startAction.Analyzer, compilationScope, compilation, analyzerOptions, cancellationToken));
                }, cancellationToken);
            }

            return compilationScope;
        }

        /// <summary>
        /// Returns true if all the diagnostics that can be produced by this analyzer are suppressed through options.
        /// </summary>
        internal static bool IsDiagnosticAnalyzerSuppressed(
            DiagnosticAnalyzer analyzer,
            CompilationOptions options,
            Action<Diagnostic> addDiagnostic,
            Func<Exception, DiagnosticAnalyzer, bool> continueOnAnalyzerException,
            CancellationToken cancellationToken)
        {
            if (analyzer is CompilerDiagnosticAnalyzer)
            {
                // Compiler analyzer must always be executed for compiler errors, which cannot be suppressed or filtered.
                return false;
            }

            var supportedDiagnostics = ImmutableArray<DiagnosticDescriptor>.Empty;

            // Catch Exception from analyzer.SupportedDiagnostics
            AnalyzerDriverHelper.ExecuteAndCatchIfThrows(analyzer, addDiagnostic, continueOnAnalyzerException, () => { supportedDiagnostics = analyzer.SupportedDiagnostics; }, cancellationToken);

            var diagnosticOptions = options.SpecificDiagnosticOptions;

            foreach (var diag in supportedDiagnostics)
            {
                if (diag.IsNotConfigurable())
                {
                    // If diagnostic descriptor is not configurable, then diagnostics created through it cannot be suppressed.
                    return false;
                }

                // Is this diagnostic suppressed by default (as written by the rule author)
                var isSuppressed = !diag.IsEnabledByDefault;

                // If the user said something about it, that overrides the author.
                if (diagnosticOptions.ContainsKey(diag.Id))
                {
                    isSuppressed = diagnosticOptions[diag.Id] == ReportDiagnostic.Suppress;
                }

                if (isSuppressed)
                {
                    continue;
                }
                else
                {
                    return false;
                }
            }

            return true;
        }

        public void Dispose()
        {
            this.CompilationEventQueue.TryComplete();
            this.DiagnosticQueue.TryComplete();
            this.queueRegistration.Dispose();
        }
    }

    /// <summary>
    /// Driver to execute diagnostic analyzers for a given compilation.
    /// It uses a <see cref="AsyncQueue{TElement}"/> of <see cref="CompilationEvent"/>s to drive its analysis.
    /// </summary>
    internal class AnalyzerDriver<TLanguageKindEnum> : AnalyzerDriver where TLanguageKindEnum : struct
    {
        private Func<SyntaxNode, TLanguageKindEnum> GetKind;
        private ImmutableDictionary<TLanguageKindEnum, ArrayBuilder<SyntaxNodeAnalyzerAction<TLanguageKindEnum>>> lazyNodeActionsByKind = null;

        /// <summary>
        /// Create an analyzer driver.
        /// </summary>
        /// <param name="analyzers">The set of analyzers to include in the analysis</param>
        /// <param name="getKind">A delegate that returns the language-specific kind for a given syntax node</param>
        /// <param name="options">Options that are passed to analyzers</param>
        /// <param name="continueOnAnalyzerException">Delegate which is invoked when an analyzer throws an exception.
        /// If a non-null delegate is provided and it returns true, then the exception is handled and converted into a diagnostic and driver continues with other analyzers.
        /// Otherwise if it returns false, then the exception is not handled by the driver.
        /// If null, then the driver always handles the exception.
        /// </param>
        /// <param name="cancellationToken">a cancellation token that can be used to abort analysis</param>
        internal AnalyzerDriver(ImmutableArray<DiagnosticAnalyzer> analyzers, Func<SyntaxNode, TLanguageKindEnum> getKind, AnalyzerOptions options, Func<Exception, DiagnosticAnalyzer, bool> continueOnAnalyzerException, CancellationToken cancellationToken) : base(analyzers, options, cancellationToken, continueOnAnalyzerException)
        {
            GetKind = getKind;
        }

        private ImmutableDictionary<TLanguageKindEnum, ArrayBuilder<SyntaxNodeAnalyzerAction<TLanguageKindEnum>>> NodeActionsByKind
        {
            get
            {
                if (lazyNodeActionsByKind == null)
                {
                    var nodeActions = this.compilationAnalysisScope.GetSyntaxNodeActions<TLanguageKindEnum>();
                    ImmutableDictionary<TLanguageKindEnum, ArrayBuilder<SyntaxNodeAnalyzerAction<TLanguageKindEnum>>> analyzersByKind;
                    if (nodeActions.Any())
                    {
                        var addDiagnostic = GetDiagnosticSinkWithSuppression();
                        var pooledAnalyzerActionsByKind = PooledDictionary<TLanguageKindEnum, ArrayBuilder<SyntaxNodeAnalyzerAction<TLanguageKindEnum>>>.GetInstance();
                        AnalyzerDriverHelper.GetNodeActionsByKind(nodeActions, pooledAnalyzerActionsByKind, addDiagnostic);
                        analyzersByKind = pooledAnalyzerActionsByKind.ToImmutableDictionary();
                        pooledAnalyzerActionsByKind.Free();
                    }
                    else
                    {
                        analyzersByKind = ImmutableDictionary<TLanguageKindEnum, ArrayBuilder<SyntaxNodeAnalyzerAction<TLanguageKindEnum>>>.Empty;
                    }

                    lazyNodeActionsByKind = analyzersByKind;
                }

                return lazyNodeActionsByKind;
            }
        }

        protected override async Task AnalyzeDeclaringReferenceAsync(SymbolDeclaredCompilationEvent symbolEvent, SyntaxReference decl, Action<Diagnostic> reportDiagnostic, CancellationToken cancellationToken)
        {
            var symbol = symbolEvent.Symbol;
            SemanticModel semanticModel = symbolEvent.SemanticModel(decl);
            var declaringReferenceSyntax = await decl.GetSyntaxAsync().ConfigureAwait(false);
            var syntax = semanticModel.GetTopmostNodeForDiagnosticAnalysis(symbol, declaringReferenceSyntax);

            var statelessNodeActionsByKind = this.NodeActionsByKind;
            var executeSyntaxNodeActions = statelessNodeActionsByKind.Any();
            var executeCodeBlockActions = AnalyzerDriverHelper.CanHaveExecutableCodeBlock(symbol) && (this.compilationAnalysisScope.HasCodeBlockStartActions<TLanguageKindEnum>() || this.compilationAnalysisScope.HasCodeBlockEndActions<TLanguageKindEnum>());

            if (executeSyntaxNodeActions || executeCodeBlockActions)
            {
                // We only care about the top level symbol declaration and its immediate member declarations.
                int? levelsToCompute = 2;

                var declarationsInNode = semanticModel.GetDeclarationsInNode(syntax, getSymbol: syntax != declaringReferenceSyntax, cancellationToken: cancellationToken, levelsToCompute: levelsToCompute);

                // Execute stateless syntax node actions.
                if (executeSyntaxNodeActions)
                {
                    ExecuteStatelessNodeActions(statelessNodeActionsByKind, syntax, symbol, declarationsInNode, semanticModel,
                        reportDiagnostic, this.continueOnAnalyzerException, this.analyzerOptions, this.GetKind, cancellationToken);
                }

                // Execute code block actions.
                if (executeCodeBlockActions)
                {
                    // Compute the executable code blocks of interest.
                    var executableCodeBlocks = ImmutableArray<SyntaxNode>.Empty;
                    foreach (var declInNode in declarationsInNode)
                    {
                        if (declInNode.DeclaredNode == syntax || declInNode.DeclaredNode == declaringReferenceSyntax)
                        {
                            executableCodeBlocks = declInNode.ExecutableCodeBlocks;
                            break;
                        }
                    }

                    if (executableCodeBlocks.Any())
                    {
                        ExecuteCodeBlockActions(this.compilationAnalysisScope,
                            syntax, symbol, executableCodeBlocks, this.analyzerOptions,
                            semanticModel, reportDiagnostic, this.continueOnAnalyzerException, this.GetKind, cancellationToken);
                    }
                }
            }
        }

        private static void ExecuteStatelessNodeActions(
            IDictionary<TLanguageKindEnum, ArrayBuilder<SyntaxNodeAnalyzerAction<TLanguageKindEnum>>> actionsByKind,
            SyntaxNode declaredNode,
            ISymbol declaredSymbol,
            IEnumerable<DeclarationInfo> declarationsInNode,
            SemanticModel semanticModel,
            Action<Diagnostic> addDiagnostic,
            Func<Exception, DiagnosticAnalyzer, bool> continueOnAnalyzerException,
            AnalyzerOptions analyzerOptions,
            Func<SyntaxNode, TLanguageKindEnum> getKind,
            CancellationToken cancellationToken)
        {
            // Eliminate syntax nodes for descendant member declarations within declarations.
            // There will be separate symbols declared for the members, hence we avoid duplicate syntax analysis by skipping these here.
            HashSet<SyntaxNode> descendantDeclsToSkip = null;
            bool first = true;
            foreach (var declInNode in declarationsInNode)
            {
                if (declInNode.DeclaredNode != declaredNode)
                {
                    // Might be a field declaration statement with multiple fields declared.
                    // Adjust syntax node for analysis to be just the field (except for the first field so that we don't skip nodes common to all fields).
                    if (declInNode.DeclaredSymbol == declaredSymbol)
                    {
                        if (!first)
                        {
                            declaredNode = declInNode.DeclaredNode;
                        }

                        continue;
                    }

                    // Compute the topmost node representing the syntax declaration for the member that needs to be skipped.
                    var declarationNodeToSkip = declInNode.DeclaredNode;
                    var declaredSymbolOfDeclInNode = declInNode.DeclaredSymbol ?? semanticModel.GetDeclaredSymbol(declInNode.DeclaredNode, cancellationToken);
                    if (declaredSymbolOfDeclInNode != null)
                    {
                        declarationNodeToSkip = semanticModel.GetTopmostNodeForDiagnosticAnalysis(declaredSymbolOfDeclInNode, declInNode.DeclaredNode);
                    }

                    descendantDeclsToSkip = descendantDeclsToSkip ?? new HashSet<SyntaxNode>();
                    descendantDeclsToSkip.Add(declarationNodeToSkip);
                }

                first = false;
            }

            var nodesToAnalyze = descendantDeclsToSkip == null ?
                declaredNode.DescendantNodesAndSelf(descendIntoTrivia: true) :
                declaredNode.DescendantNodesAndSelf(n => !descendantDeclsToSkip.Contains(n), descendIntoTrivia: true).Except(descendantDeclsToSkip);

            AnalyzerDriverHelper.ExecuteSyntaxNodeActions(nodesToAnalyze, actionsByKind, semanticModel,
                analyzerOptions, addDiagnostic, continueOnAnalyzerException, getKind, cancellationToken);
        }

        private static void VerifyArguments(
            IEnumerable<DeclarationInfo> declarationsInNode,
            Func<SyntaxNode, TLanguageKindEnum> getKind,
            SemanticModel semanticModel,
            AnalyzerActions actions,
            AnalyzerOptions analyzerOptions,
            Action<Diagnostic> addDiagnostic,
            Func<Exception, DiagnosticAnalyzer, bool> continueOnAnalyzerException)
        {
            if (declarationsInNode == null)
            {
                throw new ArgumentNullException(nameof(declarationsInNode));
            }

            VerifyArguments(getKind, semanticModel, actions, analyzerOptions, addDiagnostic, continueOnAnalyzerException);
        }

        private static void VerifyArguments(
            IEnumerable<SyntaxNode> nodes,
            Func<SyntaxNode, TLanguageKindEnum> getKind,
            SemanticModel semanticModel,
            AnalyzerActions actions,
            AnalyzerOptions analyzerOptions,
            Action<Diagnostic> addDiagnostic,
            Func<Exception, DiagnosticAnalyzer, bool> continueOnAnalyzerException)
        {
            if (nodes == null)
            {
                throw new ArgumentNullException(nameof(nodes));
            }

            if (nodes.Any(n => n == null))
            {
                throw new ArgumentException(CodeAnalysisResources.ArgumentElementCannotBeNull, nameof(nodes));
            }

            VerifyArguments(getKind, semanticModel, actions, analyzerOptions, addDiagnostic, continueOnAnalyzerException);
        }

        private static void VerifyArguments(
            Func<SyntaxNode, TLanguageKindEnum> getKind,
            SemanticModel semanticModel,
            AnalyzerActions actions,
            AnalyzerOptions analyzerOptions,
            Action<Diagnostic> addDiagnostic,
            Func<Exception, DiagnosticAnalyzer, bool> continueOnAnalyzerException)
        {
            if (getKind == null)
            {
                throw new ArgumentNullException(nameof(getKind));
            }

            VerifyArguments(semanticModel, actions, analyzerOptions, addDiagnostic, continueOnAnalyzerException);
        }

        private static void ExecuteCodeBlockActions(
            HostCompilationStartAnalysisScope compilationScope,
            SyntaxNode declaredNode,
            ISymbol declaredSymbol,
            ImmutableArray<SyntaxNode> executableCodeBlocks,
            AnalyzerOptions analyzerOptions,
            SemanticModel semanticModel,
            Action<Diagnostic> addDiagnostic,
            Func<Exception, DiagnosticAnalyzer, bool> continueOnAnalyzerException,
            Func<SyntaxNode, TLanguageKindEnum> getKind,
            CancellationToken cancellationToken)
        {
            AnalyzerDriverHelper.ExecuteCodeBlockActions(
                compilationScope.GetCodeBlockStartActions<TLanguageKindEnum>(),
                compilationScope.GetCodeBlockEndActions<TLanguageKindEnum>(),
                declaredNode,
                declaredSymbol,
                executableCodeBlocks,
                analyzerOptions,
                semanticModel,
                addDiagnostic,
                continueOnAnalyzerException,
                getKind,
                cancellationToken);
        }

        public new void Dispose()
        {
            base.Dispose();
            foreach (var kvp in this.NodeActionsByKind)
            {
                kvp.Value.Free();
            }
        }
    }

    internal static class AnalyzerDriverResources
    {
        internal static string AnalyzerFailure => CodeAnalysisResources.CompilerAnalyzerFailure;
        internal static string AnalyzerThrows => CodeAnalysisResources.CompilerAnalyzerThrows;
        internal static string ArgumentElementCannotBeNull => CodeAnalysisResources.ArgumentElementCannotBeNull;
        internal static string ArgumentCannotBeEmpty => CodeAnalysisResources.ArgumentCannotBeEmpty;
    }
}
