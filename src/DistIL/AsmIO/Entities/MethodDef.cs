namespace DistIL.AsmIO;

using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

/// <summary> Base class for all method entities. </summary>
public abstract class MethodDesc : MemberDesc
{
    public MethodAttributes Attribs { get; protected set; }
    public MethodImplAttributes ImplAttribs { get; protected set; }

    public bool IsStatic => (Attribs & MethodAttributes.Static) != 0;
    public bool IsInstance => !IsStatic;

    public ImmutableArray<TypeDesc> GenericParams { get; protected set; } = ImmutableArray<TypeDesc>.Empty;
    public bool IsGeneric => GenericParams.Length > 0;
    public bool IsGenericSpec => this is MethodSpec;

    public TypeDesc ReturnType { get; protected set; } = null!;
    public ImmutableArray<ParamDef> Params { get; protected set; }
    public ReadOnlySpan<ParamDef> StaticParams => Params.AsSpan(IsStatic ? 0 : 1);

    public override void Print(PrintContext ctx)
    {
        if (IsStatic) ctx.Print("static ", PrintToner.Keyword);
        ReturnType.Print(ctx);
        ctx.Print(" ");
        DeclaringType.Print(ctx);
        ctx.Print("::");
        ctx.Print(Name, PrintToner.MethodName);
        if (IsGeneric) {
            ctx.PrintSequence("<", ">", GenericParams, p => p.Print(ctx));
        }
        ctx.PrintSequence("(", ")", Params, p => p.Type.Print(ctx));
    }

    public virtual MethodDesc GetSpec(GenericContext ctx)
    {
        Debug.Assert(GenericParams.Length == 0, "GetSpec() must be overriden if the method can be instantiated");
        return this;
    }
}
public class ParamDef
{
    public TypeDesc Type { get; set; }
    public string Name { get; set; }
    public int Index { get; }
    public ParameterAttributes Attribs { get; set; }

    public ParamDef(TypeDesc type, int index, string name, ParameterAttributes attribs = default)
    {
        Type = type;
        Name = name;
        Index = index;
        Attribs = attribs;
    }

    public override string ToString() => Type.ToString();
}

public abstract class MethodDefOrSpec : MethodDesc, ModuleEntity
{
    /// <summary> Returns the parent definition if this is a MethodSpec, or the current instance if already a MethodDef. </summary>
    public abstract MethodDef Definition { get; }
    public ModuleDef Module => Definition.DeclaringType.Module;

    public abstract override TypeDefOrSpec DeclaringType { get; }

    public IReadOnlyCollection<CustomAttrib> GetParamCustomAttribs(ParamDef param)
    {
        Debug.Assert(Params.Contains(param));

        return Module.GetCustomAttribs(new() {
            LinkType = CustomAttribLink.Type.MethodParam,
            Entity = Definition,
            Index = param.Index
        });
    }
    public IReadOnlyCollection<CustomAttrib> GetGenericArgCustomAttribs(int index)
    {
        Ensure.That(index >= 0 && index < GenericParams.Length);

        return Module.GetCustomAttribs(new() {
            LinkType = CustomAttribLink.Type.GenericParam,
            Entity = Definition,
            Index = index
        });
    }
}
public class MethodDef : MethodDefOrSpec
{
    public override MethodDef Definition => this;
    public override TypeDef DeclaringType { get; }
    public override string Name { get; }

    public ILMethodBody? ILBody { get; set; }
    public IR.MethodBody? Body { get; set; }

    public MethodDef(
        TypeDef declaringType, TypeDesc retType, 
        ImmutableArray<ParamDef> pars, string name,
        MethodAttributes attribs = default, MethodImplAttributes implAttribs = default,
        ImmutableArray<TypeDesc> genericParams = default)
    {
        DeclaringType = declaringType;
        ReturnType = retType;
        Params = pars;
        Name = name;
        Attribs = attribs;
        ImplAttribs = implAttribs;
        GenericParams = genericParams.EmptyIfDefault();
    }

