using System.Reflection;

namespace OutPost2.Tests;

/// <summary>
/// Implements any interface for a test: members handed a function answer, the rest throw,
/// and every call is recorded. For the wide interfaces (the connection store, the calendar)
/// where a test touches two members and a hand-written fake would be forty lines of
/// NotSupportedException.
/// </summary>
public class Stub<T> : DispatchProxy where T : class
{
    private Dictionary<string, Func<object?[], object?>> _impl = [];
    public List<(string Name, object?[] Args)> Calls { get; } = [];

    public static (T Proxy, Stub<T> Stub) Create(Dictionary<string, Func<object?[], object?>> impl)
    {
        var proxy = DispatchProxy.Create<T, Stub<T>>();
        var stub = (Stub<T>)(object)proxy;
        stub._impl = impl;
        return (proxy, stub);
    }

    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        lock (Calls) Calls.Add((method!.Name, args ?? []));
        return _impl.TryGetValue(method.Name, out var f)
            ? f(args ?? [])
            : throw new NotSupportedException($"{typeof(T).Name}.{method.Name} is not part of this test.");
    }

    public int Count(string name)
    {
        lock (Calls) return Calls.Count(c => c.Name == name);
    }
}
