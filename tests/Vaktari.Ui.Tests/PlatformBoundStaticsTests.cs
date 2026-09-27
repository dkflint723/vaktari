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

    private static bool Plain(MethodInfo method)
        => method.GetCustomAttributes().Any(a => a.GetType() == typeof(FactAttribute) || a.GetType() == typeof(TheoryAttribute));

    /// <summary>What the test's body reaches, following the calls it makes into
    /// this assembly: helpers, local functions, lambdas and async bodies.</summary>
    private static IEnumerable<(MethodBase Via, MemberInfo Member)> Reached(MethodInfo test)
    {
        var seen = new HashSet<MethodBase>();
        var pending = new Stack<MethodBase>();

        pending.Push(test);

        while (pending.TryPop(out var method))
        {
            if (!seen.Add(method)) continue;

            if (method.GetCustomAttribute<AsyncStateMachineAttribute>() is { } async
                && async.StateMachineType.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.NonPublic) is { } body)
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
}