    internal void Load(ModuleLoader loader, MethodDefinition info)
    {
        var reader = loader._reader;
        foreach (var parHandle in info.GetParameters()) {
            var parInfo = reader.GetParameter(parHandle);

            int index = parInfo.SequenceNumber;
            if (index == 0) {
                //TODO: return parameter
            } else if (index <= Params.Length) {
                var par = Params[index - (IsStatic ? 1 : 0)]; //`this` is always implicit
                par.Name = reader.GetString(parInfo.Name);
                par.Attribs = parInfo.Attributes;
            }
        }
        if (info.RelativeVirtualAddress != 0) {
            ILBody = new ILMethodBody(loader, info.RelativeVirtualAddress);
        }
        loader.FillGenericParams(GenericParams, info.GetGenericParameters());
    }

    public override MethodDesc GetSpec(GenericContext ctx)
    {
        return IsGeneric || DeclaringType.IsGeneric
            ? new MethodSpec(DeclaringType.GetSpec(ctx), this, ctx.FillParams(GenericParams))
            : this;
    }
}

/// <summary> Represents a generic method instantiation. </summary>
public class MethodSpec : MethodDefOrSpec
{
    public override MethodDef Definition { get; }

    public override TypeDefOrSpec DeclaringType { get; }
    public override string Name => Definition.Name;

    internal MethodSpec(TypeDefOrSpec declaringType, MethodDef def, ImmutableArray<TypeDesc> args = default)
    {
        Definition = def;
        Attribs = def.Attribs;
        ImplAttribs = def.ImplAttribs;

        DeclaringType = declaringType;
        Ensure.That(args.IsDefaultOrEmpty || def.IsGeneric);
        GenericParams = args.IsDefault ? def.GenericParams : args;

        var genCtx = new GenericContext(this);
        ReturnType = def.ReturnType.GetSpec(genCtx);
        Params = def.Params.Select(p => new ParamDef(p.Type.GetSpec(genCtx), p.Index, p.Name, p.Attribs)).ToImmutableArray();
    }
}

public class ILMethodBody
{
    public required List<ExceptionRegion> ExceptionRegions { get; set; }
    public required List<ILInstruction> Instructions { get; set; }
    public required List<Variable> Locals { get; set; }
    public int MaxStack { get; set; }
    public bool InitLocals { get; set; }

    [SetsRequiredMembers]
    internal ILMethodBody(ModuleLoader loader, int rva)
    {
        var block = loader._pe.GetMethodBody(rva);

        ExceptionRegions = DecodeExceptionRegions(loader, block);
        Instructions = DecodeInsts(loader, block.GetILReader());
        Locals = DecodeLocals(loader, block);
        MaxStack = block.MaxStack;
        InitLocals = block.LocalVariablesInitialized;
    }

    public ILMethodBody()
    {
    }

    private static List<ILInstruction> DecodeInsts(ModuleLoader loader, BlobReader reader)
    {
        var list = new List<ILInstruction>(reader.Length / 2);
        while (reader.Offset < reader.Length) {
            var inst = DecodeInst(loader, ref reader);
            list.Add(inst);
        }
        return list;
    }

