﻿// (C) Copyright ETAS 2015.
// Distributed under the Boost Software License, Version 1.0.
// (See accompanying file LICENSE_1_0.txt or copy at
// http://www.boost.org/LICENSE_1_0.txt)

// This file has been modified by Microsoft on 8/2017.

using BoostTestAdapter.Utility;
using BoostTestAdapter.Utility.ExecutionContext;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Threading;
using System.Threading.Tasks;

namespace BoostTestAdapter.Boost.Runner
{
    /// <summary>
    /// Abstract IBoostTestRunner specification which contains common functionality
    /// for executing external '.exe' Boost Test runners.
    /// </summary>
    public abstract class BoostTestRunnerBase : IBoostTestRunner
    {
        #region Constructors

        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="testRunnerExecutable">Path to the '.exe' file.</param>
        protected BoostTestRunnerBase(string testRunnerExecutable)
        {
            this.TestRunnerExecutable = testRunnerExecutable;
        }

        #endregion Constructors
        
        #region Properties

        /// <summary>
        /// Boost.Test runner '.exe' file path
        /// </summary>
        protected string TestRunnerExecutable { get; private set; }

        /// <summary>
        /// Caches Boost.Test runner capabilities
        /// </summary>
        private BoostTestRunnerCapabilities _capabilities = null;
        
        #endregion Properties

        #region IBoostTestRunner

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes")]
        public Task<int> ExecuteAsync(BoostTestRunnerCommandLineArgs args, BoostTestRunnerSettings settings, IProcessExecutionContext executionContext, CancellationToken token)
        {
            Utility.Code.Require(executionContext, "executionContext");

            var source = new TaskCompletionSource<int>();
            var process = executionContext.LaunchProcess(GetExecutionContextArgs(args, settings));

            process.Exited += (object obj, EventArgs ev) =>
            {
                try
                {
                    source.TrySetResult(process.ExitCode);
                }
                catch (Exception ex)
                {
                    source.TrySetException(ex);
                }
            };

            try
            {
                process.EnableRaisingEvents = true;
            }
            catch (Exception ex)
            {
                source.TrySetException(ex);
            }

            token.Register(() => { source.TrySetCanceled(); });

            return source.Task.ContinueWith((Task<int> result) =>
            {
                if (result.Status != TaskStatus.RanToCompletion)
                {
                    KillProcessIncludingChildren(process);
                }

                process.Dispose();

                return result.Result;
            });
        }
        
        public virtual string Source
        {
            get { return this.TestRunnerExecutable; }
        }
        
        public virtual IBoostTestRunnerCapabilities Capabilities
        {
            get
            {
                if (_capabilities == null)
                {
                    _capabilities = GetCapabilities();
                }

                return _capabilities;
            }
        }
        
        #endregion IBoostTestRunner
        
        /// <summary>
        /// Provides a ProcessExecutionContextArgs structure containing the necessary information to launch the test process.
        /// Aggregates the BoostTestRunnerCommandLineArgs structure with the command-line arguments specified at configuration stage.
        /// </summary>
        /// <param name="args">The Boost Test Framework command line arguments</param>
        /// <param name="settings">The Boost Test Runner settings</param>
        /// <returns>A valid ProcessExecutionContextArgs structure to launch the test executable</returns>
        protected virtual ProcessExecutionContextArgs GetExecutionContextArgs(BoostTestRunnerCommandLineArgs args, BoostTestRunnerSettings settings)
        {
            Code.Require(args, "args");

            return new ProcessExecutionContextArgs()
            {
                FilePath = this.TestRunnerExecutable,
                WorkingDirectory = args.WorkingDirectory,
                Arguments = args.ToString(),
                EnvironmentVariables = args.Environment
            };
        }

        /// <summary>
        /// Kills a process identified by its pid and all its children processes
        /// </summary>
        /// <param name="process">process object</param>
        /// <returns></returns>
        private static void KillProcessIncludingChildren(Process process)
        {
            Logger.Info(Resources.FindingChildren, process.Id);

            // Once the children pids are available we start killing the processes.
            // Enumerate each and every child immediately via the .toList() method.
            List<Process> children = EnumerateChildren(process).ToList();

            // Killing the main process
            if (KillProcess(process))
            {
                Logger.Error(Resources.TerminatedProcess, process.Id);
            }
            else
            {
                Logger.Error(Resources.FailedToTerminateProcess, process.Id);
            }

            foreach (Process child in children)
            {
                // Recurse
                KillProcessIncludingChildren(child);
            }
        }

