using System;
using System.Globalization;
using System.IO;
using System.Threading;

namespace SkylineTogether.Core.Tests.Framework
{
    /// <summary>
    /// The runner itself (<see cref="TestRunner"/>): a failing test fails the run, an empty selection and an unknown
    /// option are errors, a timed-out test stops the run, and the culture option reaches the test threads. The fixture
    /// classes below are marked <see cref="RunnerFixtureAttribute"/>, so only these tests run them.
    /// </summary>
    public sealed class RunnerSelfTests
    {
        private static (int ExitCode, string Output) Run(params string[] args)
        {
            var output = new StringWriter(CultureInfo.InvariantCulture);
            int code = TestRunner.Run(new[] { typeof(Fixtures.Passing), typeof(Fixtures.Failing), typeof(Fixtures.Throwing), typeof(Fixtures.Skipped), typeof(Fixtures.Cultured), typeof(Fixtures.Hanging) },
                                      null, args, output, includeFixtures: true);
            return (code, output.ToString());
        }

        [Test]
        public void APassingSelectionExitsWithZero()
        {
            var (code, output) = Run("Fixtures.Passing");
            Assert.Equal(0, code, output);
            Assert.Contains("== 2 passed, 0 failed, 0 skipped", output);
        }

        [Test]
        public void AFailingTestFailsTheRun()
        {
            var (code, output) = Run("Fixtures.Passing", "Fixtures.Failing");
            Assert.Equal(1, code, output);
            Assert.Contains("FAIL Framework.RunnerSelfTests.Fixtures.Failing.AssertsWrongly", output);
            Assert.Contains("actual 2", output);
            Assert.Contains("== 2 passed, 1 failed", output);
        }

        [Test]
        public void AnExceptionFailsTheRunAndEveryFailureCounts()
        {
            var (code, output) = Run("Fixtures.Failing", "Fixtures.Throwing");
            Assert.Equal(2, code, output);
            Assert.Contains("System.InvalidOperationException: boom", output);
        }

        [Test]
        public void SkippedTestsDoNotFailTheRun()
        {
            var (code, output) = Run("Fixtures.Skipped");
            Assert.Equal(0, code, output);
            Assert.Contains("SKIP Framework.RunnerSelfTests.Fixtures.Skipped.ByAttribute: not today", output);
            Assert.Contains("SKIP Framework.RunnerSelfTests.Fixtures.Skipped.AtRunTime: no file", output);
        }

        [Test]
        public void AnEmptySelectionIsAnError()
        {
            var (code, output) = Run("NoSuchTestXyz");
            Assert.Equal(TestRunner.UsageError, code, output);
            Assert.Contains("no test selected", output);
            Assert.Equal(0, Run("NoSuchTestXyz", "--allow-empty").ExitCode, "unless allowed");
        }

        [Test]
        public void UnknownOptionsAreErrorsAndSingleDashesExclude()
        {
            var (code, output) = Run("--verbsoe");
            Assert.Equal(TestRunner.UsageError, code, output);
            Assert.Contains("unknown option --verbsoe", output);
            var (excluded, excludedOutput) = Run("Fixtures", "-Failing", "-Throwing", "-Hanging", "-Cultured", "--verbose");
            Assert.Equal(0, excluded, excludedOutput);
            Assert.Contains("PASS Framework.RunnerSelfTests.Fixtures.Passing.One", excludedOutput);
            Assert.DoesNotContain("Failing", excludedOutput);
        }

        [Test]
        public void TooFewTestsAreAnError()
        {
            var (code, output) = Run("Fixtures.Passing", "--expect-at-least", "3");
            Assert.Equal(TestRunner.UsageError, code, output);
            Assert.Contains("2 test(s) selected, expected at least 3", output);
            Assert.Equal(0, Run("Fixtures.Passing", "--expect-at-least", "2").ExitCode);
            Assert.Equal(TestRunner.UsageError, Run("--expect-at-least").ExitCode, "the number is required");
        }

        [Test]
        public void OnlyExecutedTestsCountTowardsTheMinimum()
        {
            // 4 selected, 2 of them skip: 2 executed
            var (code, output) = Run("Fixtures.Passing", "Fixtures.Skipped", "--expect-at-least", "3");
            Assert.Equal(TestRunner.UsageError, code, output);
            Assert.Contains("ERROR 2 test(s) executed, expected at least 3", output);
            Assert.Equal(0, Run("Fixtures.Passing", "Fixtures.Skipped", "--expect-at-least", "2").ExitCode);
            var (failing, failingOutput) = Run("Fixtures.Failing", "--expect-at-least", "1");
            Assert.Equal(1, failing, "a failed test was executed: " + failingOutput);
        }

