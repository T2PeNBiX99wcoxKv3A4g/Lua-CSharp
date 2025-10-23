using System.Runtime.CompilerServices;
using Lua.Internal;
using Lua.Runtime;

namespace Lua;

public abstract class LuaThread
{
    public abstract LuaThreadStatus GetStatus();
    public abstract void UnsafeSetStatus(LuaThreadStatus status);
    public abstract ValueTask<int> ResumeAsync(LuaFunctionExecutionContext context, Memory<LuaValue> buffer, CancellationToken cancellationToken = default);
    public abstract ValueTask<int> YieldAsync(LuaFunctionExecutionContext context, Memory<LuaValue> buffer, CancellationToken cancellationToken = default);

    private FastStackCore<CallStackFrame> _callStack;

    public LuaStack Stack { get; } = new();

    internal ref FastStackCore<CallStackFrame> CallStack => ref _callStack;

    internal bool IsLineHookEnabled
    {
        get => LineAndCountHookMask.Flag0;
        set => LineAndCountHookMask.Flag0 = value;
    }

    internal bool IsCountHookEnabled
    {
        get => LineAndCountHookMask.Flag1;
        set => LineAndCountHookMask.Flag1 = value;
    }

    internal BitFlags2 LineAndCountHookMask;

    internal bool IsCallHookEnabled
    {
        get => CallOrReturnHookMask.Flag0;
        set => CallOrReturnHookMask.Flag0 = value;
    }

    internal bool IsReturnHookEnabled
    {
        get => CallOrReturnHookMask.Flag1;
        set => CallOrReturnHookMask.Flag1 = value;
    }

    internal BitFlags2 CallOrReturnHookMask;
    internal bool IsInHook;
    internal int HookCount;
    internal int BaseHookCount;
    internal int LastPc;
    internal LuaFunction? Hook { get; set; }

    public ref readonly CallStackFrame GetCurrentFrame()
    {
        return ref _callStack.PeekRef();
    }

    public ReadOnlySpan<LuaValue> GetStackValues()
    {
        return Stack.AsSpan();
    }

    public ReadOnlySpan<CallStackFrame> GetCallStackFrames()
    {
        return _callStack.AsSpan();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void PushCallStackFrame(in CallStackFrame frame)
    {
        _callStack.Push(frame);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void PopCallStackFrame()
    {
        if (_callStack.TryPop(out var frame))
        {
            Stack.PopUntil(frame.Base);
        }
        else
        {
            ThrowForEmptyStack();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void PopCallStackFrameUnsafe(int frameBase)
    {
        if (_callStack.TryPop())
        {
            Stack.PopUntil(frameBase);
        }
        else
        {
            ThrowForEmptyStack();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void PopCallStackFrameUnsafe()
    {
        if (!_callStack.TryPop())
        {
            ThrowForEmptyStack();
        }
    }

    internal void DumpStackValues()
    {
        var span = GetStackValues();
        for (int i = 0; i < span.Length; i++)
        {
            Console.WriteLine($"LuaStack [{i}]\t{span[i]}");
        }
    }

    private static void ThrowForEmptyStack() => throw new InvalidOperationException("Empty stack");
}