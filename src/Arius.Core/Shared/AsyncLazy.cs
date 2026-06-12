using System.Runtime.CompilerServices;

namespace Arius.Core.Shared;

internal class AsyncLazy<T> : Lazy<Task<T>>
{
    // Reference: https://devblogs.microsoft.com/dotnet/asynclazyt/

    public AsyncLazy(Func<T> valueFactory) :
        base(() => Task.Factory.StartNew(valueFactory))
    { }

    public AsyncLazy(Func<Task<T>> taskFactory) :
        base(() => Task.Factory.StartNew(taskFactory).Unwrap())
    { }

    public TaskAwaiter<T> GetAwaiter() { return Value.GetAwaiter(); }
}