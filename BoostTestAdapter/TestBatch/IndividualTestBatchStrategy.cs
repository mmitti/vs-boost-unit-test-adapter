﻿// (C) Copyright ETAS 2015.
// Distributed under the Boost Software License, Version 1.0.
// (See accompanying file LICENSE_1_0.txt or copy at
// http://www.boost.org/LICENSE_1_0.txt)

using System.Collections.Generic;
using System.Linq;
using BoostTestAdapter.Boost.Runner;
using BoostTestAdapter.Settings;
using BoostTestAdapter.Utility;
using BoostTestAdapter.Utility.VisualStudio;
using VSTestCase = Microsoft.VisualStudio.TestPlatform.ObjectModel.TestCase;

namespace BoostTestAdapter.TestBatch
{
    /// <summary>
    /// An ITestBatchingStrategy which allocates a test run per test case.
    /// </summary>
    public class IndividualTestBatchStrategy : TestBatchStrategy
    {
        public IndividualTestBatchStrategy(IBoostTestRunnerFactory testRunnerFactory, BoostTestAdapterSettings settings, CommandLineArgsBuilder argsBuilder) :
            base(testRunnerFactory, settings, argsBuilder)
        {
        }

        #region TestBatchStrategy

        public override IEnumerable<TestRun> BatchTests(IEnumerable<VSTestCase> tests)
        {
            // Group by source
            var sources = tests.GroupBy(test => test.Source);
            foreach (var source in sources)
            {
                IBoostTestRunner runner = GetTestRunner(source.Key);
                if (runner == null)
                {
                    continue;
                }

                // Group by tests individually
                foreach (VSTestCase test in source)
                {
                    BoostTestRunnerCommandLineArgs args = BuildCommandLineArgs(runner.Source);
                    args.Tests.Add(test.GetBoostTestPath());

                    yield return new TestRun(runner, new VSTestCase[] { test }, args, this.Settings.TestRunnerSettings);
                }
            }
        }

        #endregion TestBatchStrategy
    }
}