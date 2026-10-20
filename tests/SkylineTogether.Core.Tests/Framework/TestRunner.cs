using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading;

namespace SkylineTogether.Core.Tests.Framework
{
    /// <summary>
    /// Discovers every <see cref="TestAttribute"/> method of an assembly and runs them one after another.
    /// <para>
    /// Arguments: plain words are name filters (a test runs when its name contains any of them, case-insensitive);
    /// a word starting with a single <c>-</c> excludes matching tests. Options:
    /// <c>--list</c> prints the selected names without running them; <c>--verbose</c> prints every test result;
    /// <c>--allow-empty</c> accepts a selection without tests (otherwise an error); <c>--expect-at-least N</c> fails the
    /// run when fewer than N tests are selected (CI pins the size of the suite); <c>--culture NAME</c> runs every test
    /// under that culture (default: the invariant culture); <c>--canary</c> runs only the canary tests, which are made to
    /// fail (CI checks that a failing test fails the run). Any other option starting with <c>--</c> is an error.
    /// Test names are <c>Area.Class.Method</c>, where Area is the namespace below <c>SkylineTogether.Core.Tests</c>
    /// (for example <c>Buffers.ByteWriterTests.RoundTripsVarints</c>).
    /// </para>
    /// <para>
    /// The process exit code is the number of failed tests, capped at 125 (so shells never wrap it to 0); a usage error
    /// or an empty selection is 126. A test that exceeds its time limit cannot be stopped (its thread keeps running), so
    /// the run stops there: the remaining tests are reported as not run and counted as failed.
    /// </para>
    /// </summary>
    public static class TestRunner
    {
        private const string RootNamespace = "SkylineTogether.Core.Tests.";

        /// <summary>Exit code of a usage error or an empty selection.</summary>
        public const int UsageError = 126;

        private sealed class TestCase
        {
            public string Name;
            public MethodInfo Method;
            public TestAttribute Attribute;
        }

        private enum Outcome { Passed, Failed, Skipped, TimedOut }

        private sealed class Options
        {
            public readonly List<string> Includes = new List<string>();
            public readonly List<string> Excludes = new List<string>();
            public bool List, Verbose, AllowEmpty, Canary;
            public int ExpectAtLeast;
            public CultureInfo Culture = CultureInfo.InvariantCulture;
        }

        public static int Run(Assembly assembly, string[] args)
        {
            Type[] types;
            string loadError = null;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException e)
            {
                types = Array.FindAll(e.Types, t => t != null);
                loadError = "type load: " + e.Message;
            }
            return Run(types, loadError, args, Console.Out, includeFixtures: false);
        }

        /// <summary>
        /// Runs the tests of <paramref name="types"/> and writes the report to <paramref name="output"/>; returns the exit
        /// code. Classes marked <see cref="RunnerFixtureAttribute"/> are skipped unless <paramref name="includeFixtures"/>
        /// (the runner's own self-tests run them).
        /// </summary>
        internal static int Run(IReadOnlyList<Type> types, string loadError, string[] args, TextWriter output, bool includeFixtures)
        {
            if (!TryParse(args, out Options options, out string usage))
            {
                output.WriteLine("ERROR " + usage);
                output.WriteLine("usage: [name filters] [-excluded] [--list] [--verbose] [--allow-empty] [--expect-at-least N] [--culture NAME] [--canary]");
                return UsageError;
            }

            List<TestCase> tests = Discover(types, options, includeFixtures, out List<string> discoveryErrors);
            if (loadError != null) discoveryErrors.Insert(0, loadError);
            foreach (string error in discoveryErrors) output.WriteLine("INVALID " + error);
            if (tests.Count == 0 && !options.AllowEmpty)
            {
                output.WriteLine("ERROR no test selected" + Filters(options) + " (pass --allow-empty to accept that)");
                return UsageError;
            }
            if (tests.Count < options.ExpectAtLeast)
            {
                output.WriteLine("ERROR " + tests.Count + " test(s) selected, expected at least " + options.ExpectAtLeast
                                 + ": tests were lost (a class or attribute no longer discovered?)");
                return UsageError;
            }
            if (options.List)
            {
                foreach (TestCase test in tests) output.WriteLine(test.Name);
                output.WriteLine("== " + tests.Count + " test(s) selected");
                return Math.Min(discoveryErrors.Count, 125);
            }

            CultureInfo previousDefault = CultureInfo.DefaultThreadCurrentCulture;
            CultureInfo previousDefaultUi = CultureInfo.DefaultThreadCurrentUICulture;
            // worker threads the code under test starts (thread pool, Task.Run) inherit the culture too
            CultureInfo.DefaultThreadCurrentCulture = options.Culture;
            CultureInfo.DefaultThreadCurrentUICulture = options.Culture;
            try
            {
                return RunAll(tests, discoveryErrors.Count, options, output);
            }
            finally
            {
                CultureInfo.DefaultThreadCurrentCulture = previousDefault;
                CultureInfo.DefaultThreadCurrentUICulture = previousDefaultUi;
            }
        }

