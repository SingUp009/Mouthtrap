using Moderato.Results;
using Xunit;

namespace Mouthtrap.Tests;

internal static class ResultAssert
{
    // The value of a result that must be Ok.
    public static T Ok<T, E>(Result<T, E> result)
    {
        Assert.True(result.TryGetOk(out var value), $"Expected Ok, got {result}.");
        return value!;
    }

    // The error of a result that must be Err.
    public static E Err<T, E>(Result<T, E> result)
    {
        Assert.True(result.TryGetErr(out var error), $"Expected Err, got {result}.");
        return error!;
    }
}
