﻿// (C) Copyright ETAS 2015.
// Distributed under the Boost Software License, Version 1.0.
// (See accompanying file LICENSE_1_0.txt or copy at
// http://www.boost.org/LICENSE_1_0.txt)

// This file has been modified by Microsoft on 4/2018.

using BoostTestAdapter.Boost.Results;
using BoostTestAdapter.Boost.Runner;
using BoostTestAdapter.Settings;
using BoostTestAdapter.TestBatch;
using BoostTestAdapter.Utility;
using BoostTestAdapter.Utility.ExecutionContext;
using BoostTestAdapter.Utility.VisualStudio;
using Microsoft.VisualStudio.TestPlatform.ObjectModel;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Adapter;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Xml;
using BoostTestResult = BoostTestAdapter.Boost.Results.TestResult;
using VSTestCase = Microsoft.VisualStudio.TestPlatform.ObjectModel.TestCase;
using VSTestResult = Microsoft.VisualStudio.TestPlatform.ObjectModel.TestResult;

namespace BoostTestAdapter
{
    /// <summary>
    /// Implementation of ITestExecutor interface for Boost Tests.
    /// </summary>
    [ExtensionUri(ExecutorUriString)]
    public class BoostTestExecutor : ITestExecutor, IDisposable
    {
        #region Constants

        public const string ExecutorUriString = "executor://BoostTestExecutor/v1";
        public static readonly Uri ExecutorUri = new Uri(ExecutorUriString);

        // Error issued by Boost Test when a test cannot be executed.
        // This string is duplicated in several places for localization:
        // - Resources.TestNotFound
        // - Resources.TestNameContainsSpaces
        // - Resources.TestNameContainsCommas
        private const string TestNotFound = "Test setup error: no test cases matching filter";

        /// <summary>
        /// Static class aggregating constant file extensions.
        /// </summary>
        internal static class FileExtensions
        {
            public const string LogFile = ".test.log.xml";
            public const string ReportFile = ".test.report.xml";
            public const string StdOutFile = ".test.stdout.log";
            public const string StdErrFile = ".test.stderr.log";
        }

        #endregion Constants

        #region Constructors

        /// <summary>
        /// Default constructor
        /// </summary>
        public BoostTestExecutor()
        {
            _testRunnerFactory = new DefaultBoostTestRunnerFactory();
            _packageServiceFactory = new DefaultBoostTestPackageServiceFactory();
            _boostTestDiscovererFactory = new BoostTestDiscovererFactory(_testRunnerFactory, _packageServiceFactory);
        }

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="testRunnerFactory">The IBoostTestRunnerFactory to be used</param>
        /// <param name="boostTestDiscovererFactory">The IBoostTestDiscovererFactory to be used</param>
        /// <param name="packageServiceFactory">The IBoostTestDiscovererFactory to be used</param>
        public BoostTestExecutor(IBoostTestRunnerFactory testRunnerFactory, IBoostTestDiscovererFactory boostTestDiscovererFactory, IBoostTestPackageServiceFactory packageServiceFactory)
        {
            _testRunnerFactory = testRunnerFactory;
            _boostTestDiscovererFactory = boostTestDiscovererFactory;
            _packageServiceFactory = packageServiceFactory;
        }

        #endregion Constructors
        
        #region Member variables

        /// <summary>
        /// Cancel flag
        /// </summary>
        private CancellationTokenSource _cancelled = null;

        /// <summary>
        /// Boost Test Discoverer Factory - provisions test discoverers
        /// </summary>
        private readonly IBoostTestDiscovererFactory _boostTestDiscovererFactory;

        /// <summary>
        /// Boost Test Runner Factory - provisions test runners
        /// </summary>
        private readonly IBoostTestRunnerFactory _testRunnerFactory;

        /// <summary>
        /// The Boost Test Package Service factory
        /// </summary>
        private readonly IBoostTestPackageServiceFactory _packageServiceFactory;

