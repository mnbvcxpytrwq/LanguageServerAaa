using JSLSApp;
using JSLSApp.LspTypes;
using JSLSApp.Syntax;

namespace JavaScriptTranspiler;

/// <summary>
/// TODO: Might want to just have the JavaScriptParser do the parse and then the JavaScriptTranspiler walks the resulting AST to write the file.
/// </summary>
public class JavaScriptTranspilerClass
{
    private JavaScriptDocument _doc;
    private OutputFileState _outputFileState;
    private int _pos;

    private int _posOfPreviousNode = 0;
    private int _indexLine = 0;
    private int _indexChar = 0;

    // The Undo Stack. It tracks what needs to be restored when a scope exits.
    // We store a list of actions (or previous states) for each scope level.
    private readonly Stack<List<ScopeUndoEntry>> _undoStack = new();

    /// <summary>
    /// null! => the parse function is immediately constructed.
    /// 
    /// ====
    /// 
    /// TODO: Are you certain this tends to be the local scope specifically?...
    /// ...I'm thinking based on some text you can match to some locally defined/declared node...
    /// ...but maybe this would more-so imply every available defined/declared node i.e.: the ones in parent scopes?
    /// 
    /// TODO: probably just have 1 dictionary active ever as you go for the instant lookups rather than per scope. (i.e.: you'd prob just update this as you move through the scopes and overwrite older scopes with the???
    /// maybe if shadow you move shadow'd to a list of shadow'd nodes or maybe a stack? and then you can resotre the shadow'd node when you out of the scope that did the shadow.
    /// </summary>
    public Dictionary<int, SyntaxNode> _lexicalScope = null!;

    public string DebugString => $"pos=>{_pos} | (line,char)=>({_indexLine},{_indexChar})\n";
    private List<FunctionDefinitionSyntax> _functionDefinitionStartPositionList = new();
    
    private byte[] _syntaxHighlightByteList = new byte[0];
    /// <summary>
    /// TODO: Perhaps storing the _indexLine prior to invoking Lex and then checking if it changed is equivalent functionality with less overhead.
    /// </summary>
    private bool _seenLineEnd_flagForStringsAndComments;

    /// <summary>
    /// _peekToken is also used for temporary storage, when the boolean '_peekTokenExists' is false.
    /// </summary>
    private SyntaxToken _peekToken;
    private SyntaxKind _previousTokenSyntaxKind;
    /// <summary>
    /// TODO: Probably don't do this but I want 'this.apple' to work so I can feel at peace.
    /// </summary>
    private SyntaxKind _previousPreviousTokenSyntaxKind;
    private bool _peekTokenExists;

    /// <summary>
    /// These are not cleared between lex invocations, if you lex an identifier token then a number token you'll still have the hash of the identifier token in here.
    /// 
    /// Additionally if you lex a keyword you'll get the keyword's hash in this variable.
    /// Since you already have to track whether it was your Lex invocation that wrote to this the idea that a keyword writes to it
    /// feels sensible because blind reading of this is already known to be unreliable.
    /// </summary>
    private int _identifierHash;
    /// <summary>
    /// These are not cleared between lex invocations, if you lex an identifier token then a number token you'll still have the hash of the identifier token in here.
    /// 
    /// Additionally if you lex a keyword you'll get the keyword's hash in this variable.
    /// Since you already have to track whether it was your Lex invocation that wrote to this the idea that a keyword writes to it
    /// feels sensible because blind reading of this is already known to be unreliable.
    /// </summary>
    private int _peekIdentifierHash;


    public ref Scope _globalScope => ref _globalNode.GetScope();

    /// <summary>
    /// null! => the parse function is immediately constructed.
    /// </summary>
    private GlobalNode _globalNode = null!;

    /// <summary>
    /// null! => the parse function is immediately constructed.
    /// </summary>
    public SyntaxNode _currentScopeProvider = null!;
    public ref Scope _currentScope => ref _currentScopeProvider.GetScope();

    /// <summary>
    /// Order matters thus <see cref="_pendingVariableDeclarationNodeList"/>
    /// </summary>
    public Dictionary<int, VariableDeclarationNode> _pendingVariableDeclarationNodeDictionary = new();
    /// <summary>
    /// Order of the pending variables matters (and all must be accounted for) thus this exists in addition to the <see cref="_pendingVariableDeclarationNodeDictionary"/>
    /// </summary>
    public List<VariableDeclarationNode> _pendingVariableDeclarationNodeList = new();

    public JavaScriptTranspilerClass(JavaScriptDocument doc, OutputFileState outputFileState)
    {
        _doc = doc;
        _outputFileState = outputFileState;
    }

    public SyntaxToken PeekToken()
    {
        if (_peekTokenExists)
        {
            return _peekToken;
        }
        _peekTokenExists = true; // This comes first so the Lexer knows to set the _peekIdentifierHash;
        while ((_peekToken = Lex()).SyntaxKind == SyntaxKind.WhitespaceToken || _peekToken.SyntaxKind == SyntaxKind.MultiLineCommentToken || _peekToken.SyntaxKind == SyntaxKind.SingleLineCommentToken);
        return _peekToken;
    }

    public SyntaxToken ConsumePeekToken()
    {
        _peekTokenExists = false;
        _identifierHash = _peekIdentifierHash;
        return _peekToken;
    }

    public SyntaxToken NextToken()
    {
        _peekTokenExists = false;
        // Using _peekToken for temporary storage, the boolean '_peekTokenExists' is still false.
        while ((_peekToken = Lex()).SyntaxKind == SyntaxKind.WhitespaceToken || _peekToken.SyntaxKind == SyntaxKind.MultiLineCommentToken || _peekToken.SyntaxKind == SyntaxKind.SingleLineCommentToken) ;
        return _peekToken;
    }

    public SyntaxToken Defensive_SkipUntil_LexFor_OrEof(SyntaxKind syntaxKind)
    {
        _peekTokenExists = false;
        // Using _peekToken for temporary storage, the boolean '_peekTokenExists' is still false.
        while ((_peekToken = Lex()).SyntaxKind != syntaxKind)
        {
            if (_peekToken.SyntaxKind == SyntaxKind.EndOfFileToken)
                break;
        }
        return _peekToken;
    }