        private static int RunAll(List<TestCase> tests, int invalid, Options options, TextWriter output)
        {
            int passed = 0, failed = invalid, skipped = 0, notRun = 0;
            var total = Stopwatch.StartNew();
            for (int i = 0; i < tests.Count; i++)
            {
                TestCase test = tests[i];
                var watch = Stopwatch.StartNew();
                Outcome outcome = Execute(test, options.Culture, out string detail);
                watch.Stop();
                switch (outcome)
                {
                    case Outcome.Passed:
                        passed++;
                        if (options.Verbose) output.WriteLine("PASS " + test.Name + " (" + Millis(watch) + ")");
                        break;
                    case Outcome.Skipped:
                        skipped++;
                        output.WriteLine("SKIP " + test.Name + ": " + detail);
                        break;
                    default:
                        failed++;
                        output.WriteLine("FAIL " + test.Name + " (" + Millis(watch) + ")");
                        foreach (string line in detail.Split('\n'))
                        {
                            output.WriteLine("     " + line.TrimEnd('\r'));
                        }
                        break;
                }
                if (outcome == Outcome.TimedOut)
                {
                    // the test's thread cannot be stopped and may still change shared state: nothing after it is trustworthy
                    notRun = tests.Count - i - 1;
                    if (notRun > 0)
                    {
                        output.WriteLine("ABORT the run stops after a timed-out test; " + notRun + " test(s) not run (counted as failed):");
                        for (int k = i + 1; k < tests.Count; k++) output.WriteLine("     not run: " + tests[k].Name);
                    }
                    failed += notRun;
                    break;
                }
            }
            total.Stop();
            output.WriteLine("== " + passed + " passed, " + failed + " failed, " + skipped + " skipped in "
                             + total.Elapsed.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture) + " s"
                             + (notRun > 0 ? " (" + notRun + " not run)" : string.Empty)
                             + Filters(options)
                             + (options.Culture.Name.Length > 0 ? " (culture " + options.Culture.Name + ")" : string.Empty));
            return Math.Min(failed, 125);
        }

        private static string Filters(Options options) => options.Includes.Count > 0 ? " (filter: " + string.Join(" ", options.Includes) + ")" : string.Empty;

        private static bool TryParse(string[] args, out Options options, out string error)
        {
            options = new Options();
            error = null;
            args = args ?? Array.Empty<string>();
            for (int i = 0; i < args.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(args[i])) continue;
                string arg = args[i].Trim();
                switch (arg)
                {
                    case "--list":
                        options.List = true;
                        continue;
                    case "--verbose":
                        options.Verbose = true;
                        continue;
                    case "--allow-empty":
                        options.AllowEmpty = true;
                        continue;
                    case "--canary":
                        options.Canary = true;
                        continue;
                    case "--expect-at-least":
                        if (i + 1 >= args.Length || !int.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out options.ExpectAtLeast))
                        {
                            error = "--expect-at-least needs a number";
                            return false;
                        }
                        i++;
                        continue;
                    case "--culture":
                        if (i + 1 >= args.Length)
                        {
                            error = "--culture needs a culture name (for example de-DE)";
                            return false;
                        }
                        try
                        {
                            options.Culture = CultureInfo.GetCultureInfo(args[++i]);
                        }
                        catch (CultureNotFoundException)
                        {
                            error = "unknown culture " + args[i] + " (is InvariantGlobalization on?)";
                            return false;
                        }
                        continue;
                }
                if (arg.StartsWith("--", StringComparison.Ordinal))
                {
                    error = "unknown option " + arg + " (exclude filters take a single dash: -Name)";
                    return false;
                }
                if (arg.StartsWith("-", StringComparison.Ordinal) && arg.Length > 1) options.Excludes.Add(arg.Substring(1));
                else options.Includes.Add(arg);
            }
            return true;
        }

        private static List<TestCase> Discover(IReadOnlyList<Type> types, Options options, bool includeFixtures, out List<string> errors)
        {
            errors = new List<string>();
            var tests = new List<TestCase>();
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
            foreach (Type type in types)
            {
                if (!includeFixtures && IsFixture(type)) continue;
                foreach (MethodInfo method in type.GetMethods(flags))
                {
                    var attribute = method.GetCustomAttribute<TestAttribute>();
                    if (attribute == null) continue;
                    string name = NameOf(type, method);
                    if (method.GetParameters().Length != 0 || method.ReturnType != typeof(void) || method.IsGenericMethodDefinition)
                    {
                        errors.Add(name + ": a test method takes no parameters and returns void");
                        continue;
                    }
                    if (!method.IsStatic && (type.IsAbstract || type.GetConstructor(Type.EmptyTypes) == null))
                    {
                        errors.Add(name + ": instance tests need a concrete class with a public parameterless constructor");
                        continue;
                    }
                    if (attribute.Canary != options.Canary) continue;
                    if (!Selected(name, options.Includes, options.Excludes)) continue;
                    tests.Add(new TestCase { Name = name, Method = method, Attribute = attribute });
                }
            }
            tests.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            return tests;
        }

        private static bool IsFixture(Type type)
        {
            for (Type t = type; t != null; t = t.DeclaringType)
            {
                if (t.GetCustomAttribute<RunnerFixtureAttribute>() != null) return true;
            }
            return false;
        }

        private static string NameOf(Type type, MethodInfo method)
        {
            string ns = type.Namespace ?? string.Empty;
            if (ns.StartsWith(RootNamespace, StringComparison.Ordinal)) ns = ns.Substring(RootNamespace.Length);
            else if (ns == "SkylineTogether.Core.Tests") ns = string.Empty;
            string typeName = type.FullName ?? type.Name;
            int dot = typeName.LastIndexOf('.');
            if (dot >= 0) typeName = typeName.Substring(dot + 1);
            typeName = typeName.Replace('+', '.');
            return (ns.Length > 0 ? ns + "." : string.Empty) + typeName + "." + method.Name;
        }

        private static bool Selected(string name, List<string> includes, List<string> excludes)
        {
            foreach (string exclude in excludes)
            {
                if (name.IndexOf(exclude, StringComparison.OrdinalIgnoreCase) >= 0) return false;
            }
            if (includes.Count == 0) return true;
            foreach (string include in includes)
            {
                if (name.IndexOf(include, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        private static Outcome Execute(TestCase test, CultureInfo culture, out string detail)
        {
            if (test.Attribute.Skip != null)
            {
                detail = test.Attribute.Skip;
                return Outcome.Skipped;
            }
            Outcome outcome = Outcome.Failed;
            string message = null;
            var thread = new Thread(() =>
            {
                try
                {
                    object instance = test.Method.IsStatic ? null : Activator.CreateInstance(test.Method.DeclaringType);
                    try
                    {
                        test.Method.Invoke(instance, null);
                    }
                    finally
                    {
                        (instance as IDisposable)?.Dispose();
                    }
                    outcome = Outcome.Passed;
                }
                catch (Exception e)
                {
                    Exception inner = Unwrap(e);
                    if (inner is SkipException)
                    {
                        outcome = Outcome.Skipped;
                        message = inner.Message;
                    }
                    else if (inner is AssertionException)
                    {
                        outcome = Outcome.Failed;
                        message = inner.Message + Environment.NewLine + Location(inner);
                    }
                    else
                    {
                        outcome = Outcome.Failed;
                        message = inner.GetType().FullName + ": " + inner.Message + Environment.NewLine + Trim(inner.StackTrace, 8);
                    }
                }
            }, 64 * 1024 * 1024);
            thread.IsBackground = true;
            thread.Name = test.Name;
            thread.CurrentCulture = culture;
            thread.CurrentUICulture = culture;
            thread.Start();
            int timeoutMs = Math.Max(1, test.Attribute.TimeoutSeconds) * 1000;
            if (!thread.Join(timeoutMs))
            {
                detail = "timed out after " + test.Attribute.TimeoutSeconds + " s";
                return Outcome.TimedOut;
            }
            detail = message ?? string.Empty;
            return outcome;
        }

        private static Exception Unwrap(Exception e)
        {
            while (e is TargetInvocationException && e.InnerException != null) e = e.InnerException;
            return e;
        }

        private static string Location(Exception e)
        {
            // the first stack frame outside the framework is the failing assertion's caller
            if (e.StackTrace == null) return string.Empty;
            foreach (string line in e.StackTrace.Split('\n'))
            {
                if (line.IndexOf("Tests.Framework.Assert", StringComparison.Ordinal) >= 0) continue;
                return line.Trim();
            }
            return string.Empty;
        }

        private static string Trim(string stackTrace, int maxLines)
        {
            if (stackTrace == null) return string.Empty;
            string[] lines = stackTrace.Split('\n');
            var kept = new List<string>();
            foreach (string line in lines)
            {
                string t = line.Trim();
                if (t.Length == 0 || t.StartsWith("at System.RuntimeMethodHandle", StringComparison.Ordinal)
                    || t.StartsWith("at System.Reflection.", StringComparison.Ordinal)) continue;
                kept.Add(t);
                if (kept.Count == maxLines) break;
            }
            return string.Join(Environment.NewLine, kept);
        }

        private static string Millis(Stopwatch watch) => watch.Elapsed.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture) + " ms";
    }

    /// <summary>
    /// Marks a class whose tests exist only for the runner's own self-tests (<see cref="FrameworkSelfTests"/>): normal
    /// discovery skips it and everything nested in it.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    public sealed class RunnerFixtureAttribute : Attribute
    {
    }
}