        #endregion Member variables
        
        /// <summary>
        /// Initialization routine for running tests
        /// </summary>
        /// <param name="logger">The logger which will be used to emit log messages</param>
        private void SetUp(IMessageLogger logger)
        {
#if DEBUG && LAUNCH_DEBUGGER
            System.Diagnostics.Debugger.Launch();
#endif

            _cancelled = new CancellationTokenSource();
            Logger.Initialize(logger);
        }

        /// <summary>
        /// Termination/Cleanup routine for running tests
        /// </summary>
        private void TearDown()
        {
            _cancelled.Dispose();
            Logger.Shutdown();
        }

        /// <summary>
        /// Filters out any tests which are not intended to run
        /// </summary>
        /// <param name="settings">Adapter settings which determines test filtering</param>
        /// <param name="tests">The entire test corpus</param>
        /// <returns>A test corpus which contains only the test which are intended to run</returns>
        private static IEnumerable<VSTestCase> GetTestsToRun(BoostTestAdapterSettings settings, IEnumerable<VSTestCase> tests)
        {
            IEnumerable<VSTestCase> testsToRun = tests;

            if (!settings.RunDisabledTests)
            {
                testsToRun = tests.Where((test) =>
                {
                    foreach (var trait in test.Traits)
                    {
                        if ((trait.Name == VSTestModel.StatusTrait) && (trait.Value == VSTestModel.TestEnabled))
                        {
                            return true;
                        }
                    }

                    return false;
                });
            }

            return testsToRun;
        }

        #region ITestExecutor

        /// <summary>
        /// Execute the tests one by one. Run All.
        /// </summary>
        /// <param name="sources">Collection of test modules (exe/dll)</param>
        /// <param name="runContext">Solution properties</param>
        /// <param name="frameworkHandle">Unit test framework handle</param>
        /// <remarks>Entry point of the execution procedure whenever the user requests to run all the tests</remarks>
        public void RunTests(IEnumerable<string> sources,
            IRunContext runContext,
            IFrameworkHandle frameworkHandle)
        {
            Code.Require(sources, "sources");
            Code.Require(runContext, "runContext");
            Code.Require(frameworkHandle, "frameworkHandle");

            SetUp(frameworkHandle);

            Logger.Debug("IRunContext.IsDataCollectionEnabled: {0}", runContext.IsDataCollectionEnabled);
            Logger.Debug("IRunContext.RunSettings.SettingsXml: {0}", runContext.RunSettings.SettingsXml);

            BoostTestAdapterSettings settings = BoostTestAdapterSettingsProvider.GetSettings(runContext);
            
            foreach (string source in sources)
            {
                if (_cancelled.IsCancellationRequested)
                {
                    break;
                }

                var discoverer = _boostTestDiscovererFactory.GetDiscoverer(source, settings);
                if (discoverer != null)
                {
                    try
                    {
                        DefaultTestCaseDiscoverySink sink = new DefaultTestCaseDiscoverySink();

                        // NOTE IRunContext implements IDiscoveryContext
                        // NOTE IFrameworkHandle implements IMessageLogger

                        // Re-discover tests so that we could make use of the RunTests overload which takes an enumeration of test cases.
                        // This is necessary since we need to run tests one by one in order to have the test adapter remain responsive
                        // and have a list of tests over which we can generate test results for.
                        discoverer.DiscoverTests(new[] { source }, runContext, sink);
                        
                        // The following ensures that only test cases that are not disabled are run when the user presses "Run all"
                        // This, however, can be overridden by the .runsettings file supplied
                        IEnumerable<TestCase> testsToRun = GetTestsToRun(settings, sink.Tests);

                        // Batch tests into grouped runs based by source so that we avoid reloading symbols per test run
                        // Batching by source since this overload is called when 'Run All...' or equivalent is triggered
                        // NOTE For code-coverage speed is given preference over adapter responsiveness.
                        Strategy strategy = ((runContext.IsDataCollectionEnabled) ? Strategy.Source : settings.TestBatchStrategy);

                        ITestBatchingStrategy batchStrategy = GetBatchStrategy(strategy, settings, runContext);
                        if (batchStrategy == null)
                        {
                            Logger.Error(Resources.BatchStrategyNotFoundFor, source);
                            continue;
                        }

                        IEnumerable<TestRun> batches = batchStrategy.BatchTests(testsToRun);

                        // Delegate to the RunBoostTests overload which takes an enumeration of test batches
                        RunBoostTests(batches, runContext, frameworkHandle);
                    }
                    catch (Exception ex)
                    {
                        Logger.Error(Resources.TestException, source, ex.Message);
                    }
                }
            }

            TearDown();
        }

