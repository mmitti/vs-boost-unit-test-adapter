﻿// (C) Copyright ETAS 2015.
// Distributed under the Boost Software License, Version 1.0.
// (See accompanying file LICENSE_1_0.txt or copy at
// http://www.boost.org/LICENSE_1_0.txt)

// This file has been modified by Microsoft on 8/2017.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using BoostTestAdapter.Boost.Results;
using BoostTestAdapter.Boost.Results.LogEntryTypes;
using BoostTestAdapter.Boost.Test;
using Microsoft.VisualStudio.TestPlatform.ObjectModel;
using VSTestCase = Microsoft.VisualStudio.TestPlatform.ObjectModel.TestCase;
using VSTestOutcome = Microsoft.VisualStudio.TestPlatform.ObjectModel.TestOutcome;
using VSTestResult = Microsoft.VisualStudio.TestPlatform.ObjectModel.TestResult;
using System.Globalization;

namespace BoostTestAdapter.Utility.VisualStudio
{
    /// <summary>
    /// Static class hosting utility methods related to the
    /// Visual Studio Test object model.
    /// </summary>
    public static class VSTestModel
    {
        /// <summary>
        /// TestSuite trait name
        /// </summary>
        public static string TestSuiteTrait { get; } = "TestSuite";

        /// <summary>
        /// Test Status (Enabled/Disabled) trait name
        /// </summary>
        public static string StatusTrait { get; } = "Status";

        /// <summary>
        /// Boost.Test Test Path test property
        /// </summary>
        public static TestProperty TestPathProperty { get; } = TestProperty.Register("Boost.Test.Test.Path", "Boost.Test Test Path", typeof(string), typeof(VSTestModel));

        /// <summary>
        /// Boost.Test Boost Version property
        /// </summary>
        public static TestProperty VersionProperty { get; } = TestProperty.Register("Boost.Test.Boost.Version", "Boost Version", typeof(string), typeof(VSTestModel));

        /// <summary>
        /// Converts forward slashes in a file path to backward slashes.
        /// </summary>
        /// <param name="path_in"> The input path</param>
        /// <returns>The output path, modified with backward slashes </returns>

        private static string ConvertSlashes(string path_in)
        {
            return path_in.Replace('/', '\\');
        }

        /// <summary>
        /// Constant Used to indicate that the test is Enabled
        /// </summary>
        public static string TestEnabled
        {
            get
            {
                return "Enabled";
            }
        }

        /// <summary>
        /// Constant Used to indicate that the test is Disabled
        /// </summary>
        public static string TestDisabled
        {
            get
            {
                return "Disabled";
            }
        }


        /// <summary>
        /// Converts a Boost.Test.Result.TestResult model into an equivalent
        /// Microsoft.VisualStudio.TestPlatform.ObjectModel.TestResult model.
        /// </summary>
        /// <param name="result">The Boost.Test.Result.TestResult model to convert.</param>
        /// <param name="test">The Microsoft.VisualStudio.TestPlatform.ObjectModel.TestCase model which is related to the result.</param>
        /// <returns>The Boost.Test.Result.TestResult model converted into its Microsoft.VisualStudio.TestPlatform.ObjectModel.TestResult counterpart.</returns>
        public static VSTestResult AsVSTestResult(this BoostTestAdapter.Boost.Results.TestResult result, VSTestCase test)
        {
            Utility.Code.Require(result, "result");
            Utility.Code.Require(test, "test");

            VSTestResult vsResult = new VSTestResult(test);

            vsResult.ComputerName = Environment.MachineName;

            vsResult.Outcome = GetTestOutcome(result.Result);

            // Boost.Test.Result.TestResult.Duration is in microseconds
            
            // 1 millisecond = 10,000 ticks
            // => 1 microsecond = 10 ticks
            // Reference: https://msdn.microsoft.com/en-us/library/zz841zbz(v=vs.110).aspx
            long ticks = (long) Math.Min((result.Duration * 10), long.MaxValue);
            
            // Clamp tick count to 1 in case Boost duration is listed as 0
            vsResult.Duration = new TimeSpan(Math.Max(ticks, 1));

            if (result.LogEntries.Count > 0)
            {
                foreach (TestResultMessage message in GetTestMessages(result))
                {
                    vsResult.Messages.Add(message);
                }

                // Test using the TestOutcome type since elements from the
                // Boost Result type may be collapsed into a particular value
                if (vsResult.Outcome == VSTestOutcome.Failed)
                {
                    LogEntry error = GetLastError(result);

                    if (error != null)
                    {
                        vsResult.ErrorMessage = GetErrorMessage(result);
                        
                        if (error.Source != null)
                        {
                            //String format for a hyper linkable Stack Trace
                            //Reference: NUnit3 Test Adapter.
                            vsResult.ErrorStackTrace = string.Format(CultureInfo.InvariantCulture, "at {0}() in {1}:line {2}", vsResult.TestCase.DisplayName, ConvertSlashes(error.Source.File), error.Source.LineNumber);
                        }
                    }
                }
            }

            return vsResult;
        }

