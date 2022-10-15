﻿namespace DistIL.Frontend;

using ExceptionRegionKind = System.Reflection.Metadata.ExceptionRegionKind;

public class ILImporter
{
    public MethodDef Method { get; }

    internal MethodBody _body;
    internal VarFlags[] _varFlags; //Used to discover variables crossing try blocks/exposed address
    internal Variable[] _argSlots; //Argument variables

    readonly Dictionary<int, BlockState> _blocks = new();

    public ILImporter(MethodDef method)
    {
        Method = method;
        if (method.ILBody == null) {
            throw new ArgumentException("Method has no body to import");
        }
        int numVars = method.Params.Length + method.ILBody.Locals.Count;
        _argSlots = new Variable[method.Params.Length];
        _varFlags = new VarFlags[numVars];
        _body = new MethodBody(method);
    }

    public MethodBody ImportCode()
    {
        var ilBody = Method.ILBody!;
        var code = ilBody.Instructions.AsSpan();
        var ehRegions = ilBody.ExceptionRegions;
        var leaders = FindLeaders(code, ehRegions);

        CreateBlocks(leaders, ehRegions);
        CreateGuards(ehRegions);
        ImportBlocks(code, leaders);
        return _body;
    }

    private void CreateBlocks(BitSet leaders, List<ExceptionRegion> regions)
    {
        //Remove 0th label to avoid creating 2 blocks
        bool firstHasPred = leaders.Remove(0);
        var entryBlock = firstHasPred ? _body.CreateBlock() : null!;

        int startOffset = 0;
        foreach (int endOffset in leaders) {
            _blocks[startOffset] = new BlockState(this, IsInsideRegion(regions, startOffset));
            startOffset = endOffset;
        }
        //Ensure that the entry block don't have predecessors
        if (firstHasPred) {
            var firstBlock = GetBlock(0).Block;
            entryBlock.SetBranch(firstBlock);
        }
    }

    private void CreateGuards(List<ExceptionRegion> regions)
    {
        var mappings = new Dictionary<GuardInst, ExceptionRegion>(regions.Count);
        
        //I.12.4.2.5 Overview of exception handling
        foreach (var region in regions) {
            var kind = region.Kind switch {
                ExceptionRegionKind.Catch or
                ExceptionRegionKind.Filter  => GuardKind.Catch,
                ExceptionRegionKind.Finally => GuardKind.Finally,
                ExceptionRegionKind.Fault   => GuardKind.Fault,
                _ => throw new InvalidOperationException()
            };
            bool hasFilter = region.Kind == ExceptionRegionKind.Filter;

            var startBlock = GetOrSplitStartBlock(region);
            var handlerBlock = GetBlock(region.HandlerStart);
            var filterBlock = hasFilter ? GetBlock(region.FilterStart) : null;

            var guard = new GuardInst(kind, handlerBlock.Block, region.CatchType, filterBlock?.Block);
            startBlock.InsertBefore(startBlock.Last, guard);
            startBlock.Connect(handlerBlock.Block); //dummy edge to avoid unreachable blocks
            mappings.Add(guard, region);

            //Push exception on handler/filter entry stack
            if (kind == GuardKind.Catch) {
                handlerBlock.PushNoEmit(guard);
            }
            if (hasFilter) {
                filterBlock!.PushNoEmit(guard);
                startBlock.Connect(filterBlock.Block);
            }
        }

        BasicBlock GetOrSplitStartBlock(ExceptionRegion region)
        {
            var state = GetBlock(region.TryStart);

            //Create a new dominating block for this region if it nests any other in the current block.
            //Note that this code relies on the region table to be correctly ordered, as required by ECMA335:
            //  "If handlers are nested, the most deeply nested try blocks shall come
            //  before the try blocks that enclose them."
            if (IsBlockNestedBy(region, state.EntryBlock)) {
                var newBlock = _body.CreateBlock(insertAfter: state.EntryBlock.Prev);

                //FIXME: stop hacking block edges!
                foreach (var pred in state.EntryBlock.Preds.ToArray()) {
                    Debug.Assert(pred.Succs.Count == 1);
                    pred.SetBranch(newBlock);
                }
                newBlock.SetBranch(state.EntryBlock);
                state.EntryBlock = newBlock;
            }
            return state.EntryBlock;
        }
        bool IsBlockNestedBy(ExceptionRegion region, BasicBlock block)
        {
            foreach (var guard in block.Guards()) {
                var currRegion = mappings[guard];
                if (currRegion.TryStart >= region.TryStart && currRegion.TryEnd < region.TryEnd) {
                    return true;
                }
            }
            return false;
        }
    }