        /// <summary>
        /// Execute the tests one by one. Run Selected
        /// </summary>
        /// <param name="tests">Testcases object</param>
        /// <param name="runContext">Solution properties</param>
        /// <param name="frameworkHandle">Unit test framework handle</param>
        /// <remarks>Entry point of the execution procedure whenever the user requests to run one or a specific lists of tests</remarks>
        public void RunTests(IEnumerable<VSTestCase> tests, IRunContext runContext, IFrameworkHandle frameworkHandle)
        {
            Code.Require(tests, "tests");
            Code.Require(runContext, "runContext");
            Code.Require(frameworkHandle, "frameworkHandle");

            SetUp(frameworkHandle);

            Logger.Debug("IRunContext.IsDataCollectionEnabled: {0}", runContext.IsDataCollectionEnabled);
            Logger.Debug("IRunContext.RunSettings.SettingsXml: {0}", runContext.RunSettings.SettingsXml);

            BoostTestAdapterSettings settings = BoostTestAdapterSettingsProvider.GetSettings(runContext);

            // Batch tests into grouped runs based on test source and test suite so that we minimize symbol reloading
            //
            // NOTE Required batching at test suite level since Boost Unit Test Framework command-line arguments only allow
            //      multiple test name specification for tests which reside in the same test suite
            //
            // NOTE For code-coverage speed is given preference over adapter responsiveness.
            TestBatch.Strategy strategy = ((runContext.IsDataCollectionEnabled) ? Strategy.One : settings.TestBatchStrategy);
            // Source strategy is invalid in such context since explicit tests are chosen. TestSuite is used instead.
            if (strategy == Strategy.Source)
            {
                strategy = Strategy.TestSuite;
            }

            ITestBatchingStrategy batchStrategy = GetBatchStrategy(strategy, settings, runContext);
            if (batchStrategy == null)
            {
                Logger.Error(Resources.BatchStrategyNotFound);
            }
            else
            {
                // NOTE Apply distinct to avoid duplicate test cases. Common issue when using BOOST_DATA_TEST_CASE.
                IEnumerable<TestRun> batches = batchStrategy.BatchTests(tests.Distinct(new TestCaseComparer()));
                RunBoostTests(batches, runContext, frameworkHandle);
            }

            TearDown();
        }

        /// <summary>
        /// Cancel the execution of tests
        /// </summary>
        public void Cancel()
        {
            _cancelled.Cancel();
        }

        #endregion ITestExecutor

        #region Test Batching