    public JavaScriptCompilationUnit Parse()
    {
        // TODO: Lexical scope might need to be set to null after the parse idk. It's gonna hold the global definitions.

        //Program._activeParser = this;

        _globalNode = new GlobalNode(0, 0);
        OpenScope(_globalNode, new Position(0, 0));
        _lexicalScope = new();

        _syntaxHighlightByteList = new byte[_doc.Count];

        _doc.HasBeenParsedAtLeastOnce = true;

        while (_pos < _doc.Count)
        {
            SyntaxToken token;
            if (_peekTokenExists)
            {
                token = ConsumePeekToken();
            }
            else
            {
                token = NextToken();
            }
            switch (token.SyntaxKind)
            {
                case SyntaxKind.EndOfFileToken:
                    goto exitOuterWhileLoop;
                case SyntaxKind.FunctionKeywordToken:
                    ParseFunctionDefinitionNode(default, false);
                    break;
                case SyntaxKind.ClassKeywordToken:
                    ParseClassDefinitionNode();
                    break;
                case SyntaxKind.ConstructorKeywordToken:
                    ParseConstructorDefinitionNode(token);
                    break;
                case SyntaxKind.ImportKeywordToken:
                    ParseImportNode();
                    break;
                case SyntaxKind.IdentifierToken:
                    if (_currentScope.Type == BodyKind.ClassBody &&
                        PeekToken().SyntaxKind == SyntaxKind.OpenParenthesisToken)
                    {
                        ParseFunctionDefinitionNode(token, true);
                    }
                    else
                    {
                        ParseAssumedReference(token);
                    }
                    // TODO: 'this' keyword.
                    break;
                case SyntaxKind.StringToken:
                    if (_seenLineEnd_flagForStringsAndComments)
                    {
                    }
                    break;
                case SyntaxKind.CloseBraceToken:
                    if (_currentScope.AttemptEndScope(token.Position, token.SyntaxKind) && _currentScope.ParentScopeProvider is not null)
                    {
                        CloseScope();
                    }
                    break;
                case SyntaxKind.IfKeywordToken:
                    ParseIfStatement();
                    break;
                case SyntaxKind.ElseKeywordToken:
                    // TODO: Don't just eat these without connecting the entire flow of the if/elseif/else
                    if (PeekToken().SyntaxKind == SyntaxKind.IfKeywordToken)
                    {
                        // 'else if'
                        ConsumePeekToken();
                        ParseIfStatement();
                        break;
                    }
                    // 'else' hit this
                    ParseElseStatement();
                    break;
                case SyntaxKind.ForKeywordToken:
                    ParseForStatement();
                    break;
                case SyntaxKind.OpenBraceToken:
                    ParseBlockNode(token);
                    break;
                case SyntaxKind.LetKeywordToken:
                case SyntaxKind.ConstKeywordToken:
                case SyntaxKind.VarKeywordToken:
                    ParseVariableDeclaration(token);
                    break;
                case SyntaxKind.WhitespaceToken:
                    break;
            }
            _previousPreviousTokenSyntaxKind = _previousTokenSyntaxKind;
            _previousTokenSyntaxKind = token.SyntaxKind;
        }

        exitOuterWhileLoop:
        //Program._activeParser = null;
        return new JavaScriptCompilationUnit(_functionDefinitionStartPositionList, _globalNode, _lexicalScope);
    }

    private void OpenScope(SyntaxNode scopeProviderNode, Position position)
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

    private void CloseScope()
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

    public void DeclareVariable(int identifierHash, SyntaxNode node)
    {
        // 1. Record the current state before we overwrite/shadow anything
        var entry = new ScopeUndoEntry { IdentifierHash = identifierHash };

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

        _currentScope.CountOfChildrenThatAreDefinitions++;
    }

    /// <summary>
    /// Returns 'true' if successfully found the definition, otherwise false.
    /// </summary>
    private bool ParseAssumedReference(SyntaxToken token)
    {
        var identifierHash = _identifierHash;
        if (_lexicalScope.TryGetValue(identifierHash, out var definition))
        {
            var variableReferenceNode = new VariableReferenceNode((_pos - token.Length) - _posOfPreviousNode, token.Length, definition);
            _posOfPreviousNode = _pos - token.Length;
            variableReferenceNode.GetIdentifierHash() = identifierHash;
            _currentScope.BodyList.Add(variableReferenceNode);
            return true;
        }

        return false;
    }
    
    private void ParseAssumedReference_PENDING(SyntaxToken token)
    {
        var identifierHash = _identifierHash;
        if (_pendingVariableDeclarationNodeDictionary.TryGetValue(identifierHash, out var definition))
        {
            var variableReferenceNode = new VariableReferenceNode((_pos - token.Length) - _posOfPreviousNode, token.Length, definition);
            _posOfPreviousNode = _pos - token.Length;
            variableReferenceNode.GetIdentifierHash() = identifierHash;
            _currentScope.BodyList.Add(variableReferenceNode);
        }
    }

    private void ParseVariableDeclaration(SyntaxToken token, bool isPending = false)
    {
        /*
        TODO: How do you say it... "implicit global variable declaration"? Lacking let,var,const when defining a variable that isn't defined.
        */
        var identifierToken = PeekToken();
        if (identifierToken.SyntaxKind == SyntaxKind.IdentifierToken)
        {
            ConsumePeekToken();
            var variableDeclarationNode = new VariableDeclarationNode((_pos - identifierToken.Length) - _posOfPreviousNode, identifierToken.Length, _currentScopeProvider);
            _posOfPreviousNode = _pos - identifierToken.Length;
            var identifierHash = _identifierHash;
            variableDeclarationNode.GetIdentifierHash() = identifierHash;

            if (isPending)
            {
                _pendingVariableDeclarationNodeDictionary.TryAdd(identifierHash, variableDeclarationNode);
                _pendingVariableDeclarationNodeList.Add(variableDeclarationNode);
            }
            else
            {
                _currentScope.BodyList.Add(variableDeclarationNode);
                DeclareVariable(identifierHash, variableDeclarationNode);
            }
        }
    }

    private void ParseImportNode()
    {
        return;
    }

    public void ParseIfStatement()
    {
        var token = PeekToken();
        if (token.SyntaxKind != SyntaxKind.OpenParenthesisToken)
            return;
        _ = ConsumePeekToken();
        // ----

        while (true)
        {
            token = NextToken();
            switch (token.SyntaxKind)
            {
                case SyntaxKind.IdentifierToken:
                    ParseAssumedReference(token);
                    break;
                case SyntaxKind.CloseParenthesisToken:
                case SyntaxKind.EndOfFileToken:
                    goto exitWhileLoop;
            }
        }
        exitWhileLoop:


        token = PeekToken();
        if (token.SyntaxKind != SyntaxKind.OpenBraceToken)
            return;
        _ = ConsumePeekToken();
        var openBraceToken = token;
        var ifStatementNode = new IfStatementNode((_pos - token.Length) - _posOfPreviousNode, 0);
        _posOfPreviousNode = _pos - token.Length;
        _currentScope.BodyList.Add(ifStatementNode);
        OpenScope(ifStatementNode, openBraceToken.Position);
    }

    public void ParseElseStatement()
    {
        var token = PeekToken();
        if (token.SyntaxKind != SyntaxKind.OpenBraceToken)
            return;
        _ = ConsumePeekToken();
        var openBraceToken = token;
        var ifStatementNode = new IfStatementNode((_pos - token.Length) - _posOfPreviousNode, 0);
        _posOfPreviousNode = _pos - token.Length;
        _currentScope.BodyList.Add(ifStatementNode);
        OpenScope(ifStatementNode, openBraceToken.Position);
    }

