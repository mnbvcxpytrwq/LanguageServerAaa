using JSLSApp.LspTypes;
using JSLSApp.Syntax;

namespace JSLSApp;

public class CompletionCache
{
    // The Undo Stack. It tracks what needs to be restored when a scope exits.
    // We store a list of actions (or previous states) for each scope level.
    public readonly Stack<List<ScopeUndoEntry>> _undoStack = new();
    public SyntaxNode _currentScopeProvider = null!;
    public ref Scope _currentScope => ref _currentScopeProvider.GetScope();
    public GlobalNode _globalNode = null!;
    public Dictionary<int, SyntaxNode> _lexicalScope = null!;

    public void OpenScope(SyntaxNode scopeProviderNode, Position position)
    {
        ref var scope = ref scopeProviderNode.GetScope();
        scope.SetStart(position);
        scope.ParentScopeProvider = _currentScopeProvider;
        scope.BodyList = new();
        _currentScopeProvider = scopeProviderNode;

        // Push a new bucket to track changes for this specific scope level
        // TODO: This new list per scope isn't as bad as the dictionary was most importantly because the list is only a temporary object that isn't tied to the scope reference...
        // ...but I would still probably want to get rid of this allocation long term...
        // ...(i.e.: they both had an allocation but this new list won't clog up GC for the entire length of the Scope object's existence)...
        // ...and I intend to keep the scope objects around so this is good.
        if (scopeProviderNode != _globalNode)
        {
            _undoStack.Push(new List<ScopeUndoEntry>());
        }
    }

    public void CloseScope()
    {
        if (_undoStack.Count == 0) return; // GlobalScope doesn't make a ScopeUndoEntry

        if (_currentScope.ParentScopeProvider is not null) // GlobalScope has a null Parent
        {
            _currentScopeProvider = _currentScope.ParentScopeProvider;
        }

        // Pop the undo log for the scope we are leaving
        var undoList = _undoStack.Pop();

        // Roll back the changes in reverse order
        for (int i = undoList.Count - 1; i >= 0; i--)
        {
            var entry = undoList[i];
            if (entry.ExistedBefore)
            {
                // Restore the shadowed variable from the parent scope
                _lexicalScope[entry.IdentifierHash] = entry.PreviousNode;
            }
            else
            {
                // It was a brand new variable local to this scope; remove it entirely
                _lexicalScope.Remove(entry.IdentifierHash);
            }
        }
    }

    public void RecursiveSearch(ref RecursiveSearch_Args args)
    {
        ref var scope = ref args.ScopeProvider.GetScope();
        var i = 0;

        while (true)
        {
            bool descended = false;

            for (; i < scope.BodyList.Count; i++)
            {
                SyntaxNode? child_node = scope.BodyList[i];
                args.CurrentIndexPosition += child_node.DistanceFromPreviousNode;

                if (args.TargetIndexPosition >= args.CurrentIndexPosition && args.TargetIndexPosition <= args.CurrentIndexPosition + child_node.Length)
                {
                    args.TargetNode = child_node;
                    return;
                }
                else if (args.TargetIndexPosition < args.CurrentIndexPosition)
                {
                    return;
                }
                else if (args.TargetIndexPosition > args.CurrentIndexPosition)
                {
                    if (child_node.IsScopeProvider)
                    {
                        args.ReturnStack.Push((args.ScopeProvider, i + 1, false));
                        args.ScopeProvider = child_node;
                        scope = ref args.ScopeProvider.GetScope();
                        i = 0;
                        descended = true;
                        break;
                    }
                }
            }

            if (!descended)
            {
                if (args.ReturnStack.Count > 0)
                {
                    var parentContext = args.ReturnStack.Pop();
                    args.ScopeProvider = parentContext.ScopeProvider;
                    scope = ref args.ScopeProvider.GetScope();
                    i = parentContext.ChildIndexOfScopeProviderAmongItsSiblings;
                }
                else
                {
                    return;
                }
            }
        }
    }
}