        /// <summary>
        /// Provides a test batching strategy based on the provided arguments
        /// </summary>
        /// <param name="strategy">The base strategy to provide</param>
        /// <param name="settings">Adapter settings currently in use</param>
        /// <param name="runContext">The RunContext for this TestCase. Determines whether the test should be debugged or not.</param>
        /// <returns>An ITestBatchingStrategy instance or null if one cannot be provided</returns>
        private ITestBatchingStrategy GetBatchStrategy(TestBatch.Strategy strategy, BoostTestAdapterSettings settings, IRunContext runContext)
        {
            TestBatch.CommandLineArgsBuilder argsBuilder = (string _source, BoostTestAdapterSettings _settings) =>
            {
                return GetDefaultArguments(_source, _settings, runContext.IsBeingDebugged);
            };

            if (strategy != Strategy.TestCase)
            {
                // Disable stdout, stderr and memory leak detection since it is difficult
                // to distinguish from which test does portions of the output map to
                argsBuilder = (string _source, BoostTestAdapterSettings _settings) =>
                {
                    var args = GetDefaultArguments(_source, _settings, runContext.IsBeingDebugged);

                    // Disable standard error/standard output capture
                    args.StandardOutFile = null;
                    args.StandardErrorFile = null;

                    // Disable memory leak detection
                    args.DetectMemoryLeaks = 0;

                    return args;
                };
            }

            switch (strategy)
            {
                case Strategy.Source: return new SourceTestBatchStrategy(_testRunnerFactory, settings, argsBuilder);
                case Strategy.TestSuite: return new TestSuiteTestBatchStrategy(_testRunnerFactory, settings, argsBuilder);
                case Strategy.TestCase: return new IndividualTestBatchStrategy(_testRunnerFactory, settings, argsBuilder);
                case Strategy.One: return new OneShotTestBatchStrategy(_testRunnerFactory, settings, argsBuilder);
            }

            return null;
        }

        #endregion Test Batching

        #region Helper methods

