
// In terminal I can run:
// dotnet "C:\Users\hunte\Repos\New folder (7)\LanguageServer2\JavaScriptTranspiler\bin\Release\net10.0\publish\JavaScriptTranspiler.dll"


// Ensure an argument was actually passed to avoid an IndexOutOfRangeException
using JavaScriptTranspiler;
using JSLSApp;

if (args.Length != 2)
{
    Console.WriteLine($"2 command line arguments are expected (first is the path to the JS source code, second is the path to where the transpiled output goes) (of the 2 required there were '{args.Length}' arguments provided).");
    return;
}


// Capture the very first argument
string sourceFilePath = args[0];
string outputFilePath = args[1];
if (!Path.Exists(sourceFilePath))
{
    Console.WriteLine($"The source path {sourceFilePath} does not exist.");
    return;
}
if (!Path.Exists(outputFilePath))
{
    Console.WriteLine($"The output path {outputFilePath} does not exist.");
    return;
}

var sourceFile = new FileInfo(sourceFilePath);
var outputFile = new FileInfo(outputFilePath);

/// TODO: This is a terrible idea, but the first step is to reproduce the text exactly as is and the way I get that accomplish is gonna get swapped out in the end.
var outputFileText = new List<string>();

OutputFileState outputFileState;


try
{
    var javaScriptDocument = UtilityFunctions.ReadIntoJavaScriptDocument(sourceFile.FullName);
    outputFileState = new OutputFileState(new byte[javaScriptDocument.Capacity], javaScriptDocument.Capacity, 0);
    var javaScriptTranspilerClass = new JavaScriptTranspilerClass(javaScriptDocument, outputFileState);
    javaScriptTranspilerClass.Parse();
    File.WriteAllBytes(outputFile.FullName, new ReadOnlySpan<byte>(outputFileState.Bytes, 0, outputFileState.Count));
}
catch (Exception ex)
{
    Console.WriteLine($"An error occurred: {ex.Message}");
}





