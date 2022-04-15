namespace DistIL.Util;

public class GraphTraversal
{
    public static void DepthFirst<TNode>(
        TNode entry,
        Func<TNode, List<TNode>> getChildren,
        Action<TNode>? preVisit = null,
        Action<TNode>? postVisit = null
    ) where TNode : class
    {
        var pending = new ArrayStack<(TNode Node, int Index)>();
        var visited = new HashSet<TNode>();

        visited.Add(entry);
        pending.Push((entry, 0));
        preVisit?.Invoke(entry);

        while (!pending.IsEmpty) {
            ref var top = ref pending.Top;
            var children = getChildren(top.Node);

            if (top.Index < children.Count) {
                var child = children[top.Index++];

                if (visited.Add(child)) {
                    pending.Push((child, 0));
                    preVisit?.Invoke(child);
                }
            } else {
                postVisit?.Invoke(top.Node);
                pending.Pop();
            }
        }
    }
}