        /// <summary>
        /// Enumerates all live children of the provided parent Process instance.
        /// </summary>
        /// <param name="process">The parent process whose live children are to be enumerated</param>
        /// <returns>An enumeration of live/active child processes</returns>
        private static IEnumerable<Process> EnumerateChildren(Process process)
        {
            ManagementObjectSearcher searcher = new ManagementObjectSearcher("SELECT ProcessId FROM Win32_Process WHERE ParentProcessId = " + process.Id.ToString());
            ManagementObjectCollection collection = searcher.Get();

            foreach (ManagementBaseObject item in collection)
            {
                int childPid = Convert.ToInt32(item["ProcessId"]);

                Process child = null;

                try
                {
                    child = Process.GetProcessById(childPid);
                }
                catch (ArgumentException /* ex */)
                {
                    Logger.Error(Resources.ProcessNotFound, childPid);
                    // Reset child to null so that it is not enumerated
                    child = null;
                }
                catch (Exception /* ex */)
                {
                    child = null;
                }

                if (child != null)
                {
                    yield return child;
                }
            }
        }

        /// <summary>
        /// Kill a process immediately
        /// </summary>
        /// <param name="process">process object</param>
        /// <returns>return true if success or false if it was not successful</returns>
        private static bool KillProcess(Process process)
        {
            return KillProcess(process, 0);
        }

        /// <summary>
        /// Kill a process
        /// </summary>
        /// <param name="process">process object</param>
        /// <param name="killTimeout">the timeout in milliseconds to note correct process termination</param>
        /// <returns>return true if success or false if it was not successful</returns>
        private static bool KillProcess(Process process, int killTimeout)
        {
            if (process.HasExited)
            {
                return true;
            }

            try
            {
                // If the call to the Kill method is made while the process is already terminating, a Win32Exception is thrown for Access Denied.

                process.Kill();
                return process.WaitForExit(killTimeout);
            }
            catch (Exception e)
            {
                Logger.Error(e.Message);
            }

            return false;
        }

        /// <summary>
        /// Boost.Test 'strings' for '--list_content' which are encoded in a Boost.Test runner/executable
        /// </summary>
        private static readonly ByteUtilities.BoyerMooreBytePattern[] list_content_markers = new []
        {
            // Help text
            new ByteUtilities.BoyerMooreBytePattern("Lists the content of test tree - names of all test suites and test cases.", System.Text.Encoding.ASCII),
             // Environment Variable
            new ByteUtilities.BoyerMooreBytePattern("BOOST_TEST_LIST_CONTENT", System.Text.Encoding.ASCII),
            // Command-line argument
            new ByteUtilities.BoyerMooreBytePattern("list_content", System.Text.Encoding.ASCII)
        };

        /// <summary>
        /// Boost.Test 'strings' for '--version' which are encoded in a Boost.Test runner/executable
        /// </summary>
        private static readonly ByteUtilities.BoyerMooreBytePattern[] version_markers = new[]
        {
            // Help text
            new ByteUtilities.BoyerMooreBytePattern("Prints Boost.Test version and exits.", System.Text.Encoding.ASCII)
        };

        /// <summary>
        /// Acquires the Boost.Test runner's capabilities by looking up markers from the test module
        /// </summary>
        /// <returns>The Boost.Test runner's capabilities</returns>
        private BoostTestRunnerCapabilities GetCapabilities()
        {
            using (new Utility.TimedScope("Looking up '--list_content' and '--version' via Boyer-Moore string search"))
            {
                var listContent = true;

                // Search symbols on the TestRunner not on the source. Source could be .dll which may not contain list_content functionality.
                using (DebugHelper dbgHelp = CreateDebugHelper(this.TestRunnerExecutable))
                {
                    if (dbgHelp != null)
                    {
                        listContent = DebugHelper.FindImport(this.TestRunnerExecutable, "boost_unit_test_framework", StringComparison.OrdinalIgnoreCase);
                    }
                }

                var buffer = System.IO.File.ReadAllBytes(this.TestRunnerExecutable);

                // At least 3 markers need to be available to ensure that the test module is
                // a Boost.Test runner with '--list_content' capabilities
                listContent = listContent || list_content_markers.Select(marker => buffer.IndexOf(marker)).All(index => index >= 0);
                
                if (!listContent)
                {
                    Logger.Warn(Resources.CouldNotLocateDebugSymbols, this.TestRunnerExecutable);
                }

                // Don't bother checking for the '--version' symbol if '--list_content' is not available
                var version = listContent && version_markers.Select(marker => buffer.IndexOf(marker)).All(index => index >= 0);

                return new BoostTestRunnerCapabilities
                {
                    ListContent = listContent,
                    Version = version
                };
            }
        }

        /// <summary>
        /// Creates a DebugHelper instance for the specified source
        /// </summary>
        /// <param name="source">The module/source for which to inspect debug symbols</param>
        /// <returns>A new DebugHelper instance for the specified source or null if one cannot be created</returns>
        private static DebugHelper CreateDebugHelper(string source)
        {
            try
            {
                return new DebugHelper(source);
            }
            catch (Win32Exception ex)
            {
                Logger.Exception(ex, Resources.CouldNotCreateDbgHelp, source);
            }

            return null;
        }
    }
}