    public void ParseForStatement()
    {
        var token = PeekToken();
        if (token.SyntaxKind != SyntaxKind.OpenParenthesisToken)
            return;
        _ = ConsumePeekToken();
        // ----

        while (true)
        {
            token = NextToken();
            switch (token.SyntaxKind)
            {
                case SyntaxKind.LetKeywordToken:
                case SyntaxKind.ConstKeywordToken:
                case SyntaxKind.VarKeywordToken:
                    ParseVariableDeclaration(token, isPending: true);
                    break;
                case SyntaxKind.IdentifierToken:
                    if (!ParseAssumedReference(token))
                    {
                        ParseAssumedReference_PENDING(token);
                    }
                    break;
                case SyntaxKind.CloseParenthesisToken:
                case SyntaxKind.EndOfFileToken:
                    goto exitWhileLoop;
            }
        }
        exitWhileLoop:

        token = PeekToken();
        if (token.SyntaxKind == SyntaxKind.OpenBraceToken)
        {
            _ = ConsumePeekToken();
            var openBraceToken = token;
            var forStatementNode = new ForStatementNode((_pos - token.Length) - _posOfPreviousNode, 0);
            _posOfPreviousNode = _pos - token.Length;
            _currentScope.BodyList.Add(forStatementNode);
            OpenScope(forStatementNode, openBraceToken.Position);
            CopyPendingDefinitionsToCurrentScope_DOES_NOT_CLEAR_PENDING();
        }

        CopyPendingDefinitionsToCurrentScope_CLEAR();
    }

    public void ParseBlockNode(SyntaxToken openBraceToken)
    {
        var blockNode = new BlockNode((_pos - openBraceToken.Length) - _posOfPreviousNode, 0);
        _posOfPreviousNode = _pos - openBraceToken.Length;
        _currentScope.BodyList.Add(blockNode);
        OpenScope(blockNode, openBraceToken.Position);
    }

    public void ParseFunctionDefinitionNode(SyntaxToken identifierToken, bool identifierTokenExists)
    {
        // TODO: if (!identifierTokenExists) then you have a 'function' keyword defined function...
        // ...otherwise you have a function defined within a class that doesn't have the 'function' keyword...
        // ...TODO: Why is the function defined within a class logic 1 character off (the ' - 1' whereas the 'function' keyword defined function doesn't need this?
        // TODO: You probably lex'd the braces and parentheses wrong.

        // TODO: idk this is wrong the identifierTokenExists thing ???

        var identifierHash = 0;

        if (!identifierTokenExists)
        {
            identifierToken = PeekToken();
            if (identifierToken.SyntaxKind != SyntaxKind.IdentifierToken)
                return;
            _ = ConsumePeekToken();
            identifierHash = _identifierHash;
        }

        if (!identifierTokenExists)
        {
            
        }
        else
        {
            identifierHash = _identifierHash;
        }

        //if (!identifierTokenExists)
        //{
        //    _functionDefinitionStartPositionList[^1].Name = str;
        //}
        
        var functionDeclarationNode = new FunctionDeclarationNode((_pos - identifierToken.Length) - _posOfPreviousNode, identifierToken.Length);
        _posOfPreviousNode = _pos - identifierToken.Length;
        _currentScope.BodyList.Add(functionDeclarationNode);

        functionDeclarationNode.GetIdentifierHash() = identifierHash;
        DeclareVariable(identifierHash, functionDeclarationNode);

        ParseFunctionArguments();
        ParseFunctionBody(functionDeclarationNode);

        CopyPendingDefinitionsToCurrentScope_CLEAR();
    }

    public void ParseFunctionArguments()
    {
        var token = PeekToken();
        if (token.SyntaxKind != SyntaxKind.OpenParenthesisToken)
            return;
        _ = ConsumePeekToken();
        // ----

        while (true)
        {
            var peekToken = PeekToken();
            switch (peekToken.SyntaxKind)
            {
                case SyntaxKind.IdentifierToken:
                    var identifierToken = ConsumePeekToken();
                    var variableDeclarationNode = new VariableDeclarationNode((_pos - identifierToken.Length) - _posOfPreviousNode, identifierToken.Length, parentScopeProvider: null);
                    _posOfPreviousNode = _pos - identifierToken.Length;
                    var identifierHash = _identifierHash;
                    variableDeclarationNode.GetIdentifierHash() = identifierHash;
                    _pendingVariableDeclarationNodeDictionary.TryAdd(identifierHash, variableDeclarationNode);
                    _pendingVariableDeclarationNodeList.Add(variableDeclarationNode);
                    break;
                case SyntaxKind.CommaToken:
                    _ = ConsumePeekToken();
                    break;
                default:
                    goto exitWhileLoop;
            }
        }

        exitWhileLoop:

        if (_peekToken.SyntaxKind != SyntaxKind.CloseParenthesisToken)
        {
            Defensive_SkipUntil_LexFor_OrEof(SyntaxKind.CloseParenthesisToken);
        }

        if (_peekToken.SyntaxKind == SyntaxKind.CloseParenthesisToken)
        {
            ConsumePeekToken();
        }
    }

    public void ParseFunctionBody(FunctionDeclarationNode functionDeclarationNode)
    {
        var token = PeekToken();
        if (token.SyntaxKind != SyntaxKind.OpenBraceToken)
            return;
        _ = ConsumePeekToken();
        var openBraceToken = token;
        OpenScope(functionDeclarationNode, openBraceToken.Position);
        CopyPendingDefinitionsToCurrentScope_DOES_NOT_CLEAR_PENDING();
    }


    public void ParseClassDefinitionNode()
    {
        var token = PeekToken();
        if (token.SyntaxKind != SyntaxKind.IdentifierToken)
            return;
        _ = ConsumePeekToken();
        var identifierHash = _identifierHash;

        //SyntaxHighlightByteList.SetSyntaxHighlighting(_pos - token.Length, token.Length, TrackedSyntaxKind.Type);

        var classDeclarationNode = new ClassDeclarationNode((_pos - token.Length) - _posOfPreviousNode, 0);
        _posOfPreviousNode = _pos - token.Length;
        _currentScope.BodyList.Add(classDeclarationNode);

        classDeclarationNode.GetIdentifierHash() = identifierHash;
        DeclareVariable(identifierHash, classDeclarationNode);

        ParseClassBody(classDeclarationNode);
    }

    public void ParseClassBody(ClassDeclarationNode classDeclarationNode)
    {
        var token = PeekToken();
        if (token.SyntaxKind != SyntaxKind.OpenBraceToken)
            return;
        _ = ConsumePeekToken();
        var openBraceToken = token;
        OpenScope(classDeclarationNode, openBraceToken.Position);
    }

    public void ParseConstructorDefinitionNode(SyntaxToken constructorToken)
    {
        var functionDeclarationNode = new FunctionDeclarationNode((_pos - constructorToken.Length) - _posOfPreviousNode, constructorToken.Length);
        _posOfPreviousNode = _pos - constructorToken.Length;
        _currentScope.BodyList.Add(functionDeclarationNode);

        DeclareVariable(functionDeclarationNode.GetIdentifierHash(), functionDeclarationNode);

        ParseFunctionArguments();
        ParseFunctionBody(functionDeclarationNode);
    }

