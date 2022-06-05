namespace DistIL.IR;

using System.Text;

public class CallInst : Instruction
{
    public MethodDesc Method {
        get => (MethodDesc)Operands[0];
        set => ReplaceOperand(0, value);
    }
    public ReadOnlySpan<Value> Args => Operands.Slice(1);
    
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    public int NumArgs => Operands.Length - 1;

    public bool IsVirtual { get; set; }
    public bool IsStatic => Method.IsStatic;

    public override bool HasSideEffects => true;
    public override bool MayThrow => true;
    public override string InstName => "call" + (IsVirtual ? "virt" : "");

    public CallInst(MethodDesc method, Value[] args, bool isVirtual = false)
        : base(args.Prepend(method).ToArray())
    {
        ResultType = method.ReturnType;
        IsVirtual = isVirtual;
    }

    public Value GetArg(int index) => Operands[index + 1];
    public void SetArg(int index, Value newValue) => ReplaceOperand(index + 1, newValue);
    
    public override void Accept(InstVisitor visitor) => visitor.Visit(this);

    protected override void PrintOperands(StringBuilder sb, SlotTracker slotTracker)
        => PrintOperands(sb, slotTracker, Method, Args);

    internal static void PrintOperands(StringBuilder sb, SlotTracker slotTracker, MethodDesc method, ReadOnlySpan<Value> args, bool isCtor = false)
    {
        sb.Append(" ");
        method.DeclaringType.Print(sb, slotTracker, false);
        sb.Append($"::{method.Name}");
        if (method is MethodSpec { GenericParams.Length: > 0 }) {
            sb.AppendSequence("<", ">", method.GenericParams, p => p.Print(sb, slotTracker, false));
        }
        sb.Append("(");
        for (int i = 0; i < args.Length; i++) {
            if (i != 0) sb.Append(", ");

            if (i == 0 && method.IsInstance && !isCtor) {
                sb.Append("this: ");
            } else {
                var paramType = method.Params[i + (isCtor ? 1 : 0)].Type;
                paramType.Print(sb, slotTracker, false);
                sb.Append(": ");
            }
            args[i].PrintAsOperand(sb, slotTracker);
        }
        sb.Append(")");
    }
}

public class NewObjInst : Instruction
{
    /// <summary> The `.ctor` method. Note that the first argument (`this`) is ignored. </summary>
    public MethodDesc Constructor {
        get => (MethodDesc)Operands[0];
        set => ReplaceOperand(0, value);
    }
    public ReadOnlySpan<Value> Args => Operands.Slice(1);

    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    public int NumArgs => Operands.Length - 1;

    public override bool HasSideEffects => true;
    public override string InstName => "newobj";

    public NewObjInst(MethodDesc ctor, Value[] args)
        : base(args.Prepend(ctor).ToArray())
    {
        ResultType = ctor.DeclaringType;
    }

    public override void Accept(InstVisitor visitor) => visitor.Visit(this);

    protected override void PrintOperands(StringBuilder sb, SlotTracker slotTracker)
        => CallInst.PrintOperands(sb, slotTracker, Constructor, Args, true);
}

public class FuncAddrInst : Instruction
{
    public MethodDesc Method {
        get => (MethodDesc)Operands[0];
        set => ReplaceOperand(0, value);
    }
    public Value? Object {
        get => IsVirtual ? Operands[1] : null;
        set {
            Ensure(IsVirtual && value != null);
            ReplaceOperand(1, value);
        }
    }
    [MemberNotNullWhen(true, nameof(Object))]
    public bool IsVirtual => Operands.Length >= 2;

    public override string InstName => IsVirtual ? "virtfuncaddr" : "funcaddr";

    public FuncAddrInst(MethodDesc method, Value? obj = null)
        : base(obj == null ? new Value[] { method } : new Value[] { method, obj })
    {
        ResultType = new FuncPtrType(method);
    }

    public override void Accept(InstVisitor visitor) => visitor.Visit(this);
}