        /// <summary>
        /// Converts a Boost.Test.Result.Result enumeration into an equivalent
        /// Microsoft.VisualStudio.TestPlatform.ObjectModel.TestOutcome.
        /// </summary>
        /// <param name="result">The Boost.Test.Result.Result value to convert.</param>
        /// <returns>The Boost.Test.Result.Result enumeration converted into Microsoft.VisualStudio.TestPlatform.ObjectModel.TestOutcome.</returns>
        private static VSTestOutcome GetTestOutcome(TestResultType result)
        {
            switch (result)
            {
                case TestResultType.Passed: return VSTestOutcome.Passed;
                case TestResultType.Skipped: return VSTestOutcome.Skipped;

                case TestResultType.Failed:
                case TestResultType.Aborted:
                default: return VSTestOutcome.Failed;
            }
        }

        /// <summary>
        /// Converts the log entries stored within the provided test result into equivalent
        /// Microsoft.VisualStudio.TestPlatform.ObjectModel.TestResultMessages
        /// </summary>
        /// <param name="result">The Boost.Test.Result.TestResult whose LogEntries are to be converted.</param>
        /// <returns>An enumeration of TestResultMessage equivalent to the Boost log entries stored within the provided TestResult.</returns>
        private static IEnumerable<TestResultMessage> GetTestMessages(BoostTestAdapter.Boost.Results.TestResult result)
        {
            foreach (LogEntry entry in result.LogEntries)
            {
                string category = null;

                if (
                    (entry is LogEntryInfo) ||
                    (entry is LogEntryMessage) ||
                    (entry is LogEntryStandardOutputMessage)
                )
                {
                    category = TestResultMessage.StandardOutCategory;
                }
                else if (
                    (entry is LogEntryWarning) ||
                    (entry is LogEntryError) ||
                    (entry is LogEntryFatalError) ||
                    (entry is LogEntryMemoryLeak) ||
                    (entry is LogEntryException) ||
                    (entry is LogEntryStandardErrorMessage)
                )
                {
                    category = TestResultMessage.StandardErrorCategory;
                }
                else
                {
                    // Skip unknown message types
                    continue;
                }

                yield return new TestResultMessage(category, GetTestResultMessageText(result.Unit, entry));
            }
        }
        
        /// <summary>
        /// Given a log entry and its respective test unit, returns a string
        /// formatted similar to the compiler_log_formatter.ipp in the Boost Test framework.
        /// </summary>
        /// <param name="unit">The test unit related to this log entry</param>
        /// <param name="entry">The log entry</param>
        /// <returns>A string message using a similar format as specified within compiler_log_formatter.ipp in the Boost Test framework</returns>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Globalization", "CA1308:NormalizeStringsToUppercase")]
        private static string GetTestResultMessageText(TestUnit unit, LogEntry entry)
        {
            Code.Require(unit, "unit");
            Code.Require(entry, "entry");

            if ((entry is LogEntryStandardOutputMessage) || (entry is LogEntryStandardErrorMessage))
            {
                return entry.Detail.TrimEnd(null) + Environment.NewLine;
            }

            StringBuilder sb = new StringBuilder();

            if (entry.Source != null)
            {
                AppendSourceInfo(entry.Source, sb);
            }

            sb.Append(entry.ToString().ToLowerInvariant()).
                Append(" in \"").
                Append(unit.Name).
                Append("\"");

            LogEntryMemoryLeak memoryLeak = entry as LogEntryMemoryLeak;
            if (memoryLeak == null)
            {
                sb.Append(": ").Append(entry.Detail.TrimEnd(null));
            }

            LogEntryException exception = entry as LogEntryException;
            if (exception != null)
            {
                FormatException(exception, sb);
            }

            if ((entry.ContextFrames != null) && (entry.ContextFrames.Any()))
            {
                FormatContextFrames(entry, sb);
            }

            if (memoryLeak != null)
            {
                FormatMemoryLeak(memoryLeak, sb);
            }

            // Append NewLine so that log entries are listed one per line
            return sb.Append(Environment.NewLine).ToString();
        }

        /// <summary>
        /// Formats a LogEntryException to append to test result string
        /// </summary>
        /// <param name="exception">The exception to format</param>
        /// <param name="sb">The StringBuilder which will host the output</param>
        /// <returns>sb</returns>
        private static StringBuilder FormatException(LogEntryException exception, StringBuilder sb)
        {
            if (exception.LastCheckpoint != null)
            {
                sb.Append(Environment.NewLine);
                AppendSourceInfo(exception.LastCheckpoint, sb);
                sb.Append("last checkpoint: ").Append(exception.CheckpointDetail);
            }

            return sb;
        }