    /// <summary>
    /// DOES NOT CLEAR '_pendingVariableDeclarationNodeDictionary'
    /// This is to ensure a habit of not just covering the happy path.
    /// You need to clear whether you've used them or not.
    /// So you really ought to just have the code converge to a single clear statement if the syntax might've populated the pending.
    /// (or duplicate the clear yourself if the code cannot converge reasonably).
    /// 
    /// 
    /// 'OpenScope(forStatementNode, openBraceToken.Position);' prior to invoking.
    /// 
    /// <see cref="CopyPendingDefinitionsToCurrentScope_CLEAR"/>
    /// </summary>
    public void CopyPendingDefinitionsToCurrentScope_DOES_NOT_CLEAR_PENDING()
    {
        foreach (var variableDeclarationNode in _pendingVariableDeclarationNodeList)
        {
            variableDeclarationNode.ParentScopeProvider = _currentScopeProvider;
            _currentScope.BodyList.Add(variableDeclarationNode);
            DeclareVariable(variableDeclarationNode.GetIdentifierHash(), variableDeclarationNode);
        }
    }
    
    public void CopyPendingDefinitionsToCurrentScope_CLEAR()
    {
        _pendingVariableDeclarationNodeList.Clear();
        _pendingVariableDeclarationNodeDictionary.Clear();
    }

    /// <summary>
    /// TODO: maybe change to one of these 3 options instead of hardcoding the byte with a single line comment containing the letter
    /// - [ ] case (byte)')':
    /// - [ ] case >= (byte)'a' and <= (byte)'z':
    ///       case >= (byte)'A' and <= (byte)'Z':
    ///       case (byte)'_':
    /// - [ ] UTF-8 string literals (C# 11+): If you prefer avoiding casts altogether, a single-byte UTF-8 literal ("a"u8[0]) is technically a byte, though (byte)'a' is much more idiomatic and readable for lexer code.
    /// </summary>
    public SyntaxToken Lex()
    {
        while (_pos < _doc.Count)
        {
            // TODO: various multicharacter tokens
            switch (_doc.Chars[_pos])
            {
                case 97: // 'a'
                case 98: // 'b'
                case 99: // 'c'
                case 100: // 'd'
                case 101: // 'e'
                case 102: // 'f'
                case 103: // 'g'
                case 104: // 'h'
                case 105: // 'i'
                case 106: // 'j'
                case 107: // 'k'
                case 108: // 'l'
                case 109: // 'm'
                case 110: // 'n'
                case 111: // 'o'
                case 112: // 'p'
                case 113: // 'q'
                case 114: // 'r'
                case 115: // 's'
                case 116: // 't'
                case 117: // 'u'
                case 118: // 'v'
                case 119: // 'w'
                case 120: // 'x'
                case 121: // 'y'
                case 122: // 'z'
                case 65: // 'A'
                case 66: // 'B'
                case 67: // 'C'
                case 68: // 'D'
                case 69: // 'E'
                case 70: // 'F'
                case 71: // 'G'
                case 72: // 'H'
                case 73: // 'I'
                case 74: // 'J'
                case 75: // 'K'
                case 76: // 'L'
                case 77: // 'M'
                case 78: // 'N'
                case 79: // 'O'
                case 80: // 'P'
                case 81: // 'Q'
                case 82: // 'R'
                case 83: // 'S'
                case 84: // 'T'
                case 85: // 'U'
                case 86: // 'V'
                case 87: // 'W'
                case 88: // 'X'
                case 89: // 'Y'
                case 90: // 'Z'
                case 95: // '_'
                    return Lex_IdentifierOrKeyword();
                case 48: // '0'
                case 49: // '1'
                case 50: // '2'
                case 51: // '3'
                case 52: // '4'
                case 53: // '5'
                case 54: // '6'
                case 55: // '7'
                case 56: // '8'
                case 57: // '9'
                    return Lex_Number();
                case 32: // ' ' (space)
                case 9: // '\t' (tab)
                case 13: // '\r' (carriage return)
                case 10: // '\n' (line feed / newline)
                    return Lex_Whitespace();
                case 47: // '/'
                    if (_pos <= _doc.Count - 2)
                    {
                        if (_doc.Chars[_pos + 1] == 47 /* '/' */)
                        {
                            return Lex_SingleLineComment();
                        }
                        else if (_doc.Chars[_pos + 1] == 42 /* '*' */)
                        {
                            return Lex_MultiLineComment();
                        }
                    }
                    break;
                case 34: // '"'
                    return Lex_String('\"');
                case 39: // '\''
                    return Lex_String('\'');
                case 96: // '`'
                    return Lex_String('`');
                case 40: // '('
                    {
                        IncPos();
                        var startPosition = new Position(_indexLine, _indexChar);
                        _indexChar++;
                        return new SyntaxToken(SyntaxKind.OpenParenthesisToken, startPosition, 1);
                    }
                case 41: // ')'
                    {
                        IncPos();
                        var startPosition = new Position(_indexLine, _indexChar);
                        _indexChar++;
                        return new SyntaxToken(SyntaxKind.CloseParenthesisToken, startPosition, 1);
                    }
                case 123: // '{'
                    {
                        IncPos();
                        var startPosition = new Position(_indexLine, _indexChar);
                        _indexChar++;
                        return new SyntaxToken(SyntaxKind.OpenBraceToken, startPosition, 1);
                    }
                case 125: // '}'
                    {
                        IncPos();
                        var startPosition = new Position(_indexLine, _indexChar);
                        _indexChar++;
                        return new SyntaxToken(SyntaxKind.CloseBraceToken, startPosition, 1);
                    }
                case 91: // '['
                    {
                        IncPos();
                        var startPosition = new Position(_indexLine, _indexChar);
                        _indexChar++;
                        return new SyntaxToken(SyntaxKind.OpenSquareToken, startPosition, 1);
                    }
                case 93: // ']'
                    {
                        IncPos();
                        var startPosition = new Position(_indexLine, _indexChar);
                        _indexChar++;
                        return new SyntaxToken(SyntaxKind.CloseSquareToken, startPosition, 1);
                    }
                case 61: // '='
                    {
                        IncPos();
                        var startPosition = new Position(_indexLine, _indexChar);
                        _indexChar++;
                        return new SyntaxToken(SyntaxKind.EqualsToken, startPosition, 1);
                    }
                case 44: // ','
                    {
                        IncPos();
                        var startPosition = new Position(_indexLine, _indexChar);
                        _indexChar++;
                        return new SyntaxToken(SyntaxKind.CommaToken, startPosition, 1);
                    }
                case 59: // ';'
                    {
                        IncPos();
                        var startPosition = new Position(_indexLine, _indexChar);
                        _indexChar++;
                        return new SyntaxToken(SyntaxKind.SemicolonToken, startPosition, 1);
                    }
                case 60: // '<'
                    {
                        IncPos();
                        var startPosition = new Position(_indexLine, _indexChar);
                        _indexChar++;
                        return new SyntaxToken(SyntaxKind.OpenAngleToken, startPosition, 1);
                    }
                case 62: // '>'
                    {
                        IncPos();
                        var startPosition = new Position(_indexLine, _indexChar);
                        _indexChar++;
                        return new SyntaxToken(SyntaxKind.CloseAngleToken, startPosition, 1);
                    }
                case 38: // '&'
                    {
                        IncPos();
                        var startPosition = new Position(_indexLine, _indexChar);
                        _indexChar++;
                        return new SyntaxToken(SyntaxKind.AmpersandToken, startPosition, 1);
                    }
                case 46: // '.'
                    {
                        IncPos();
                        var startPosition = new Position(_indexLine, _indexChar);
                        _indexChar++;
                        return new SyntaxToken(SyntaxKind.PeriodToken, startPosition, 1);
                    }
                case 124: // '|'
                    {
                        IncPos();
                        var startPosition = new Position(_indexLine, _indexChar);
                        _indexChar++;
                        return new SyntaxToken(SyntaxKind.PipeToken, startPosition, 1);
                    }
                case 58: // ':'
                    {
                        IncPos();
                        var startPosition = new Position(_indexLine, _indexChar);
                        _indexChar++;
                        return new SyntaxToken(SyntaxKind.ColonToken, startPosition, 1);
                    }
                case 43: // '+'
                    {
                        IncPos();
                        var startPosition = new Position(_indexLine, _indexChar);
                        _indexChar++;
                        return new SyntaxToken(SyntaxKind.PlusToken, startPosition, 1);
                    }
                case 45: // '-'
                    {
                        IncPos();
                        var startPosition = new Position(_indexLine, _indexChar);
                        _indexChar++;
                        return new SyntaxToken(SyntaxKind.MinusToken, startPosition, 1);
                    }
                case 42: // '*'
                    {
                        IncPos();
                        var startPosition = new Position(_indexLine, _indexChar);
                        _indexChar++;
                        return new SyntaxToken(SyntaxKind.StarToken, startPosition, 1);
                    }
                case 94: // '^'
                    {
                        IncPos();
                        var startPosition = new Position(_indexLine, _indexChar);
                        _indexChar++;
                        return new SyntaxToken(SyntaxKind.CaretToken, startPosition, 1);
                    }
                case 37: // '%'
                    {
                        IncPos();
                        var startPosition = new Position(_indexLine, _indexChar);
                        _indexChar++;
                        return new SyntaxToken(SyntaxKind.PercentToken, startPosition, 1);
                    }
                case 36: // '$'
                    {
                        IncPos();
                        var startPosition = new Position(_indexLine, _indexChar);
                        _indexChar++;
                        return new SyntaxToken(SyntaxKind.DollarSignToken, startPosition, 1);
                    }
                case 35: // '#'
                    {
                        IncPos();
                        var startPosition = new Position(_indexLine, _indexChar);
                        _indexChar++;
                        return new SyntaxToken(SyntaxKind.PoundToken, startPosition, 1);
                    }
                case 64: // '@'
                    {
                        IncPos();
                        var startPosition = new Position(_indexLine, _indexChar);
                        _indexChar++;
                        return new SyntaxToken(SyntaxKind.AtToken, startPosition, 1);
                    }
                case 33: // '!'
                    {
                        IncPos();
                        var startPosition = new Position(_indexLine, _indexChar);
                        _indexChar++;
                        return new SyntaxToken(SyntaxKind.BangToken, startPosition, 1);
                    }
                default:
                    // TODO: multibyte characters
                    IncPos();
                    _indexChar++;
                    break;
            }

            IncPos();
        }

        return new SyntaxToken(SyntaxKind.EndOfFileToken, new Position(_indexLine, _indexChar), 0);
    }