    private void ImportBlocks(Span<ILInstruction> code, BitSet leaders)
    {
        //Insert argument copies to local vars on the entry block
        var entryBlock = _body.EntryBlock ?? GetBlock(0).Block;
        var firstInst = entryBlock.First?.Prev;
        var args = _body.Args;
        for (int i = 0; i < args.Length; i++) {
            var arg = args[i];
            var slot = _argSlots[i] = new Variable(arg.ResultType, name: $"a_{arg.Name}");
            var store = new StoreVarInst(slot, arg);
            entryBlock.InsertAfter(firstInst, store);
            firstInst = store;
        }

        //Import code
        int startIndex = 0;
        foreach (int endOffset in leaders) {
            var block = GetBlock(code[startIndex].Offset);
            int endIndex = FindIndex(code, endOffset);
            block.ImportCode(code[startIndex..endIndex]);
            startIndex = endIndex;
        }
    }

    private static bool IsInsideRegion(List<ExceptionRegion> regions, int offset)
    {
        return regions.Any(r =>
            (offset >= r.TryStart && offset < r.TryEnd) ||
            (offset >= r.HandlerStart && offset < r.HandlerEnd) ||
            (r.Kind == ExceptionRegionKind.Filter && offset >= r.FilterStart && offset < r.FilterEnd)
        );
    }

    //Returns a bitset containing all instruction offsets where a block starts (branch targets).
    private static BitSet FindLeaders(Span<ILInstruction> code, List<ExceptionRegion> ehRegions)
    {
        int codeSize = code[^1].GetEndOffset();
        var leaders = new BitSet(codeSize);

        foreach (ref var inst in code) {
            if (!inst.OpCode.IsTerminator()) continue;

            if (inst.Operand is int targetOffset) {
                leaders.Add(targetOffset);
            }
            //switch
            else if (inst.Operand is int[] targetOffsets) {
                foreach (int offset in targetOffsets) {
                    leaders.Add(offset);
                }
            }
            leaders.Add(inst.GetEndOffset()); //fallthrough
        }

        foreach (var region in ehRegions) {
            //Note: end offsets must have already been marked by leave/endfinally
            leaders.Add(region.TryStart);

            if (region.HandlerStart >= 0) {
                leaders.Add(region.HandlerStart);
            }
            if (region.FilterStart >= 0) {
                leaders.Add(region.FilterStart);
            }
        }
        return leaders;
    }
    //Binary search to find instruction index using offset
    private static int FindIndex(Span<ILInstruction> code, int offset)
    {
        int start = 0;
        int end = code.Length - 1;
        while (start <= end) {
            int mid = (start + end) / 2;
            int c = offset - code[mid].Offset;
            if (c < 0) {
                end = mid - 1;
            } else if (c > 0) {
                start = mid + 1;
            } else {
                return mid;
            }
        }
        //Special case last instruction
        if (offset >= code[^1].Offset) {
            return code.Length;
        }
        throw new InvalidProgramException("Invalid instruction offset");
    }

    /// <summary> Gets or creates a block for the specified instruction offset. </summary>
    internal BlockState GetBlock(int offset) => _blocks[offset];
}
