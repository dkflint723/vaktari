using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// No plain <c>[Fact]</c> or <c>[Theory]</c> reaches a type whose static
/// initialiser needs the platform.
///
/// **One plain test poisoned a type for the whole process, and which class it
/// broke depended on the order xunit chose.** FileConverters parses the two
/// folder triangles into Geometry objects as it initialises, and a Geometry
/// needs Avalonia's render interface — which exists only once the headless
/// session has started, that is after the first <c>[AvaloniaFact]</c> of the
/// run. CompareSidesTests.A_row_says_what_the_comparison_found was a plain
/// <c>[Fact]</c> reading FileConverters.CompareWord: when it happened to run
/// first, the initialiser threw, the type stayed broken for every later test,
/// and the next Avalonia test failed its session setup with "the calling
/// thread cannot access this object". CompareSidesTests failed alone, on main
/// too, and passed in the full suite only because some other class started the
/// session first. Three more tests had the same shape.
///
/// Read from the compiled tests rather than their source, so a helper, a
/// lambda or an async body cannot hide the reference. A type counts as
/// platform-bound when it keeps a Geometry in a static or its initialiser
/// calls into one — the shape FileConverters and LinkEmblem have — and the
/// guard asserts it found FileConverters, so it cannot pass by finding nothing.
/// </summary>
public sealed class PlatformBoundStaticsTests
{
    private static readonly Dictionary<short, OpCode> ByValue =
        typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
                       .Select(f => (OpCode)f.GetValue(null)!)
                       .ToDictionary(o => o.Value);

    private static bool IsGeometry(Type? type)
        => type is not null && typeof(Avalonia.Media.Geometry).IsAssignableFrom(type);

    /// <summary>Every member a method body names, by walking its IL.</summary>
    private static IEnumerable<MemberInfo> Named(MethodBase method)
    {
        var il = method.GetMethodBody()?.GetILAsByteArray();

        if (il is null) yield break;

        var typeArgs = method.DeclaringType?.IsGenericType == true ? method.DeclaringType.GetGenericArguments() : null;
        var methodArgs = method.IsGenericMethod ? method.GetGenericArguments() : null;

        for (var at = 0; at < il.Length;)
        {
            var code = il[at] == 0xFE ? (short)(0xFE00 | il[at + 1]) : il[at];
            at += il[at] == 0xFE ? 2 : 1;

            var op = ByValue[code];

            switch (op.OperandType)
            {
                case OperandType.InlineMethod:
                case OperandType.InlineField:
                    MemberInfo? member = null;

                    try { member = method.Module.ResolveMember(BitConverter.ToInt32(il, at), typeArgs, methodArgs); }
                    catch (ArgumentException) { }

                    if (member is not null) yield return member;

                    at += 4;
                    break;

                case OperandType.InlineNone: break;
                case OperandType.ShortInlineBrTarget:
                case OperandType.ShortInlineI:
                case OperandType.ShortInlineVar: at += 1; break;
                case OperandType.InlineVar: at += 2; break;
                case OperandType.InlineI8:
                case OperandType.InlineR: at += 8; break;
                case OperandType.InlineSwitch: at += 4 + (4 * BitConverter.ToInt32(il, at)); break;
                default: at += 4; break;
            }
        }
    }

    /// <summary>The types whose initialiser makes a Geometry.</summary>
    private static HashSet<Type> PlatformBound()
        => [.. typeof(MainWindow).Assembly.GetTypes().Where(t =>
               t.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .Any(f => IsGeometry(f.FieldType))
               || (t.TypeInitializer is { } init && Named(init).Any(m => IsGeometry(m as Type ?? m.DeclaringType))))];

    /// <summary>
    /// Any fact or theory that does not start the headless session.
    ///
    /// **Compared by exact type, this let [WindowsFact] and [PosixFact] through**,
    /// and they are plain facts: FactAttribute subclasses that only add a skip.
    /// ListingRowNameTests.A_shortcut_is_read_the_way_it_is_drawn was a
    /// [WindowsFact] reading FileConverters.RowName, and alone it failed exactly
    /// as the four this guard was written for did — MEASURED, "the type
    /// initializer for 'FileConverters' threw" — while the guard passed.
    /// </summary>
    private static bool Plain(MethodInfo method)
        => method.GetCustomAttributes().Any(a =>
               a is FactAttribute
               && a is not Avalonia.Headless.XUnit.AvaloniaFactAttribute
               && a is not Avalonia.Headless.XUnit.AvaloniaTheoryAttribute);

    /// <summary>
    /// What xunit runs for one test on the test's own thread: the test, and for
    /// an instance test its class's constructor (field initialisers included)
    /// and Dispose, which run before and after it on the same thread.
    /// </summary>
    private static IEnumerable<MethodBase> Roots(MethodInfo test)
    {
        yield return test;

        if (test.IsStatic || test.DeclaringType is not { } type) yield break;

        foreach (var ctor in type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            yield return ctor;

        if (typeof(IDisposable).IsAssignableFrom(type)
            && type.GetMethod(nameof(IDisposable.Dispose), Type.EmptyTypes) is { } dispose)
            yield return dispose;
    }

    /// <summary>What the test's body reaches, following the calls it makes into
    /// this assembly: helpers, local functions, lambdas, async and iterator
    /// bodies, and the class's own set-up and tear-down.</summary>
    private static IEnumerable<(MethodBase Via, MemberInfo Member)> Reached(MethodInfo test)
    {
        var seen = new HashSet<MethodBase>();
        var pending = new Stack<MethodBase>(Roots(test));

        while (pending.TryPop(out var method))
        {
            if (!seen.Add(method)) continue;

            // Async, iterator and async-iterator bodies all live in a
            // compiler-made MoveNext that the method only constructs.
            if (method.GetCustomAttribute<StateMachineAttribute>() is { } machine
                && machine.StateMachineType.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.NonPublic) is { } body)
                pending.Push(body);

            foreach (var member in Named(method))
            {
                yield return (method, member);

                if (member is MethodBase callee && callee.Module == test.Module) pending.Push(callee);
            }
        }
    }