    /// <summary>
    /// TODO: Usage of reserved words with '@' prefix
    /// </summary>
    public SyntaxToken Lex_IdentifierOrKeyword()
    {
        // TODO: hash collisions amongst identifiers (not a keyword)?
        //
        // NOTE: if you change this to 5381 make sure your pre-calculated case integers match the 5381 seed formula!
        var charIntSum = 0; // TODO: Google AI "a starting seed" // A standard starting seed for DJB2 is 5381"
        var startPosition = new Position(_indexLine, _indexChar);
        
        // TODO: if you just let it enter the loop you'd get rid of this specific line of duplicated hash logic
        charIntSum = ((charIntSum << 5) - charIntSum) + _doc.Chars[_pos];
        //_outputFileState.Add(_doc.Chars[_pos]);

        var length = 1;
        IncPos();
        _indexChar++;

        while (_pos < _doc.Count)
        {
            byte b = _doc.Chars[_pos];
            bool isValidIdentifierChar = b is >= (byte)'0' and <= (byte)'9'
                                           or >= (byte)'A' and <= (byte)'Z'
                                           or >= (byte)'a' and <= (byte)'z'
                                           or (byte)'_';
            if (isValidIdentifierChar)
            {
                length++;
                // Standard DJB2 hash formula: hash * 33 + c
                charIntSum = ((charIntSum << 5) - charIntSum) + _doc.Chars[_pos];

                //_outputFileState.Add(b);

                IncPos();
                _indexChar++;
            }
            else
            {
                break;
            }
        }

        var syntaxKind = SyntaxKind.IdentifierToken;
        var flagControlKeyword = false;

        /*
         TODO:
            Google AI (fast CPU SIMD vectors under the hood):

            if (tokenSpan.SequenceEqual("const"u8))
            {
                syntaxKind = SyntaxKind.ConstKeywordToken;
            }
         */

        switch (charIntSum)
        {
            case 94844771: // const
                if (length == 5 &&
                    _doc.Chars[_pos - 5] == 'c' &&
                    _doc.Chars[_pos - 4] == 'o' &&
                    _doc.Chars[_pos - 3] == 'n' &&
                    _doc.Chars[_pos - 2] == 's' &&
                    _doc.Chars[_pos - 1] == 't')
                {
                    syntaxKind = SyntaxKind.ConstKeywordToken;
                    break;
                }
                break;
            case 107035: // let
                if (length == 3 &&
                    _doc.Chars[_pos - 3] == 'l' &&
                    _doc.Chars[_pos - 2] == 'e' &&
                    _doc.Chars[_pos - 1] == 't')
                {
                    syntaxKind = SyntaxKind.LetKeywordToken;
                    break;
                }
                break;
            case 1380938712: // function
                if (length == 8 &&
                    _doc.Chars[_pos - 8] == 'f' &&
                    _doc.Chars[_pos - 7] == 'u' &&
                    _doc.Chars[_pos - 6] == 'n' &&
                    _doc.Chars[_pos - 5] == 'c' &&
                    _doc.Chars[_pos - 4] == 't' &&
                    _doc.Chars[_pos - 3] == 'i' &&
                    _doc.Chars[_pos - 2] == 'o' &&
                    _doc.Chars[_pos - 1] == 'n')
                {
                    syntaxKind = SyntaxKind.FunctionKeywordToken;
                    _functionDefinitionStartPositionList.Add(new FunctionDefinitionSyntax(startPosition));
                    break;
                }
                break;
            case 3357: // if
                if (length == 2 &&
                    _doc.Chars[_pos - 2] == 'i' &&
                    _doc.Chars[_pos - 1] == 'f')
                {
                    syntaxKind = SyntaxKind.IfKeywordToken;
                    flagControlKeyword = true;
                    break;
                }
                break;
            case 115131: // try
                if (length == 3 &&
                    _doc.Chars[_pos - 3] == 't' &&
                    _doc.Chars[_pos - 2] == 'r' &&
                    _doc.Chars[_pos - 1] == 'y')
                {
                    syntaxKind = SyntaxKind.TryKeywordToken;
                    break;
                }
                break;
            case 101577: // for
                if (length == 3 &&
                    _doc.Chars[_pos - 3] == 'f' &&
                    _doc.Chars[_pos - 2] == 'o' &&
                    _doc.Chars[_pos - 1] == 'r')
                {
                    syntaxKind = SyntaxKind.ForKeywordToken;
                    flagControlKeyword = true;
                    break;
                }
                break;
            case 116519: // var
                if (length == 3 &&
                    _doc.Chars[_pos - 3] == 'v' &&
                    _doc.Chars[_pos - 2] == 'a' &&
                    _doc.Chars[_pos - 1] == 'r')
                {
                    syntaxKind = SyntaxKind.VarKeywordToken;
                    break;
                }
                break;
            case 94432955: // catch
                if (length == 5 &&
                    _doc.Chars[_pos - 5] == 'c' &&
                    _doc.Chars[_pos - 4] == 'a' &&
                    _doc.Chars[_pos - 3] == 't' &&
                    _doc.Chars[_pos - 2] == 'c' &&
                    _doc.Chars[_pos - 1] == 'h')
                {
                    syntaxKind = SyntaxKind.CatchKeywordToken;
                    break;
                }
                break;
            case -934396624: // return
                if (length == 6 &&
                    _doc.Chars[_pos - 6] == 'r' &&
                    _doc.Chars[_pos - 5] == 'e' &&
                    _doc.Chars[_pos - 4] == 't' &&
                    _doc.Chars[_pos - 3] == 'u' &&
                    _doc.Chars[_pos - 2] == 'r' &&
                    _doc.Chars[_pos - 1] == 'n')
                {
                    syntaxKind = SyntaxKind.ReturnKeywordToken;
                    flagControlKeyword = true;
                    break;
                }
                break;
            case -889473228: // switch
                if (length == 6 &&
                    _doc.Chars[_pos - 6] == 's' &&
                    _doc.Chars[_pos - 5] == 'w' &&
                    _doc.Chars[_pos - 4] == 'i' &&
                    _doc.Chars[_pos - 3] == 't' &&
                    _doc.Chars[_pos - 2] == 'c' &&
                    _doc.Chars[_pos - 1] == 'h')
                {
                    syntaxKind = SyntaxKind.SwitchKeywordToken;
                    flagControlKeyword = true;
                    break;
                }
                break;
            case 3046192: // case
                if (length == 4 &&
                    _doc.Chars[_pos - 4] == 'c' &&
                    _doc.Chars[_pos - 3] == 'a' &&
                    _doc.Chars[_pos - 2] == 's' &&
                    _doc.Chars[_pos - 1] == 'e')
                {
                    syntaxKind = SyntaxKind.CaseKeywordToken;
                    flagControlKeyword = true;
                    break;
                }
                break;
            case 93127292: // async
                if (length == 5 &&
                    _doc.Chars[_pos - 5] == 'a' &&
                    _doc.Chars[_pos - 4] == 's' &&
                    _doc.Chars[_pos - 3] == 'y' &&
                    _doc.Chars[_pos - 2] == 'n' &&
                    _doc.Chars[_pos - 1] == 'c')
                {
                    syntaxKind = SyntaxKind.AsyncKeywordToken;
                    break;
                }
                break;
            case 3116345: // else
                if (length == 4 &&
                    _doc.Chars[_pos - 4] == 'e' &&
                    _doc.Chars[_pos - 3] == 'l' &&
                    _doc.Chars[_pos - 2] == 's' &&
                    _doc.Chars[_pos - 1] == 'e')
                {
                    syntaxKind = SyntaxKind.ElseKeywordToken;
                    flagControlKeyword = true;
                    break;
                }
                break;
            case 1544803905: // default
                if (length == 7 &&
                    _doc.Chars[_pos - 7] == 'd' &&
                    _doc.Chars[_pos - 6] == 'e' &&
                    _doc.Chars[_pos - 5] == 'f' &&
                    _doc.Chars[_pos - 4] == 'a' &&
                    _doc.Chars[_pos - 3] == 'u' &&
                    _doc.Chars[_pos - 2] == 'l' &&
                    _doc.Chars[_pos - 1] == 't')
                {
                    syntaxKind = SyntaxKind.DefaultKeywordToken;
                    break;
                }
                break;
            case 110339814: // throw
                if (length == 5 &&
                    _doc.Chars[_pos - 5] == 't' &&
                    _doc.Chars[_pos - 4] == 'h' &&
                    _doc.Chars[_pos - 3] == 'r' &&
                    _doc.Chars[_pos - 2] == 'o' &&
                    _doc.Chars[_pos - 1] == 'w')
                {
                    syntaxKind = SyntaxKind.ThrowKeywordToken;
                    break;
                }
                break;
            case 108960: // new
                if (length == 3 &&
                    _doc.Chars[_pos - 3] == 'n' &&
                    _doc.Chars[_pos - 2] == 'e' &&
                    _doc.Chars[_pos - 1] == 'w')
                {
                    syntaxKind = SyntaxKind.NewKeywordToken;
                    break;
                }
                break;
            case 93223254: // await
                if (length == 5 &&
                    _doc.Chars[_pos - 5] == 'a' &&
                    _doc.Chars[_pos - 4] == 'w' &&
                    _doc.Chars[_pos - 3] == 'a' &&
                    _doc.Chars[_pos - 2] == 'i' &&
                    _doc.Chars[_pos - 1] == 't')
                {
                    syntaxKind = SyntaxKind.AwaitKeywordToken;
                    break;
                }
                break;
            case 94742904: // class
                if (length == 5 &&
                    _doc.Chars[_pos - 5] == 'c' &&
                    _doc.Chars[_pos - 4] == 'l' &&
                    _doc.Chars[_pos - 3] == 'a' &&
                    _doc.Chars[_pos - 2] == 's' &&
                    _doc.Chars[_pos - 1] == 's')
                {
                    syntaxKind = SyntaxKind.ClassKeywordToken;
                    break;
                }
                break;
            case -1588406278: // constructor
                if (length == 11 &&
                    _doc.Chars[_pos - 11] == 'c' &&
                    _doc.Chars[_pos - 10] == 'o' &&
                    _doc.Chars[_pos - 9]  == 'n' &&
                    _doc.Chars[_pos - 8]  == 's' &&
                    _doc.Chars[_pos - 7]  == 't' &&
                    _doc.Chars[_pos - 6]  == 'r' &&
                    _doc.Chars[_pos - 5]  == 'u' &&
                    _doc.Chars[_pos - 4]  == 'c' &&
                    _doc.Chars[_pos - 3]  == 't' &&
                    _doc.Chars[_pos - 2]  == 'o' &&
                    _doc.Chars[_pos - 1]  == 'r')
                {
                    syntaxKind = SyntaxKind.ConstructorKeywordToken;
                    break;
                }
                break;
            case -1184795739: // import
                if (length == 6 &&
                    _doc.Chars[_pos - 6] == 'i' &&
                    _doc.Chars[_pos - 5] == 'm' &&
                    _doc.Chars[_pos - 4] == 'p' &&
                    _doc.Chars[_pos - 3] == 'o' &&
                    _doc.Chars[_pos - 2] == 'r' &&
                    _doc.Chars[_pos - 1] == 't')
                {
                    syntaxKind = SyntaxKind.ImportKeywordToken;
                    flagControlKeyword = true;
                    break;
                }
                break;
            case 3151786: // from
                if (length == 4 &&
                    _doc.Chars[_pos - 4] == 'f' &&
                    _doc.Chars[_pos - 3] == 'r' &&
                    _doc.Chars[_pos - 2] == 'o' &&
                    _doc.Chars[_pos - 1] == 'm')
                {
                    syntaxKind = SyntaxKind.FromKeywordToken;
                    flagControlKeyword = true;
                    break;
                }
                break;
            case -1289153612: // export
                if (length == 6 &&
                    _doc.Chars[_pos - 6] == 'e' &&
                    _doc.Chars[_pos - 5] == 'x' &&
                    _doc.Chars[_pos - 4] == 'p' &&
                    _doc.Chars[_pos - 3] == 'o' &&
                    _doc.Chars[_pos - 2] == 'r' &&
                    _doc.Chars[_pos - 1] == 't')
                {
                    syntaxKind = SyntaxKind.ExportKeywordToken;
                    break;
                }
                break;
            case 3559070: // this
                if (length == 4 &&
                    _doc.Chars[_pos - 4] == 't' &&
                    _doc.Chars[_pos - 3] == 'h' &&
                    _doc.Chars[_pos - 2] == 'i' &&
                    _doc.Chars[_pos - 1] == 's')
                {
                    syntaxKind = SyntaxKind.ThisKeywordToken;
                    break;
                }
                break;
            case 113101617: // while
                if (length == 5 &&
                    _doc.Chars[_pos - 5] == 'w' &&
                    _doc.Chars[_pos - 4] == 'h' &&
                    _doc.Chars[_pos - 3] == 'i' &&
                    _doc.Chars[_pos - 2] == 'l' &&
                    _doc.Chars[_pos - 1] == 'e')
                {
                    syntaxKind = SyntaxKind.WhileKeywordToken;
                    flagControlKeyword = true;
                    break;
                }
                break;
            case 94001407: // break
                if (length == 5 &&
                    _doc.Chars[_pos - 5] == 'b' &&
                    _doc.Chars[_pos - 4] == 'r' &&
                    _doc.Chars[_pos - 3] == 'e' &&
                    _doc.Chars[_pos - 2] == 'a' &&
                    _doc.Chars[_pos - 1] == 'k')
                {
                    syntaxKind = SyntaxKind.BreakKeywordToken;
                    flagControlKeyword = true;
                    break;
                }
                break;
            case -567202649: // continue
                if (length == 8 &&
                    _doc.Chars[_pos - 8] == 'c' &&
                    _doc.Chars[_pos - 7] == 'o' &&
                    _doc.Chars[_pos - 6] == 'n' &&
                    _doc.Chars[_pos - 5] == 't' &&
                    _doc.Chars[_pos - 4] == 'i' &&
                    _doc.Chars[_pos - 3] == 'n' &&
                    _doc.Chars[_pos - 2] == 'u' &&
                    _doc.Chars[_pos - 1] == 'e')
                {
                    syntaxKind = SyntaxKind.ContinueKeywordToken;
                    flagControlKeyword = true;
                    break;
                }
                break;
            case 3569038: // true
                if (length == 4 &&
                    _doc.Chars[_pos - 4] == 't' &&
                    _doc.Chars[_pos - 3] == 'r' &&
                    _doc.Chars[_pos - 2] == 'u' &&
                    _doc.Chars[_pos - 1] == 'e')
                {
                    syntaxKind = SyntaxKind.TrueKeywordToken;
                    break;
                }
                break;
            case 97196323: // false
                if (length == 5 &&
                    _doc.Chars[_pos - 5] == 'f' &&
                    _doc.Chars[_pos - 4] == 'a' &&
                    _doc.Chars[_pos - 3] == 'l' &&
                    _doc.Chars[_pos - 2] == 's' &&
                    _doc.Chars[_pos - 1] == 'e')
                {
                    syntaxKind = SyntaxKind.FalseKeywordToken;
                    break;
                }
                break;
            case 3392903: // null
                if (length == 4 &&
                    _doc.Chars[_pos - 4] == 'n' &&
                    _doc.Chars[_pos - 3] == 'u' &&
                    _doc.Chars[_pos - 2] == 'l' &&
                    _doc.Chars[_pos - 1] == 'l')
                {
                    syntaxKind = SyntaxKind.NullKeywordToken;
                    break;
                }
                break;
            default:
                break;
        }

        if (_doc.Chars[_pos] == '(')
        {
            if (_previousTokenSyntaxKind == SyntaxKind.NewKeywordToken)
            {
                //SyntaxHighlightByteList.SetSyntaxHighlighting(_pos - length, length, TrackedSyntaxKind.Type);
            }
            else
            {
                //SyntaxHighlightByteList.SetSyntaxHighlighting(_pos - length, length, TrackedSyntaxKind.Function);
            }
        }
        else if (_doc.Chars[_pos] == ':')
        {
            //SyntaxHighlightByteList.SetSyntaxHighlighting(_pos - length, length, TrackedSyntaxKind.Member);
        }
        else if (_previousTokenSyntaxKind == SyntaxKind.PeriodToken)
        {
            if (_previousPreviousTokenSyntaxKind == SyntaxKind.ThisKeywordToken)
            {
                //SyntaxHighlightByteList.SetSyntaxHighlighting(_pos - length, length, TrackedSyntaxKind.Identifier);
            }
            else
            {
                //SyntaxHighlightByteList.SetSyntaxHighlighting(_pos - length, length, TrackedSyntaxKind.Member);
            }
        }
        else
        {
            if (syntaxKind == SyntaxKind.IdentifierToken)
            {
                //SyntaxHighlightByteList.SetSyntaxHighlighting(_pos - length, length, TrackedSyntaxKind.Identifier);
            }
            else if (flagControlKeyword)
            {
                //SyntaxHighlightByteList.SetSyntaxHighlighting(_pos - length, length, TrackedSyntaxKind.KeywordControl);
            }
            else
            {
                //SyntaxHighlightByteList.SetSyntaxHighlighting(_pos - length, length, TrackedSyntaxKind.Keyword);
            }
        }

        if (_peekTokenExists)
        {
            _peekIdentifierHash = charIntSum;
        }
        else
        {
            _identifierHash = charIntSum;
        }
        return new SyntaxToken(syntaxKind, startPosition, length);
    }