        [Test]
        public void SkippedTestsFailTheRunAboveTheirLimit()
        {
            var (code, output) = Run("Fixtures.Passing", "Fixtures.Skipped", "--max-skipped", "1");
            Assert.Equal(TestRunner.UsageError, code, output);
            Assert.Contains("ERROR 2 test(s) skipped, at most 1 allowed", output);
            Assert.Equal(0, Run("Fixtures.Passing", "Fixtures.Skipped", "--max-skipped", "2").ExitCode);
            Assert.Equal(0, Run("Fixtures.Passing", "--max-skipped", "0").ExitCode);
            Assert.Equal(TestRunner.UsageError, Run("--max-skipped").ExitCode, "the number is required");
        }

        [Test]
        public void ListingRunsNothing()
        {
            var (code, output) = Run("Fixtures.Failing", "--list");
            Assert.Equal(0, code, output);
            Assert.Contains("Framework.RunnerSelfTests.Fixtures.Failing.AssertsWrongly", output);
            Assert.DoesNotContain("FAIL", output);
        }

        [Test]
        public void CanariesRunOnlyWhenAskedAndFail()
        {
            var (code, output) = Run("Fixtures.Passing", "--canary");
            Assert.Equal(1, code, output);
            Assert.Contains("FAIL Framework.RunnerSelfTests.Fixtures.Passing.Canary", output);
            Assert.Contains("== 0 passed, 1 failed", output);
        }

        [Test]
        public void TheCultureReachesTestThreadsAndTheirWorkers()
        {
            Assert.Equal(0, Run("Fixtures.Cultured.IsInvariantByDefault").ExitCode, "invariant by default");
            var (code, output) = Run("Fixtures.Cultured.IsGerman", "--culture", "de-DE");
            Assert.Equal(0, code, output);
            Assert.Contains("(culture de-DE)", output);
            Assert.Equal(TestRunner.UsageError, Run("--culture").ExitCode);
        }

        [Test(TimeoutSeconds = 30)]
        public void ATimedOutTestStopsTheRun()
        {
            int runsBefore = Fixtures.Passing.Runs;
            try
            {
                var (code, output) = Run("Fixtures.Hanging", "Fixtures.Passing");
                // sorted by name: Hanging.Blocks, then Passing.One and Passing.Two, which must not run after the timeout
                Assert.Equal(3, code, output);
                Assert.Contains("FAIL Framework.RunnerSelfTests.Fixtures.Hanging.Blocks", output);
                Assert.Contains("timed out after 1 s", output);
                Assert.Contains("ABORT", output);
                Assert.Contains("not run: Framework.RunnerSelfTests.Fixtures.Passing.One", output);
                Assert.Contains("(2 not run)", output);
                Assert.Equal(runsBefore, Fixtures.Passing.Runs, "nothing ran after the timeout");
            }
            finally
            {
                Fixtures.Hanging.Release.Set(); // let the abandoned thread end
            }
        }

        [RunnerFixture]
        internal static class Fixtures
        {
            public sealed class Passing
            {
                public static int Runs;

                [Test]
                public void One() => Interlocked.Increment(ref Runs);

                [Test]
                public void Two() => Interlocked.Increment(ref Runs);

                [Test(Canary = true)]
                public void Canary() => Assert.Fail("a canary always fails");
            }

            public sealed class Failing
            {
                [Test]
                public void AssertsWrongly() => Assert.Equal(1, 2);
            }

            public sealed class Throwing
            {
                [Test]
                public void Throws() => throw new InvalidOperationException("boom");
            }

            public sealed class Skipped
            {
                [Test(Skip = "not today")]
                public void ByAttribute() => throw new InvalidOperationException("must not run");

                [Test]
                public void AtRunTime() => Assert.Skip("no file");
            }

            public sealed class Cultured
            {
                [Test]
                public void IsInvariantByDefault() => Assert.Equal(string.Empty, CultureInfo.CurrentCulture.Name);

                [Test]
                public void IsGerman()
                {
                    Assert.Equal("de-DE", CultureInfo.CurrentCulture.Name);
                    Assert.Equal("1,5", 1.5.ToString(CultureInfo.CurrentCulture));
                    string worker = System.Threading.Tasks.Task.Run(() => CultureInfo.CurrentCulture.Name).Result;
                    Assert.Equal("de-DE", worker, "thread-pool work inherits the culture");
                }
            }

            public sealed class Hanging
            {
                public static readonly ManualResetEventSlim Release = new ManualResetEventSlim(false);

                [Test(TimeoutSeconds = 1)]
                public void Blocks() => Release.Wait(TimeSpan.FromSeconds(20));
            }
        }
    }
}
