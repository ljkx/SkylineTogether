using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Threading;

namespace SkylineTogether.Core.Tests.Framework
{
    /// <summary>
    /// Discovers every <see cref="TestAttribute"/> method of an assembly and runs them one after another.
    /// <para>
    /// Arguments: plain words are name filters (a test runs when its name contains any of them, case-insensitive);
    /// a word starting with <c>-</c> excludes matching tests; <c>--list</c> prints the selected names without running
    /// them; <c>--verbose</c> prints every test result. Test names are <c>Area.Class.Method</c>, where Area is the
    /// namespace below <c>SkylineTogether.Core.Tests</c> (for example <c>Buffers.ByteWriterTests.RoundTripsVarints</c>).
    /// </para>
    /// <para>The process exit code is the number of failed tests, capped at 125 (so shells never wrap it to 0).</para>
    /// </summary>
    public static class TestRunner
    {
        private const string RootNamespace = "SkylineTogether.Core.Tests.";

        private sealed class TestCase
        {
            public string Name;
            public MethodInfo Method;
            public TestAttribute Attribute;
        }

        private enum Outcome { Passed, Failed, Skipped }

        public static int Run(Assembly assembly, string[] args)
        {
            var includes = new List<string>();
            var excludes = new List<string>();
            bool list = false, verbose = false;
            foreach (string raw in args ?? Array.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                string arg = raw.Trim();
                if (arg == "--list") list = true;
                else if (arg == "--verbose") verbose = true;
                else if (arg.StartsWith("-", StringComparison.Ordinal) && arg.Length > 1) excludes.Add(arg.Substring(1));
                else includes.Add(arg);
            }

            List<TestCase> tests = Discover(assembly, includes, excludes, out List<string> discoveryErrors);
            foreach (string error in discoveryErrors) Console.WriteLine("INVALID " + error);
            if (list)
            {
                foreach (TestCase test in tests) Console.WriteLine(test.Name);
                Console.WriteLine("== " + tests.Count + " test(s) selected");
                return discoveryErrors.Count == 0 ? 0 : Math.Min(discoveryErrors.Count, 125);
            }

            int passed = 0, failed = discoveryErrors.Count, skipped = 0;
            var total = Stopwatch.StartNew();
            foreach (TestCase test in tests)
            {
                var watch = Stopwatch.StartNew();
                Outcome outcome = Execute(test, out string detail);
                watch.Stop();
                switch (outcome)
                {
                    case Outcome.Passed:
                        passed++;
                        if (verbose) Console.WriteLine("PASS " + test.Name + " (" + Millis(watch) + ")");
                        break;
                    case Outcome.Skipped:
                        skipped++;
                        Console.WriteLine("SKIP " + test.Name + ": " + detail);
                        break;
                    default:
                        failed++;
                        Console.WriteLine("FAIL " + test.Name + " (" + Millis(watch) + ")");
                        foreach (string line in detail.Split('\n'))
                        {
                            Console.WriteLine("     " + line.TrimEnd('\r'));
                        }
                        break;
                }
            }
            total.Stop();
            Console.WriteLine("== " + passed + " passed, " + failed + " failed, " + skipped + " skipped in "
                              + total.Elapsed.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture) + " s"
                              + (includes.Count > 0 ? " (filter: " + string.Join(" ", includes) + ")" : string.Empty));
            return Math.Min(failed, 125);
        }

        private static List<TestCase> Discover(Assembly assembly, List<string> includes, List<string> excludes, out List<string> errors)
        {
            errors = new List<string>();
            var tests = new List<TestCase>();
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException e)
            {
                types = Array.FindAll(e.Types, t => t != null);
                errors.Add("type load: " + e.Message);
            }
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
            foreach (Type type in types)
            {
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
                    if (!Selected(name, includes, excludes)) continue;
                    tests.Add(new TestCase { Name = name, Method = method, Attribute = attribute });
                }
            }
            tests.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            return tests;
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

        private static Outcome Execute(TestCase test, out string detail)
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
            thread.Start();
            int timeoutMs = Math.Max(1, test.Attribute.TimeoutSeconds) * 1000;
            if (!thread.Join(timeoutMs))
            {
                detail = "timed out after " + test.Attribute.TimeoutSeconds + " s";
                return Outcome.Failed;
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
}