    /// <summary>
    /// TODO: alternative syntaxes for typing numbers; supports '123' and '123.456'
    /// </summary>
    public SyntaxToken Lex_Number()
    {
        var startPosition = new Position(_indexLine, _indexChar);
        var length = 1;
        IncPos();
        _indexChar++;

        while (_pos < _doc.Count)
        {
            byte b = _doc.Chars[_pos];
            if (b >= '0' && b <= '9')
            {
                length++;
            }
            else
            {
                if (_doc.Chars[_pos] == '.')
                {
                    length++;
                }
                else
                {
                    break;
                }
            }

            IncPos();
            _indexChar++;
        }

        return new SyntaxToken(SyntaxKind.NumberToken, startPosition, length);
    }

    public SyntaxToken Lex_Whitespace()
    {
        var startPosition = new Position(_indexLine, _indexChar);
        var length = 1;
        switch (_doc.Chars[_pos])
        {
            case 13 /* \r */:
                _indexLine++;
                _indexChar = 0;
                if (_pos <= _doc.Count - 2)
                {
                    if (_doc.Chars[_pos + 1] == 10 /* \n */)
                    {
                        IncPos();
                    }
                }
                break;
            case 10 /* \n */:
                _indexLine++;
                _indexChar = 0;
                break;
            default:
                _indexChar++;
                break;
        }
        IncPos();


        while (_pos < _doc.Count)
        {
            byte b = _doc.Chars[_pos];
            bool isWhiteSpace = b is 0x20 or >= 0x09 and <= 0x0D;
            if (isWhiteSpace)
            {
                length++;
            }
            else
            {
                break;
            }

            switch (_doc.Chars[_pos])
            {
                case 13 /* \r */:
                    _indexLine++;
                    _indexChar = 0;
                    if (_pos <= _doc.Count - 2)
                    {
                        if (_doc.Chars[_pos + 1] == 10 /* \n */)
                        {
                            IncPos();
                        }
                    }
                    break;
                case 10 /* \n */:
                    _indexLine++;
                    _indexChar = 0;
                    break;
                default:
                    _indexChar++;
                    break;
            }
            IncPos();
        }

        return new SyntaxToken(SyntaxKind.WhitespaceToken, startPosition, length);
    }