        /// <summary>
        /// Run tests one test at a time and update results back to framework.
        /// </summary>
        /// <param name="testBatches">List of test batches to run</param>
        /// <param name="runContext">Solution properties</param>
        /// <param name="frameworkHandle">Unit test framework handle</param>
        private void RunBoostTests(IEnumerable<TestRun> testBatches, IRunContext runContext, IFrameworkHandle frameworkHandle)
        {
            BoostTestAdapterSettings settings = BoostTestAdapterSettingsProvider.GetSettings(runContext);

            foreach (TestRun batch in testBatches)
            {
                if (_cancelled.IsCancellationRequested)
                {
                    break;
                }

                DateTimeOffset start = new DateTimeOffset(DateTime.Now);

                try
                {
                    foreach (var test in batch.Tests)
                    {
                        frameworkHandle.RecordStart(test);
                    }

                    Logger.Info(((runContext.IsBeingDebugged) ? Resources.Debugging : Resources.Executing), string.Join(", ", batch.Tests));

                    using (TemporaryFile report = new TemporaryFile(batch.Arguments.ReportFile))
                    using (TemporaryFile log    = new TemporaryFile(batch.Arguments.LogFile))
                    using (TemporaryFile stdout = new TemporaryFile(batch.Arguments.StandardOutFile))
                    using (TemporaryFile stderr = new TemporaryFile(batch.Arguments.StandardErrorFile))
                    {
                        Logger.Debug(Resources.WorkingDirectory, batch.Arguments.WorkingDirectory ?? "(null)");
                        Logger.Debug(Resources.ReportFile, batch.Arguments.ReportFile);
                        Logger.Debug(Resources.LogFile, batch.Arguments.LogFile);
                        Logger.Debug(Resources.StdOutFile, batch.Arguments.StandardOutFile ?? "(null)");
                        Logger.Debug(Resources.StdErrFile, batch.Arguments.StandardErrorFile ?? "(null)");

                        Logger.Debug(Resources.CmdLineArguments, batch.Arguments.ToString() ?? "(null)");

                        // Execute the tests
                        if (ExecuteTests(batch, runContext, frameworkHandle))
                        {
                            if (settings.PostTestDelay > 0)
                            {
                                Thread.Sleep(settings.PostTestDelay);
                            }
                            
                            foreach (VSTestResult result in GenerateTestResults(batch, start, settings))
                            {
                                frameworkHandle.RecordEnd(result.TestCase, result.Outcome);

                                // Identify test result to Visual Studio Test framework
                                frameworkHandle.RecordResult(result);
                            }
                        }
                    }
                }
                catch (Boost.Runner.TimeoutException ex)
                {
                    foreach (VSTestCase testCase in batch.Tests)
                    {
                        VSTestResult testResult = GenerateTimeoutResult(testCase, ex);
                        testResult.StartTime = start;

                        frameworkHandle.RecordEnd(testResult.TestCase, testResult.Outcome);
                        frameworkHandle.RecordResult(testResult);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Exception(ex, Resources.BatchException, batch.Source, string.Join(", ", batch.Tests), ex.Message);
                }
            }
        }
        
        /// <summary>
        /// Executes the provided test batch
        /// </summary>
        /// <param name="run">The test batch which will be executed.</param>
        /// <param name="runContext">The RunContext for this TestCase. Determines whether the test should be debugged or not.</param>
        /// <param name="frameworkHandle">The FrameworkHandle for this test execution instance.</param>
        /// <returns></returns>
        private bool ExecuteTests(TestRun run, IRunContext runContext, IFrameworkHandle frameworkHandle)
        {
            if (run.Runner != null)
            {
                using (var context = CreateExecutionContext(runContext, frameworkHandle))
                using (var cancel = new CancellationTokenSource())
                {
                    // Associate the test-batch local cancellation source to the global cancellation source
                    // so that if the global source is canceled, the local source is also canceled
                    _cancelled.Token.Register(cancel.Cancel);

                    try
                    {
                        if (!run.ExecuteAsync(context, cancel.Token).Wait(run.Settings.Timeout))
                        {
                            cancel.Cancel();
                            throw new Boost.Runner.TimeoutException(run.Settings.Timeout);
                        }
                    }
                    catch (AggregateException)
                    {
                        // Suppress internal task exceptions or cancellations.
                        //
                        // This is a common scenario when attempting to request the exit code
                        // of a process which is executed through the debugger. In such cases
                        // assume a successful exit scenario. Should this not be the case, the
                        // adapter will 'naturally' fail in other instances e.g. when attempting
                        // to read test reports.
                    }

                    // This will return false in case the global cancellation source has been canceled
                    return !cancel.IsCancellationRequested;
                }
            }
            else
            {
                Logger.Error(Resources.ExecutorNotFound, string.Join(", ", run.Tests));
            }

            return false;
        }

        /// <summary>
        /// Retrieves and assigns parameters by resolving configurations from different possible resources
        /// </summary>
        /// <param name="source">The TestCases source</param>
        /// <param name="settings">The Boost Test adapter settings currently in use</param>
        /// <returns>A string for the default working directory</returns>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes")]
        private void GetDebugConfigurationProperties(string source, BoostTestAdapterSettings settings, BoostTestRunnerCommandLineArgs args)
        {
            try
            {
                using (var packageService = _packageServiceFactory.Create(settings.ParentVSProcessId))
                {
                    args.SetWorkingEnvironment(source, settings, packageService);
                }
            }
            catch (Exception ex)
            {
                Logger.Debug(Resources.WorkingDirectoryNotFoundEx, ex.Message);
            }
        }

        /// <summary>
        /// Factory function which returns an appropriate BoostTestRunnerCommandLineArgs structure
        /// </summary>
        /// <param name="source">The TestCases source</param>
        /// <param name="settings">The Boost Test adapter settings currently in use</param>
        /// <param name="debugMode">Determines whether the test should be debugged or not.</param>
        /// <returns>A BoostTestRunnerCommandLineArgs structure for the provided source</returns>
        private BoostTestRunnerCommandLineArgs GetDefaultArguments(string source, BoostTestAdapterSettings settings, bool debugMode)
        {
            BoostTestRunnerCommandLineArgs args = settings.CommandLineArgs.Clone();
            
            GetDebugConfigurationProperties(source, settings, args);
            
            // Specify log and report file information
            args.LogFormat = OutputFormat.XML;
            args.LogLevel = settings.LogLevel;
            args.LogFile = TestPathGenerator.Generate(source, FileExtensions.LogFile);

            args.ReportFormat = OutputFormat.XML;
            args.ReportLevel = ReportLevel.Detailed;
            args.ReportFile = TestPathGenerator.Generate(source, FileExtensions.ReportFile);
            
            args.StandardOutFile = ((settings.EnableStdOutRedirection) ? TestPathGenerator.Generate(source, FileExtensions.StdOutFile) : null);
            args.StandardErrorFile = ((settings.EnableStdErrRedirection) ? TestPathGenerator.Generate(source, FileExtensions.StdErrFile) : null);

            // Set '--catch_system_errors' to 'yes' if the test is not being debugged
            // or if this value was not overridden via configuration before-hand
            args.CatchSystemErrors = args.CatchSystemErrors.GetValueOrDefault(false) || !debugMode;

            return args;
        }

        /// <summary>
        /// Generates TestResults based on Boost Test result output.
        /// </summary>
        /// <param name="testRun">The tests which have been executed in the prior test run.</param>
        /// <param name="start">The test execution start time.</param>
        /// <param name="settings">boost test adapter settings</param>
        /// <returns>A Visual Studio TestResult related to the executed test.</returns>
        private static IEnumerable<VSTestResult> GenerateTestResults(TestRun testRun, DateTimeOffset start, BoostTestAdapterSettings settings)
        {
            return GenerateTestResults(testRun, start, DateTimeOffset.Now, settings);
        }

        /// <summary>
        /// Generates TestResults based on Boost Test result output.
        /// </summary>
        /// <param name="testRun">The tests which have been executed in the prior test run.</param>
        /// <param name="start">The test execution start time.</param>
        /// <param name="end">The test execution end time.</param>
        /// <param name="settings">boost test adapter settings</param>
        /// <returns>A Visual Studio Test result related to the executed test.</returns>
        private static IEnumerable<VSTestResult> GenerateTestResults(TestRun testRun, DateTimeOffset start, DateTimeOffset end, BoostTestAdapterSettings settings)
        {
            IDictionary<string, BoostTestResult> results;

            try
            {
                results = BoostTestResultParser.Parse(testRun.Arguments, settings);
            }
            catch (XmlException)
            {
                string text = ((File.Exists(testRun.Arguments.ReportFile)) ? File.ReadAllText(testRun.Arguments.ReportFile) : string.Empty);

                if (text.Trim().StartsWith(TestNotFound, StringComparison.Ordinal))
                {
                    return testRun.Tests.Select(GenerateNotFoundResult);
                }
                else
                {
                    // Represent result parsing exception as a test fatal error
                    if (string.IsNullOrEmpty(text))
                    {
                        text = Resources.ResultFileNotFound;
                    }
                    
                    return testRun.Tests.Select(test => {
                        var exception = new BoostTestResult();
                        
                        exception.Unit = Boost.Test.TestUnit.FromFullyQualifiedName(test.GetBoostTestPath());

                        // NOTE Divide by 10 to compensate for duration calculation described in VSTestResult.AsVSTestResult(this Boost.Results.TestResult, VSTestCase)
                        exception.Duration = ((ulong)(end - start).Ticks) / 10;

                        exception.Result = TestResultType.Failed;
                        exception.LogEntries.Add(new Boost.Results.LogEntryTypes.LogEntryFatalError()
                        {
                            Detail = text
                        });

                        return GenerateResult(test, exception, start, end);
                    });
                }
            }

            return testRun.Tests.
                Select(test =>
                {
                    // Locate the test result associated to the current test
                    BoostTestResult result = null;
                    return (results.TryGetValue(test.GetBoostTestPath(), out result)) ? GenerateResult(test, result, start, end) : null;
                }).
                Where(result => (result != null));
        }

        /// <summary>
        /// Converts a Boost Test Result into an equivalent Visual Studio Test result
        /// </summary>
        /// <param name="test">The test case under consideration</param>
        /// <param name="result">The Boost test result for the test case under consideration</param>
        /// <param name="start">The test starting time</param>
        /// <param name="end">The test ending time</param>
        /// <returns>A Visual Studio test result equivalent to the Boost Test result</returns>
        private static VSTestResult GenerateResult(VSTestCase test, BoostTestResult result, DateTimeOffset start, DateTimeOffset end)
        {
            Code.Require(test, "test");
            Code.Require(result, "result");

            // Convert the Boost.Test.Result data structure into an equivalent Visual Studio model
            VSTestResult vsResult = result.AsVSTestResult(test);
            vsResult.StartTime = start;
            vsResult.EndTime = end;

            return vsResult;
        }

        /// <summary>
        /// Generates a default TestResult for a timeout exception.
        /// </summary>
        /// <param name="test">The test which failed due to a timeout.</param>
        /// <param name="ex">The exception related to this timeout.</param>
        /// <returns>A timed-out, failed TestResult related to the provided test.</returns>
        private static VSTestResult GenerateTimeoutResult(VSTestCase test, Boost.Runner.TimeoutException ex)
        {
            VSTestResult result = new VSTestResult(test);

            result.ComputerName = Environment.MachineName;

            result.Outcome = TestOutcome.Failed;
            result.Duration = TimeSpan.FromMilliseconds(ex.Timeout);
            result.ErrorMessage = String.Format(Resources.Timeout, ex.Timeout);

            if (!string.IsNullOrEmpty(test.CodeFilePath))
            {
                result.ErrorStackTrace = new SourceFileInfo(test.CodeFilePath, test.LineNumber).ToString();
            }

            return result;
        }

        /// <summary>
        /// Generates a default TestResult for a 'test not found' exception.
        /// </summary>
        /// <param name="test">The test which failed due to a timeout.</param>
        /// <returns>A timed-out, failed TestResult related to the provided test.</returns>
        private static VSTestResult GenerateNotFoundResult(VSTestCase test)
        {
            VSTestResult result = new VSTestResult(test);

            result.ComputerName = Environment.MachineName;

            result.Outcome = TestOutcome.Skipped;
            result.ErrorMessage = GetNotFoundErrorMessage(test);

            return result;
        }

        /// <summary>
        /// Provides a suitable message in case the provided test is not found.
        /// </summary>
        /// <param name="test">The test which was not found.</param>
        /// <returns>A suitable 'not-found' for the provided test case.</returns>
        private static string GetNotFoundErrorMessage(VSTestCase test)
        {
            if (test.GetBoostTestPath().Contains(' '))
            {
                return Resources.TestNameContainsSpaces;
            }
            else if (test.GetBoostTestPath().Contains(','))
            {
                return Resources.TestNameContainsCommas;
            }

            return Resources.TestNotFound;
        }

        /// <summary>
        /// Generates an execution context. Debug executions are handled via Visual Studio's
        /// debug mechanisms and regular executions guarantee managed sub-processes
        /// (i.e. sub-processes are implicitly killed when parent is killed).
        /// </summary>
        /// <param name="context">The test execution run context</param>
        /// <param name="framework">IFrameworkHandle possessing debug capabilities</param>
        /// <returns>An IProcessExecutionContext capable of spawning sub-processes</returns>
        private static IProcessExecutionContext CreateExecutionContext(IRunContext context, IFrameworkHandle framework)
        {
            if ((context != null) && (context.IsBeingDebugged) && (framework != null))
            {
                return new DebugFrameworkExecutionContext(framework);
            }

            return new DefaultProcessExecutionContext();
        }

        #endregion Helper methods

        #region IDisposable Support

        private bool disposedValue = false; // To detect redundant calls

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    _cancelled.Dispose();
                }

                disposedValue = true;
            }
        }

        // This code added to correctly implement the disposable pattern.
        public void Dispose()
        {
            // Do not change this code. Put cleanup code in Dispose(bool disposing) above.
            Dispose(true);
        }

        #endregion
    }
}