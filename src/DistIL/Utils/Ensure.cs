
namespace DistIL.Util;

using System.Runtime.CompilerServices;

internal static class Ensure
{
    /// <summary> Throws an <see cref="InvalidOperationException"/> if <paramref name="cond"/> is false. </summary>
    [DebuggerStepThrough]
    public static void That([DoesNotReturnIf(false)] bool cond, [CallerArgumentExpression("cond")] string? msg = null)
    {
        if (!cond) {
            ThrowHelper(msg);
        }

        [DoesNotReturn, StackTraceHidden]
        static void ThrowHelper(string? msg)
            => throw new InvalidOperationException(msg);
    }

    [DebuggerStepThrough]
    public static void IndexValid(int index, int length)
    {
        Debug.Assert(length >= 0);

        if ((uint)index >= (uint)length) {
            ThrowHelper();
        }

        [DoesNotReturn, StackTraceHidden]
        static void ThrowHelper() => throw new IndexOutOfRangeException();
    }

    [DebuggerStepThrough, DoesNotReturn]
    public static Exception Unreachable() => throw new UnreachableException();
}