    public SyntaxToken Lex_String(char terminator)
    {
        _seenLineEnd_flagForStringsAndComments = false;

        var startPosition = new Position(_indexLine, _indexChar);
        var length = 1;
        IncPos();
        _indexChar++;

        while (_pos < _doc.Count)
        {
            switch (_doc.Chars[_pos])
            {
                case 13 /* \r */:
                    length++;
                    IncPos();
                    _indexLine++;
                    _indexChar = 0;
                    if (_pos <= _doc.Count - 1)
                    {
                        if (_doc.Chars[_pos] == 10 /* \n */)
                        {
                            // I'm going to have everything length wise as though '\r\n' are just '\n'.
                            // Maybe is best to make start and end positions I'm not sure.
                            // Either way my goal right now is to get the 'function' "keyword" appearing in text to not result in lsp saying a function definition exists there.
                            IncPos();
                        }
                    }
                    if (terminator == '`')
                    {
                        _seenLineEnd_flagForStringsAndComments = true;
                        break;
                    }
                    else
                    {
                        goto functionEnding;
                    }
                case 10 /* \n */:
                    length++;
                    IncPos();
                    _indexLine++;
                    _indexChar = 0;
                    if (terminator == '`')
                    {
                        _seenLineEnd_flagForStringsAndComments = true;
                        break;
                    }
                    else
                    {
                        goto functionEnding;
                    }
                case 92: // '\'
                    length++;
                    IncPos();
                    _indexChar++;
                    if (_pos <= _doc.Count - 1)
                    {
                        length++;
                        IncPos();
                        _indexChar++;
                    }
                    break;
                default:
                    if (_doc.Chars[_pos] == terminator)
                    {
                        length++;
                        IncPos();
                        _indexChar++;
                        goto functionEnding;
                    }
                    length++;
                    IncPos();
                    _indexChar++;
                    break;
            }
        }

        functionEnding:
        //SyntaxHighlightByteList.SetSyntaxHighlighting(_pos - length, length, TrackedSyntaxKind.String);
        return new SyntaxToken(SyntaxKind.StringToken, startPosition, length, _pos - length);
    }