    [Fact]
    public void No_plain_test_reaches_a_type_the_platform_has_to_initialise()
    {
        var bound = PlatformBound();

        Assert.Contains(typeof(Vaktari.Ui.ViewModels.FileConverters), bound);

        var plain = typeof(PlatformBoundStaticsTests).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            .Where(Plain)
            .ToList();

        Assert.NotEmpty(plain);

        var found = plain
            .SelectMany(test => Reached(test)
                .Where(r => (r.Member as Type ?? r.Member.DeclaringType) is { } type && bound.Contains(type))
                .Select(r => $"{test.DeclaringType!.Name}.{test.Name} reaches {(r.Member as Type ?? r.Member.DeclaringType)!.Name}.{r.Member.Name}"))
            .Distinct()
            .ToList();

        Assert.True(found.Count == 0,
            "a plain [Fact] or [Theory] touches a type whose static initialiser needs the headless platform; "
            + "make it [AvaloniaFact] or [AvaloniaTheory]:\n" + string.Join("\n", found));
    }

    private static List<MethodInfo> PlainTests()
        => [.. typeof(PlatformBoundStaticsTests).Assembly.GetTypes()
               .SelectMany(t => t.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
               .Where(Plain)];

    /// <summary>
    /// Why a member binds Avalonia's UI dispatcher, or null when it does not.
    /// The dispatcher itself, and the two view models whose construction
    /// subscribes to something that later asks it.
    /// </summary>
    private static string? BindsTheDispatcher(MemberInfo member) => member switch
    {
        MethodInfo { Name: "get_UIThread" } m when m.DeclaringType == typeof(Avalonia.Threading.Dispatcher)
            => "asks Dispatcher.UIThread",
        ConstructorInfo c when c.DeclaringType == typeof(Vaktari.Ui.ViewModels.PaneViewModel)
            => "constructs a PaneViewModel",
        ConstructorInfo c when c.DeclaringType == typeof(Vaktari.Ui.ViewModels.ShellViewModel)
            => "constructs a ShellViewModel",
        _ => null,
    };

    /// <summary>
    /// Plain tests allowed to reach the dispatcher, each with the reason it is
    /// safe. Keyed "Class.Method". An entry that no longer matches anything
    /// fails the guard, so the list cannot outlive what it excuses.
    /// </summary>
    private static readonly Dictionary<string, string> DispatcherExemptions = new()
    {
    };

    /// <summary>
    /// No plain <c>[Fact]</c>, <c>[Theory]</c>, <c>[WindowsFact]</c> or
    /// <c>[PosixFact]</c> asks Avalonia's UI dispatcher, or builds a pane or a
    /// shell.
    ///
    /// **The last full-suite breakage came from exactly this** (3d18f97,
    /// 2026-09-28: 2,268 of 3,242 Ui tests failed in one run). Avalonia makes
    /// its UI dispatcher on the first thread that asks for
    /// <c>Dispatcher.UIThread</c> and binds it to that thread for the life of
    /// the process. TerminalChoiceTests were plain facts that built panes and
    /// left them subscribed to AppSettings.Changed; the next test's settings
    /// write ran PaneViewModel.OnSettingsChanged on a test worker, which asked
    /// for the dispatcher there. When xunit ran that class before any
    /// <c>[AvaloniaFact]</c>, the headless session's own set-up then failed
    /// with "the calling thread cannot access this object", and so did every
    /// Avalonia test after it. Which class went first depended on xunit's
    /// order, so the fault came and went between runs.
    ///
    /// A pane and a shell are named by their constructors because what they
    /// subscribe to asks for the dispatcher later, from someone else's code, and
    /// the walk below stops at this assembly's edge; the dispatcher itself is
    /// named wherever a test or its helpers ask for it directly.
    /// </summary>
    [Fact]
    public void No_plain_test_reaches_the_UI_dispatcher_or_builds_a_pane_or_a_shell()
    {
        var plain = PlainTests();

        Assert.NotEmpty(plain);

        var reached = plain
            .SelectMany(test => Reached(test)
                .Select(r => (Test: test, r.Via, Why: BindsTheDispatcher(r.Member)))
                .Where(r => r.Why is not null))
            .ToList();

        var used = new HashSet<string>();
        var found = new List<string>();

        foreach (var (test, via, why) in reached)
        {
            var key = $"{test.DeclaringType!.Name}.{test.Name}";

            if (DispatcherExemptions.ContainsKey(key))
            {
                used.Add(key);
                continue;
            }

            var route = via == test ? "" : $" (through {via.DeclaringType?.Name}.{via.Name})";

            found.Add($"{key} {why}{route}");
        }

        Assert.True(found.Count == 0,
            "a plain [Fact]/[Theory]/[WindowsFact]/[PosixFact] can bind Avalonia's UI dispatcher to a test worker "
            + "thread, after which the headless session cannot start and every [AvaloniaFact] in the run fails. "
            + "Make it [AvaloniaFact] or [AvaloniaTheory], or add it to DispatcherExemptions with the reason it is safe:\n"
            + string.Join("\n", found.Distinct().Order()));

        var stale = DispatcherExemptions.Keys.Where(k => !used.Contains(k)).ToList();

        Assert.True(stale.Count == 0,
            "these DispatcherExemptions no longer match a plain test that reaches the dispatcher; remove them:\n"
            + string.Join("\n", stale));
    }
}
