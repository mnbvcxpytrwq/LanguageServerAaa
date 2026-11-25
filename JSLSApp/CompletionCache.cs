using JSLSApp.Syntax;

namespace JSLSApp;

public struct ScopeUndoEntryWithPosition
{
    public int IdentifierHash;
    public bool ExistedBefore;
    public SyntaxNodeWithPosition PreviousNode; // Null if ExistedBefore is false
}

/// <summary>
/// TODO: Keep this alive at all times and each time you get a request for a position you traverse either forwards or backwards from the current
/// </summary>
public class CompletionCache
{
    // The Undo Stack. It tracks what needs to be restored when a scope exits.
    // We store a list of actions (or previous states) for each scope level.
    public readonly Stack<List<ScopeUndoEntryWithPosition>> _undoStack = new();
    public SyntaxNode _currentScopeProvider = null!;
    public ref Scope _currentScope => ref _currentScopeProvider.GetScope();
    public GlobalNode _globalNode = null!;
    public Dictionary<int, SyntaxNodeWithPosition> _lexicalScope = new();
    public bool FoundNearestEncompassingScope { get; set; }
    public Dictionary<int, SyntaxNodeWithPosition> _foundNearestEncompassingScope_Remember = new();
    public bool IsSideBranch { get; set; }

    public void OpenScope(SyntaxNode scopeProviderNode)
    {
        _currentScopeProvider = scopeProviderNode;

        // Push a new bucket to track changes for this specific scope level
        // TODO: This new list per scope isn't as bad as the dictionary was most importantly because the list is only a temporary object that isn't tied to the scope reference...
        // ...but I would still probably want to get rid of this allocation long term...
        // ...(i.e.: they both had an allocation but this new list won't clog up GC for the entire length of the Scope object's existence)...
        // ...and I intend to keep the scope objects around so this is good.
        if (scopeProviderNode != _globalNode)
        {
            _undoStack.Push(new List<ScopeUndoEntryWithPosition>());
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

    public void DeclareVariable(int identifierHash, SyntaxNodeWithPosition node)
    {
        // 1. Record the current state before we overwrite/shadow anything
        var entry = new ScopeUndoEntryWithPosition { IdentifierHash = identifierHash };

        if (_lexicalScope.TryGetValue(identifierHash, out var oldNode))
        {
            entry.ExistedBefore = true;
            entry.PreviousNode = oldNode; // Capture the shadowed node
        }
        else
        {
            entry.ExistedBefore = false;
        }

        // Log this change to the current scope's undo list
        if (_undoStack.Count > 0)
        {
            _undoStack.Peek().Add(entry);
        }

        // 2. Perform the actual declaration/shadowing
        _lexicalScope[identifierHash] = node;

        if (FoundNearestEncompassingScope && !IsSideBranch)
        {
            // flag side branches so you don't take them.
            if (!_foundNearestEncompassingScope_Remember.TryGetValue(identifierHash, out _))
            {
                _foundNearestEncompassingScope_Remember[identifierHash] = node;
            }
        }
    }

    public void RecursiveSearch(ref RecursiveSearch_Args args)
    {
        FoundNearestEncompassingScope = false;

        OpenScope(args.ScopeProvider);
        ref var scope = ref args.ScopeProvider.GetScope();
        var i = 0;

        while (true)
        {
            bool descended = false;

            for (; i < scope.BodyList.Count; i++)
            {
                SyntaxNode? child_node = scope.BodyList[i];

                args.CurrentIndexPosition += child_node.DistanceFromPreviousNode;
                
                switch (child_node.SyntaxKind)
                {
                    case SyntaxKind.ClassDeclarationNode:
                    case SyntaxKind.VariableDeclarationNode:
                    case SyntaxKind.FunctionDeclarationNode:
                        DeclareVariable(child_node.GetIdentifierHash(), new SyntaxNodeWithPosition(child_node, args.CurrentIndexPosition));
                        break;
                }

                if (args.TargetIndexPosition >= args.CurrentIndexPosition && args.TargetIndexPosition <= args.CurrentIndexPosition + child_node.Length)
                {
                    args.TargetNode = child_node;
                    return;
                }
                else if (args.TargetIndexPosition < args.CurrentIndexPosition)
                {
                    if (!FoundNearestEncompassingScope)
                    {
                        FoundNearestEncompassingScope = true;
                        _foundNearestEncompassingScope_Remember = new(_lexicalScope);
                    }
                    if (child_node.IsScopeProvider)
                    {
                        args.ReturnStack.Push((args.ScopeProvider, i + 1, IsSideBranch));
                        IsSideBranch = true;
                        args.ScopeProvider = child_node;
                        OpenScope(args.ScopeProvider);
                        scope = ref args.ScopeProvider.GetScope();
                        i = 0;
                        descended = true;
                        break;
                    }
                    continue;
                }
                else if (args.TargetIndexPosition > args.CurrentIndexPosition)
                {
                    if (child_node.IsScopeProvider)
                    {
                        args.ReturnStack.Push((args.ScopeProvider, i + 1, IsSideBranch));
                        args.ScopeProvider = child_node;
                        OpenScope(args.ScopeProvider);
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
                    CloseScope();
                    var parentContext = args.ReturnStack.Pop();
                    args.ScopeProvider = parentContext.ScopeProvider;
                    // in this function 'IsAtAnAfter' signifies what the IsSideBranch value was prior to changing scopeProvider.
                    // TODO: Probably change things so you don't use the variable named 'IsAtAnAfter' to do this.
                    IsSideBranch = parentContext.IsAtAnAfter;
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
