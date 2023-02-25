namespace DistIL.Passes;

using DistIL.Passes.Linq;

public class ExpandLinq : IMethodPass
{
    readonly TypeDefOrSpec t_Enumerable, t_IEnumerableOfT0;

    public ExpandLinq(ModuleDef mod)
    {
        t_Enumerable = mod.Resolver.Import(typeof(Enumerable));
        t_IEnumerableOfT0 = mod.Resolver.Import(typeof(IEnumerable<>)).GetSpec(default);
    }

    static IMethodPass IMethodPass.Create<TSelf>(Compilation comp)
        => new ExpandLinq(comp.Module);

    public MethodPassResult Run(MethodTransformContext ctx)
    {
        var queries = new List<LinqSourceNode>();

        foreach (var inst in ctx.Method.Instructions()) {
            if (inst is CallInst call && CreatePipe(call) is { } pipe) {
                queries.Add(pipe);
            }
        }

        foreach (var query in queries) {
            query.Emit();
            query.DeleteSubject();
        }
        return queries.Count > 0 ? MethodInvalidations.Loops : 0;
    }

    private LinqSourceNode? CreatePipe(CallInst call)
    {
        var sink = CreateSink(call);
        if (sink == null) {
            return null;
        }
        var source = CreateStage(call.GetOperandRef(0), sink);
        return IsProfitableToExpand(source, sink) ? source : null;
    }

    private static bool IsProfitableToExpand(LinqSourceNode source, LinqSink sink)
    {
        //Unfiltered Count()/Any() is not profitable because we scan the entire source.
        if (sink.SubjectCall is { NumArgs: 1, Method.Name: "Count" or "Any" }) {
            return false;
        }
        //Concretizing enumerator sources may not be profitable because 
        //Linq can special-case source types and defer to e.g. Array.Copy().
        //Similarly, expanding an enumerator source to a loop sink is an expansive no-op.
        if (source is EnumeratorSource && source.Drain == sink) {
            return sink is not ConcretizationSink or LoopSink;
        }
        return true;
    }

    private LinqSink? CreateSink(CallInst call)
    {
        var method = call.Method;
        if (method.DeclaringType == t_Enumerable) {
#pragma warning disable format
            return method.Name switch {
                "ToList" or "ToHashSet"     => new ConcretizationSink(call),
                "ToArray"                   => new ArraySink(call),
                "ToDictionary"              => new DictionarySink(call),
                "Aggregate"                 => new AggregationSink(call),
                "Count"                     => new CountSink(call),
                "First" or "FirstOrDefault" => new FindSink(call),
                "Any" or "All"              => new SatisfySink(call),
                _ => null
            };
#pragma warning restore format
        }
        if (method.Name == "GetEnumerator") {
            var declType = (method.DeclaringType as TypeSpec)?.Definition ?? method.DeclaringType;

            if (declType == t_IEnumerableOfT0.Definition || declType.Inherits(t_IEnumerableOfT0)) {
                return LoopSink.TryCreate(call);
            }
        }
        return null;
    }

    //UseRefs allows for overlapping queries to be expanded with no specific order.
    private LinqSourceNode CreateStage(UseRef sourceRef, LinqStageNode drain)
    {
        var source = sourceRef.Operand;

        if (source is CallInst call && call.Method.DeclaringType == t_Enumerable) {
            if (call.Method.Name == "Range") {
                return new IntRangeSource(call, drain);
            }
#pragma warning disable format
            var node = call.Method.Name switch {
                "Select"        => new SelectStage(call, drain),
                "Where"         => new WhereStage(call, drain),
                "OfType"        => new OfTypeStage(call, drain),
                "Cast"          => new CastStage(call, drain),
                "Skip"          => new SkipStage(call, drain),
                "SelectMany"    => new FlattenStage(call, drain),
                _ => default(LinqStageNode)
            };
#pragma warning restore format
            if (node != null) {
                return CreateStage(call.GetOperandRef(0), node);
            }
        }
        var type = source.ResultType;

        if (type is ArrayType || type.IsCorelibType(typeof(List<>)) || type.Kind == TypeKind.String) {
            return new MemorySource(sourceRef, drain);
        }
        return new EnumeratorSource(sourceRef, drain);
    }
}