    public SyntaxToken Lex_SingleLineComment()
    {
        var startPosition = new Position(_indexLine, _indexChar);
        var length = 2;
        IncPos();
        IncPos();
        //_pos += 2;
        _indexChar += 2;

        while (_pos < _doc.Count)
        {
            switch (_doc.Chars[_pos])
            {
                case 13 /* \r */:
                    length++;
                    IncPos();
                    _indexLine++;
                    _indexChar = 0;
                    if (_pos <= _doc.Count - 1)
                    {
                        if (_doc.Chars[_pos] == 10 /* \n */)
                        {
                            // I'm going to have everything length wise as though '\r\n' are just '\n'.
                            // Maybe is best to make start and end positions I'm not sure.
                            // Either way my goal right now is to get the 'function' "keyword" appearing in text to not result in lsp saying a function definition exists there.
                            IncPos();
                        }
                    }
                    goto functionEnding;
                case 10 /* \n */:
                    length++;
                    IncPos();
                    _indexLine++;
                    _indexChar = 0;
                    goto functionEnding;
                default:
                    length++;
                    IncPos();
                    _indexChar++;
                    break;
            }
        }

        functionEnding:
        //SyntaxHighlightByteList.SetSyntaxHighlighting(_pos - length, length, TrackedSyntaxKind.Comment);
        return new SyntaxToken(SyntaxKind.SingleLineCommentToken, startPosition, length);
    }

    /// <summary>
    /// I'm going to have everything length wise as though '\r\n' are just '\n'.
    /// </summary>
    public SyntaxToken Lex_MultiLineComment()
    {
        _seenLineEnd_flagForStringsAndComments = false;

        var startPosition = new Position(_indexLine, _indexChar);
        var length = 2;
        IncPos();
        IncPos();
        //_pos += 2;
        _indexChar += 2;

        while (_pos < _doc.Count)
        {
            switch (_doc.Chars[_pos])
            {
                case 42: // '*'
                    length++;
                    IncPos();
                    _indexChar++;
                    if (_pos <= _doc.Count - 1 &&
                        _doc.Chars[_pos] == 47 /* '/' */)
                    {
                        length++;
                        IncPos();
                        _indexChar++;
                        goto functionEnding;
                    }
                    break;
                case 13: // '\r'
                    _seenLineEnd_flagForStringsAndComments = true;
                    length++;
                    IncPos();
                    _indexLine++;
                    _indexChar = 0;
                    if (_pos <= _doc.Count - 1)
                    {
                        if (_doc.Chars[_pos] == 10 /* \n */)
                        {
                            // I'm going to have everything length wise as though '\r\n' are just '\n'.
                            // Maybe is best to make start and end positions I'm not sure.
                            // Either way my goal right now is to get the 'function' "keyword" appearing in text to not result in lsp saying a function definition exists there.
                            IncPos();
                        }
                    }
                    break;
                case 10 /* \n */:
                    _seenLineEnd_flagForStringsAndComments = true;
                    length++;
                    IncPos();
                    _indexLine++;
                    _indexChar = 0;
                    break;
                default:
                    length++;
                    IncPos();
                    _indexChar++;
                    break;
            }
        }

        functionEnding:
        //SyntaxHighlightByteList.SetSyntaxHighlighting(_pos - length, length, TrackedSyntaxKind.Comment);
        return new SyntaxToken(SyntaxKind.MultiLineCommentToken, startPosition, length, _pos - length);
    }

    private void IncPos()
    {
        _outputFileState.Add(_doc.Chars[_pos]);
        _pos++;
    }
}

/*
 * - [ ] Maybe will do what I call, but don't actually know the name of,  "implicit global variable declaration" where you leave off the 'let', 'const', or 'var' for an undefined variable that you assign.
*/
