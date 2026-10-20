using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace SkylineTogether.Core.Diagnostics
{
    /// <summary>
    /// Compiles every method of the mod ahead of use (ARCHITECTURE.md#probes-and-self-test), so that a game update which
    /// removed or changed a member the mod binds to at compile time is found at startup: the runtime throws
    /// <c>MissingMethodException</c>, <c>MissingFieldException</c> or <c>TypeLoadException</c> when it compiles the
    /// method that uses it, which would otherwise happen the first time that code runs, in the middle of a session.
    /// The game layer runs it on a background thread with a time budget and switches the features of the failing methods
    /// off (<c>JitProbe</c>). Pure reflection: no method is executed, only compiled.
    /// </summary>
    public static class JitSelfTest
    {
        /// <summary>One method (or type) the runtime could not compile or load.</summary>
        public sealed class Failure
        {
            /// <summary>The declaring type's full name (or the type that failed to load).</summary>
            public string Type;
            /// <summary>The method name, or null when the whole type failed to load.</summary>
            public string Method;
            /// <summary>Exception type and message.</summary>
            public string Error;
            /// <summary>
            /// A binding failure (a missing or changed member, type or assembly): the code cannot run against this game.
            /// Other exceptions of the compiler are reported but may be quirks of the runtime.
            /// </summary>
            public bool Binding;

            public override string ToString() => Type + (Method != null ? "." + Method : "") + ": " + Error;
        }

        /// <summary>The outcome of <see cref="Run"/>.</summary>
        public sealed class Result
        {
            public int Methods;
            public int Prepared;
            public bool TimedOut;
            public bool Cancelled;
            public double Seconds;
            public readonly List<Failure> Failures = new List<Failure>();
        }

        private const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        /// <summary>
        /// The types of <paramref name="assembly"/>; types that fail to load (a base type or interface the game removed)
        /// are added to <paramref name="failures"/> instead.
        /// </summary>
        public static List<Type> TypesOf(Assembly assembly, List<Failure> failures)
        {
            if (assembly == null) throw new ArgumentNullException(nameof(assembly));
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException e)
            {
                types = e.Types ?? Array.Empty<Type>();
                foreach (Exception loader in e.LoaderExceptions ?? Array.Empty<Exception>())
                {
                    if (loader == null) continue;
                    failures?.Add(new Failure { Type = (loader as TypeLoadException)?.TypeName ?? assembly.GetName().Name, Error = Describe(loader), Binding = IsBindingFailure(loader) });
                }
            }
            var list = new List<Type>(types.Length);
            foreach (Type t in types)
            {
                if (t != null) list.Add(t);
            }
            list.Sort((a, b) => string.CompareOrdinal(a.FullName, b.FullName));
            return list;
        }

        /// <summary>
        /// The methods and constructors the runtime can compile without type arguments: declared in non-generic types,
        /// themselves not generic, with an IL body (not abstract, extern, P/Invoke or runtime-implemented such as delegate
        /// <c>Invoke</c>).
        /// </summary>
        public static List<MethodBase> PreparableMethods(IEnumerable<Type> types, List<Failure> failures = null)
        {
            if (types == null) throw new ArgumentNullException(nameof(types));
            var methods = new List<MethodBase>();
            foreach (Type type in types)
            {
                if (type == null || type.ContainsGenericParameters || type.IsInterface) continue;
                MethodBase[] members;
                try
                {
                    var all = new List<MethodBase>();
                    all.AddRange(type.GetMethods(Declared));
                    all.AddRange(type.GetConstructors(Declared));
                    members = all.ToArray();
                }
                catch (Exception e) when (!(e is OutOfMemoryException))
                {
                    failures?.Add(new Failure { Type = type.FullName, Error = Describe(e), Binding = IsBindingFailure(e) });
                    continue;
                }
                foreach (MethodBase m in members)
                {
                    if (IsPreparable(m)) methods.Add(m);
                }
            }
            return methods;
        }

        /// <summary>True for a method with an IL body and no open type arguments.</summary>
        public static bool IsPreparable(MethodBase m)
        {
            if (m == null || m.IsAbstract || m.ContainsGenericParameters) return false;
            if ((m.Attributes & MethodAttributes.PinvokeImpl) != 0) return false;
            MethodImplAttributes impl = m.GetMethodImplementationFlags();
            if ((impl & MethodImplAttributes.CodeTypeMask) != MethodImplAttributes.IL) return false; // runtime, native
            if ((impl & MethodImplAttributes.InternalCall) != 0) return false;
            return true;
        }

        /// <summary>The runtime's own ahead-of-use compilation of one method.</summary>
        public static void PrepareWithRuntime(MethodBase method)
        {
            RuntimeHelpers.PrepareMethod(method.MethodHandle);
        }

        /// <summary>
        /// Compiles <paramref name="methods"/> in order with <paramref name="prepare"/> until done, until
        /// <paramref name="budgetSeconds"/> of <paramref name="seconds"/> have passed, or until <paramref name="cancelled"/>
        /// says so. Every exception of one method is recorded as a failure of that method; the rest continue.
        /// </summary>
        public static Result Run(IReadOnlyList<MethodBase> methods, Action<MethodBase> prepare, Func<double> seconds, double budgetSeconds, Func<bool> cancelled = null)
        {
            if (methods == null) throw new ArgumentNullException(nameof(methods));
            if (prepare == null) throw new ArgumentNullException(nameof(prepare));
            if (seconds == null) throw new ArgumentNullException(nameof(seconds));
            var result = new Result { Methods = methods.Count };
            double start = seconds();
            foreach (MethodBase m in methods)
            {
                if (cancelled != null && cancelled())
                {
                    result.Cancelled = true;
                    break;
                }
                if (seconds() - start > budgetSeconds)
                {
                    result.TimedOut = true;
                    break;
                }
                try
                {
                    prepare(m);
                    result.Prepared++;
                }
                catch (Exception e) when (!(e is OutOfMemoryException) && !(e is StackOverflowException))
                {
                    result.Failures.Add(new Failure { Type = m.DeclaringType?.FullName ?? "?", Method = Signature(m), Error = Describe(e), Binding = IsBindingFailure(e) });
                }
            }
            result.Seconds = seconds() - start;
            return result;
        }

        /// <summary>A wall clock in seconds (for the game layer's background run).</summary>
        public static Func<double> WallClock()
        {
            Stopwatch watch = Stopwatch.StartNew();
            return () => watch.Elapsed.TotalSeconds;
        }

        private static string Signature(MethodBase m)
        {
            var sb = new System.Text.StringBuilder(m.Name).Append('(');
            try
            {
                ParameterInfo[] parameters = m.GetParameters();
                for (int i = 0; i < parameters.Length; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append(parameters[i].ParameterType.Name);
                }
            }
            catch (Exception e) when (!(e is OutOfMemoryException))
            {
                sb.Append('?');
            }
            return sb.Append(')').ToString();
        }

        /// <summary>A missing or changed member, type or assembly (the exceptions a game update causes).</summary>
        public static bool IsBindingFailure(Exception e)
        {
            while (e is TargetInvocationException && e.InnerException != null) e = e.InnerException;
            return e is MissingMemberException || e is TypeLoadException || e is System.IO.FileNotFoundException
                   || e is System.IO.FileLoadException || e is BadImageFormatException || e is MemberAccessException;
        }

        private static string Describe(Exception e)
        {
            while (e is TargetInvocationException && e.InnerException != null) e = e.InnerException;
            return e.GetType().Name + ": " + e.Message;
        }

        /// <summary>A one-line summary for the log.</summary>
        public static string Summary(Result r)
        {
            CultureInfo ci = CultureInfo.InvariantCulture;
            return r.Prepared.ToString(ci) + " of " + r.Methods.ToString(ci) + " methods compiled in " + r.Seconds.ToString("0.00", ci) + " s"
                   + (r.TimedOut ? " (time budget reached)" : "") + (r.Cancelled ? " (cancelled)" : "")
                   + ", " + r.Failures.Count.ToString(ci) + " failure(s)";
        }
    }
}
