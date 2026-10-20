using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using SkylineTogether.Core.Diagnostics;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Diagnostics
{
    /// <summary>
    /// <see cref="JitSelfTest"/>: which methods are compiled ahead of use, that every failure is recorded per method
    /// and the run goes on, and that the time budget and cancellation hold. The real run over the whole Core assembly
    /// measures the cost (the game layer runs the same over its own assembly on a background thread at startup).
    /// </summary>
    public sealed class JitSelfTestTests
    {
        private abstract class Shapes
        {
            public void Plain()
            {
            }

            public void Generic<T>()
            {
            }

            public abstract void Abstract();

            [DllImport("kernel32.dll")]
            public static extern int GetTickCount();

            static Shapes()
            {
            }
        }

        private sealed class Open<T>
        {
            public void Inside()
            {
            }
        }

        private delegate void Callback(int value);

        private static MethodBase Method(Type t, string name) => t.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);

        [Test]
        public void OnlyMethodsWithAnIlBodyAndNoOpenTypeArgumentsAreCompiled()
        {
            Assert.True(JitSelfTest.IsPreparable(Method(typeof(Shapes), "Plain")));
            Assert.False(JitSelfTest.IsPreparable(Method(typeof(Shapes), "Generic")), "generic method");
            Assert.False(JitSelfTest.IsPreparable(Method(typeof(Shapes), "Abstract")), "abstract");
            Assert.False(JitSelfTest.IsPreparable(Method(typeof(Shapes), "GetTickCount")), "P/Invoke");
            Assert.False(JitSelfTest.IsPreparable(Method(typeof(Callback), "Invoke")), "runtime-implemented delegate method");
            Assert.False(JitSelfTest.IsPreparable(Method(typeof(Open<>), "Inside")), "a method of an open generic type");

            List<MethodBase> methods = JitSelfTest.PreparableMethods(new[] { typeof(Shapes), typeof(Open<>), typeof(Callback) });
            var names = methods.ConvertAll(m => m.DeclaringType.Name + "." + m.Name);
            Assert.Contains("Shapes.Plain", names);
            Assert.Contains("Shapes..cctor", names);
            Assert.Contains("Shapes..ctor", names);
            Assert.Equal(0, names.FindAll(n => n.StartsWith("Callback", StringComparison.Ordinal)).Count, "a delegate's members are all runtime-implemented");
            Assert.Equal(0, names.FindAll(n => n.StartsWith("Open", StringComparison.Ordinal)).Count, "nothing of the open generic type");
            Assert.Equal(0, names.FindAll(n => n.EndsWith(".Invoke", StringComparison.Ordinal) || n.EndsWith(".Generic", StringComparison.Ordinal)).Count);
        }

        [Test]
        public void AFailureIsRecordedPerMethodAndTheRunGoesOn()
        {
            List<MethodBase> methods = JitSelfTest.PreparableMethods(new[] { typeof(Shapes) });
            var prepared = new List<string>();
            JitSelfTest.Result r = JitSelfTest.Run(methods, m =>
            {
                if (m.Name == "Plain") throw new MissingMethodException("Game.Simulation.SimulationSystem", "get_selectedSpeed");
                prepared.Add(m.Name);
            }, () => 0, 10);
            Assert.Equal(methods.Count, r.Methods);
            Assert.Equal(methods.Count - 1, r.Prepared);
            Assert.Equal(1, r.Failures.Count);
            JitSelfTest.Failure f = r.Failures[0];
            Assert.Contains("Shapes", f.Type);
            Assert.Equal("Plain()", f.Method);
            Assert.Contains("MissingMethodException", f.Error);
            Assert.Contains("get_selectedSpeed", f.Error);
            Assert.True(f.Binding, "a missing method is a binding failure");
            Assert.True(JitSelfTest.IsBindingFailure(new TypeLoadException("Game.Old")));
            Assert.True(JitSelfTest.IsBindingFailure(new MissingFieldException("Game.X", "m_Y")));
            Assert.False(JitSelfTest.IsBindingFailure(new InvalidProgramException()), "a compiler complaint is only a warning");
            Assert.False(r.TimedOut);
            Assert.Contains("1 failure(s)", JitSelfTest.Summary(r));
        }

        [Test]
        public void TheTimeBudgetAndCancellationStopTheRun()
        {
            var methods = new List<MethodBase>(JitSelfTest.PreparableMethods(new[] { typeof(JitSelfTest) }));
            Assert.True(methods.Count >= 5);
            double now = 0;
            JitSelfTest.Result timed = JitSelfTest.Run(methods, _ => now += 1, () => now, 2.5);
            Assert.True(timed.TimedOut);
            Assert.Equal(3, timed.Prepared, "methods started before 2.5 s had passed");
            int calls = 0;
            JitSelfTest.Result cancelled = JitSelfTest.Run(methods, _ => calls++, () => 0, 100, () => calls >= 2);
            Assert.True(cancelled.Cancelled);
            Assert.Equal(2, cancelled.Prepared);
        }

        [Test(TimeoutSeconds = 120)]
        public void TheWholeCoreAssemblyCompilesAndTheCostIsBounded()
        {
            var failures = new List<JitSelfTest.Failure>();
            List<Type> types = JitSelfTest.TypesOf(typeof(JitSelfTest).Assembly, failures);
            List<MethodBase> methods = JitSelfTest.PreparableMethods(types, failures);
            Assert.True(methods.Count > 3000, "the test assembly holds Core's methods (" + methods.Count + ")");
            JitSelfTest.Result r = JitSelfTest.Run(methods, JitSelfTest.PrepareWithRuntime, JitSelfTest.WallClock(), 90);
            failures.AddRange(r.Failures);
            Assert.Equal(0, failures.Count, string.Join("\n", failures.ConvertAll(f => f.ToString())));
            Assert.False(r.TimedOut, JitSelfTest.Summary(r));
            Assert.Equal(methods.Count, r.Prepared);
        }
    }
}
