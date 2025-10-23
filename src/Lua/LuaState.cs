using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Lua.Internal;
using Lua.Loaders;
using Lua.Runtime;

namespace Lua;

public sealed class LuaState
{
    public const string DefaultChunkName = "chunk";

    // states
    private readonly LuaMainThread _mainThread = new();
    private FastListCore<UpValue> _openUpValues;
    private FastStackCore<LuaThread> _threadStack;
    private readonly LuaTable _packages = new();
    private readonly LuaTable _environment;
    private readonly LuaTable _registry = new();
    private readonly UpValue _envUpValue;
    private bool _isRunning;

    private FastStackCore<LuaDebug.LuaDebugBuffer> _debugBufferPool;

    internal UpValue EnvUpValue => _envUpValue;
    internal ref FastStackCore<LuaThread> ThreadStack => ref _threadStack;
    internal ref FastListCore<UpValue> OpenUpValues => ref _openUpValues;
    internal ref FastStackCore<LuaDebug.LuaDebugBuffer> DebugBufferPool => ref _debugBufferPool;

    public LuaTable Environment => _environment;
    public LuaTable Registry => _registry;
    public LuaTable LoadedModules => _packages;
    public LuaMainThread MainThread => _mainThread;
    public LuaThread CurrentThread
    {
        get
        {
            if (_threadStack.TryPeek(out var thread)) return thread;
            return _mainThread;
        }
    }

    public ILuaModuleLoader ModuleLoader { get; set; } = FileModuleLoader.Instance;

    // metatables
    private LuaTable? _nilMetatable;
    private LuaTable? _numberMetatable;
    private LuaTable? _stringMetatable;
    private LuaTable? _booleanMetatable;
    private LuaTable? _functionMetatable;
    private LuaTable? _threadMetatable;

    public static LuaState Create()
    {
        return new();
    }

    private LuaState()
    {
        _environment = new();
        _envUpValue = UpValue.Closed(_environment);
    }

    public async ValueTask<int> RunAsync(Chunk chunk, Memory<LuaValue> buffer, CancellationToken cancellationToken = default)
    {
        ThrowIfRunning();

        Volatile.Write(ref _isRunning, true);
        try
        {
            var closure = new LuaClosure(this, chunk);
            return await closure.InvokeAsync(new()
            {
                State = this,
                Thread = CurrentThread,
                ArgumentCount = 0,
                FrameBase = 0,
                SourcePosition = null,
                RootChunkName = chunk.Name,
                ChunkName = chunk.Name,
            }, buffer, cancellationToken);
        }
        finally
        {
            Volatile.Write(ref _isRunning, false);
        }
    }

    public void Push(LuaValue value)
    {
        CurrentThread.Stack.Push(value);
    }

    public Traceback GetTraceback()
    {
        if (_threadStack.Count == 0)
        {
            return new(this)
            {
                RootFunc = (LuaClosure)MainThread.GetCallStackFrames()[0].Function,
                StackFrames = MainThread.GetCallStackFrames()[1..]
                    .ToArray()
            };
        }

        using var list = new PooledList<CallStackFrame>(8);
        foreach (var frame in MainThread.GetCallStackFrames()[1..])
        {
            list.Add(frame);
        }

        foreach (var thread in _threadStack.AsSpan())
        {
            if (thread.CallStack.Count == 0) continue;
            foreach (var frame in thread.GetCallStackFrames()[1..])
            {
                list.Add(frame);
            }
        }

        return new(this)
        {
            RootFunc = (LuaClosure)MainThread.GetCallStackFrames()[0].Function,
            StackFrames = list.AsSpan().ToArray()
        };
    }

    internal Traceback GetTraceback(LuaThread thread)
    {
        using var list = new PooledList<CallStackFrame>(8);
        foreach (var frame in thread.GetCallStackFrames()[1..])
        {
            list.Add(frame);
        }
        LuaClosure rootFunc;
        if (thread.GetCallStackFrames()[0].Function is LuaClosure closure)
        {
            rootFunc = closure;
        }
        else
        {
            rootFunc = (LuaClosure)MainThread.GetCallStackFrames()[0].Function;
        }

        return new(this)
        {
            RootFunc = rootFunc,
            StackFrames = list.AsSpan().ToArray()
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetMetatable(LuaValue value, [NotNullWhen(true)] out LuaTable? result)
    {
        result = value.Type switch
        {
            LuaValueType.Nil => _nilMetatable,
            LuaValueType.Boolean => _booleanMetatable,
            LuaValueType.String => _stringMetatable,
            LuaValueType.Number => _numberMetatable,
            LuaValueType.Function => _functionMetatable,
            LuaValueType.Thread => _threadMetatable,
            LuaValueType.UserData => value.UnsafeRead<ILuaUserData>().Metatable,
            LuaValueType.Table => value.UnsafeRead<LuaTable>().Metatable,
            _ => null
        };

        return result != null;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetMetatable(LuaValue value, LuaTable metatable)
    {
        switch (value.Type)
        {
            case LuaValueType.Nil:
                _nilMetatable = metatable;
                break;
            case LuaValueType.Boolean:
                _booleanMetatable = metatable;
                break;
            case LuaValueType.String:
                _stringMetatable = metatable;
                break;
            case LuaValueType.Number:
                _numberMetatable = metatable;
                break;
            case LuaValueType.Function:
                _functionMetatable = metatable;
                break;
            case LuaValueType.Thread:
                _threadMetatable = metatable;
                break;
            case LuaValueType.UserData:
                value.UnsafeRead<ILuaUserData>().Metatable = metatable;
                break;
            case LuaValueType.Table:
                value.UnsafeRead<LuaTable>().Metatable = metatable;
                break;
        }
    }

    internal UpValue GetOrAddUpValue(LuaThread thread, int registerIndex)
    {
        foreach (var upValue in _openUpValues.AsSpan())
        {
            if (upValue.RegisterIndex == registerIndex && upValue.Thread == thread)
            {
                return upValue;
            }
        }

        var newUpValue = UpValue.Open(thread, registerIndex);
        _openUpValues.Add(newUpValue);
        return newUpValue;
    }

    internal void CloseUpValues(LuaThread thread, int frameBase)
    {
        for (var i = 0; i < _openUpValues.Length; i++)
        {
            var upValue = _openUpValues[i];
            if (upValue.Thread != thread) continue;
            if (upValue.RegisterIndex < frameBase) continue;
            upValue.Close();
            _openUpValues.RemoveAtSwapback(i);
            i--;
        }
    }

    private void ThrowIfRunning()
    {
        if (Volatile.Read(ref _isRunning))
        {
            throw new InvalidOperationException("the lua state is currently running");
        }
    }
}