    private static ILInstruction DecodeInst(ModuleLoader loader, ref BlobReader reader)
    {
        int baseOffset = reader.Offset;
        int code = reader.ReadByte();
        if (code == 0xFE) {
            code = (code << 8) | reader.ReadByte();
        }
        var opcode = (ILCode)code;
        object? operand = opcode.GetOperandType() switch {
            ILOperandType.BrTarget => reader.ReadInt32() + reader.Offset,
            ILOperandType.Field or
            ILOperandType.Method or
            ILOperandType.Tok or
            ILOperandType.Type 
                => loader.GetEntity(MetadataTokens.EntityHandle(reader.ReadInt32())),
            ILOperandType.Sig
                //We convert "StandaloneSignature" into "FuncPtrType" because it's only used by calli
                //and it'd be inconvenient to have another obscure class.
                //TODO: fix generic context?
                => loader.DecodeMethodSig(MetadataTokens.StandaloneSignatureHandle(reader.ReadInt32())),
            ILOperandType.String => loader._reader.GetUserString(MetadataTokens.UserStringHandle(reader.ReadInt32())),
            ILOperandType.I => reader.ReadInt32(),
            ILOperandType.I8 => reader.ReadInt64(),
            ILOperandType.R => reader.ReadDouble(),
            ILOperandType.Switch => ReadJumpTable(ref reader),
            ILOperandType.Var => (int)reader.ReadUInt16(),
            ILOperandType.ShortBrTarget => (int)reader.ReadSByte() + reader.Offset,
            ILOperandType.ShortI => (int)reader.ReadSByte(),
            ILOperandType.ShortR => reader.ReadSingle(),
            ILOperandType.ShortVar => (int)reader.ReadByte(),
            _ => null
        };
        return new ILInstruction() {
            OpCode = opcode,
            Offset = baseOffset,
            Operand = operand
        };

        static int[] ReadJumpTable(ref BlobReader reader)
        {
            int count = reader.ReadInt32();
            int baseOffset = reader.Offset + count * 4;
            var targets = new int[count];

            for (int i = 0; i < count; i++) {
                targets[i] = baseOffset + reader.ReadInt32();
            }
            return targets;
        }
    }

    private static List<ExceptionRegion> DecodeExceptionRegions(ModuleLoader loader, MethodBodyBlock block)
    {
        var list = new List<ExceptionRegion>(block.ExceptionRegions.Length);
        foreach (var region in block.ExceptionRegions) {
            list.Add(new() {
                Kind = region.Kind,
                CatchType = region.CatchType.IsNil ? null : (TypeDefOrSpec)loader.GetEntity(region.CatchType),
                HandlerStart = region.HandlerOffset,
                HandlerEnd = region.HandlerOffset + region.HandlerLength,
                TryStart = region.TryOffset,
                TryEnd = region.TryOffset + region.TryLength,
                FilterStart = region.FilterOffset
            });
        }
        return list;
    }

    private static List<Variable> DecodeLocals(ModuleLoader loader, MethodBodyBlock block)
    {
        if (block.LocalSignature.IsNil) {
            return new List<Variable>();
        }
        var sig = loader._reader.GetStandaloneSignature(block.LocalSignature);
        var types = sig.DecodeLocalSignature(loader._typeProvider, default);
        var vars = new List<Variable>(types.Length);

        for (int i = 0; i < types.Length; i++) {
            var type = types[i];
            bool isPinned = false;
            if (type is PinnedType_ pinnedType) {
                type = pinnedType.ElemType;
                isPinned = true;
            }
            vars.Add(new Variable(type, isPinned, "loc" + (i + 1)));
        }
        return vars;
    }
}
public class ExceptionRegion
{
    public ExceptionRegionKind Kind { get; set; }

    /// <summary> The catch type if the region represents a catch handler, or null otherwise. </summary>
    public TypeDefOrSpec? CatchType { get; set; }

    /// <summary> Gets the starting IL offset of the exception handler. </summary>
    public int HandlerStart { get; set; }
    /// <summary> Gets the ending IL offset of the exception handler. </summary>
    public int HandlerEnd { get; set; }

    /// <summary> Gets the starting IL offset of the try region. </summary>
    public int TryStart { get; set; }
    /// <summary> Gets the ending IL offset of the try region. </summary>
    public int TryEnd { get; set; }

    /// <summary> Gets the starting IL offset of the filter region, or -1 if the region is not a filter. </summary>
    public int FilterStart { get; set; } = -1;
    /// <summary> Gets the ending IL offset of the filter region. This is an alias for `HandlerStart`. </summary>
    public int FilterEnd => HandlerStart;

    public override string ToString()
    {
        var sb = new StringBuilder();
        sb.Append($"Try(IL_{TryStart:X4}-IL_{TryEnd:X4}) ");
        sb.Append(Kind);
        if (Kind == ExceptionRegionKind.Catch) {
            sb.Append($"<{CatchType}>");
        }
        sb.Append($"(IL_{HandlerStart:X4}-IL_{HandlerEnd:X4})");
        if (FilterStart >= 0) {
            sb.Append($" Filter IL_{FilterStart:X4}");
        }
        return sb.ToString();
    }
}