        /// <summary>
        /// Formats the context frames of a LogEntry and appends it to test result string
        /// </summary>
        /// <param name="entry">The log entry to format</param>
        /// <param name="sb">The StringBuilder which will host the output</param>
        /// <returns>sb</returns>
        private static StringBuilder FormatContextFrames(LogEntry entry, StringBuilder sb)
        {
            sb.Append(Environment.NewLine).
                Append("Occurred in a following context:").
                Append(Environment.NewLine);

            foreach (string frame in entry.ContextFrames)
            {
                sb.Append("    ").Append(frame).Append(Environment.NewLine);
            }

            // Remove redundant NewLine at the end
            sb.Remove((sb.Length - Environment.NewLine.Length), Environment.NewLine.Length);
            
            return sb;
        }

        /// <summary>
        /// Formats a LogEntryMemoryLeak to append to test result string
        /// </summary>
        /// <param name="memoryLeak">The memory leak to format</param>
        /// <param name="sb">The StringBuilder which will host the output</param>
        /// <returns>sb</returns>
        private static StringBuilder FormatMemoryLeak(LogEntryMemoryLeak memoryLeak, StringBuilder sb)
        {
            if (memoryLeak.Source != null)
            {
                sb.Append("source file path leak detected at :").
                    Append(memoryLeak.Source.File);

                if (memoryLeak.Source.LineNumber != -1)
                {
                    sb.Append(", Line number: ").
                        Append(memoryLeak.Source.LineNumber);
                }
            }
            
            sb.Append(", Memory allocation number: ").
                Append(memoryLeak.LeakMemoryAllocationNumber);

            sb.Append(", Leak size: ").
                Append(memoryLeak.LeakSizeInBytes).
                Append(" byte");

            if (memoryLeak.LeakSizeInBytes > 0)
            {
                sb.Append('s');
            }

            sb.Append(Environment.NewLine).
                Append(memoryLeak.LeakLeakedDataContents);

            return sb;
        }

        /// <summary>
        /// Compresses a message so that it is suitable for the UI.
        /// </summary>
        /// <param name="result">The erroneous LogEntry whose message is to be displayed.</param>
        /// <returns>A compressed message suitable for UI.</returns>
        private static string GetErrorMessage(BoostTestAdapter.Boost.Results.TestResult result)
        {
            StringBuilder sb = new StringBuilder();

            foreach (LogEntry error in GetErrors(result))
            {
                sb.Append(error.Detail).Append(Environment.NewLine);
            }

            // Remove redundant NewLine at the end
            sb.Remove((sb.Length - Environment.NewLine.Length), Environment.NewLine.Length);

            return sb.ToString();
        }

        /// <summary>
        /// Appends the SourceInfo instance information to the provided StringBuilder.
        /// </summary>
        /// <param name="info">The SourceInfo instance to stringify.</param>
        /// <param name="sb">The StringBuilder which will host the result.</param>
        /// <returns>sb</returns>
        private static StringBuilder AppendSourceInfo(SourceFileInfo info, StringBuilder sb)
        {
            return sb.Append(info).Append(": ");
        }

        /// <summary>
        /// Given a TestResult returns the last error type log entry.
        /// </summary>
        /// <param name="result">The TestResult which hosts the necessary log entries</param>
        /// <returns>The last error type log entry or null if none are available.</returns>
        private static LogEntry GetLastError(BoostTestAdapter.Boost.Results.TestResult result)
        {
            // Select the last error issued within a Boost Test report
            return GetErrors(result).LastOrDefault();
        }

        /// <summary>
        /// Enumerates all log entries which are deemed to be an error (i.e. Warning, Error, Fatal Error and Exception).
        /// </summary>
        /// <param name="result">The TestResult which hosts the log entries.</param>
        /// <returns>An enumeration of error flagging log entries.</returns>
        private static IEnumerable<LogEntry> GetErrors(BoostTestAdapter.Boost.Results.TestResult result)
        {
            IEnumerable<LogEntry> errors = result.LogEntries.Where((e) =>
                                                    (e is LogEntryWarning) ||
                                                    (e is LogEntryError) ||
                                                    (e is LogEntryFatalError) ||
                                                    (e is LogEntryException)
                                               );

            // Only provide a single memory leak error if the test succeeded successfully (i.e. all asserts passed)
            return (errors.Any() ? errors : result.LogEntries.Where((e) => (e is LogEntryMemoryLeak)).Take(1));
        }

        /// <summary>
        /// Identifies the Boost.Test test path of the provided Visual Studio test case
        /// </summary>
        /// <param name="testcase">Visual Studio test case (mapping to Boost.Test test case)</param>
        /// <returns>The Boost.Test path of the test case which is identified by the Visual Studio counterpart</returns>
        public static string GetBoostTestPath(this VSTestCase testcase)
        {
            return testcase.GetPropertyValue(TestPathProperty).ToString();
        }

        /// <summary>
        /// Specifies the Boost.Test test path of the provided Visual Studio test case
        /// </summary>
        /// <param name="testcase">Visual Studio test case (mapping to Boost.Test test case)</param>
        public static void SetBoostTestPath(this VSTestCase testcase, string value)
        {
            testcase.SetPropertyValue(TestPathProperty, value);
        }
    }
}