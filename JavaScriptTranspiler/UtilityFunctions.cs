using JSLSApp;

namespace JavaScriptTranspiler;

internal class UtilityFunctions
{
    public static JavaScriptDocument ReadIntoJavaScriptDocument(string filePath)
    {
        bool hadBomMark;
        byte lineEndingKind = 2; // 1 = CR, 2 = LF, 3 = CRLF
        const byte CR = 13; // '\r'
        const byte LF = 10; // '\n'
        const int paddingLength = 1024;

        // 1. Get the file size to calculate exactly what we need
        var fileInfo = new FileInfo(filePath);
        int fileSize = (int)fileInfo.Length;

        // 2. High Performance: Allocate ONE uninitialized block of heap memory 
        // This behaves exactly like Node's Buffer.allocUnsafe (O(1) allocation time)
        int totalCapacity = fileSize + paddingLength;
        byte[] fileBytes = GC.AllocateUninitializedArray<byte>(totalCapacity);

        // 3. Open a stream and read directly into the start of our array
        using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            stream.ReadExactly(fileBytes, 0, fileSize);
        }

        ReadOnlySpan<byte> fileSpan = fileBytes.AsSpan(0, fileSize);
        int dataStartIndex = 0;

        // 4. Detect and strip the BOM
        if (fileSpan.Length >= 3 &&
            fileSpan[0] == 0xEF &&
            fileSpan[1] == 0xBB &&
            fileSpan[2] == 0xBF)
        {
            hadBomMark = true;
            dataStartIndex = 3;
        }
        else
        {
            hadBomMark = false;
        }

        // Look at text data boundaries
        ReadOnlySpan<byte> textSpan = fileSpan.Slice(dataStartIndex);

        // 5. Judge line ending style based on first encountered character
        int firstLineBreakIndex = textSpan.IndexOfAny(CR, LF);
        if (firstLineBreakIndex != -1)
        {
            if (textSpan[firstLineBreakIndex] == CR &&
                firstLineBreakIndex + 1 < textSpan.Length &&
                textSpan[firstLineBreakIndex + 1] == LF)
            {
                lineEndingKind = 3;
            }
            else if (textSpan[firstLineBreakIndex] == LF)
            {
                lineEndingKind = 2;
            }
            else if (textSpan[firstLineBreakIndex] == CR)
            {
                lineEndingKind = 1;
            }
        }
        else
        {
            lineEndingKind = 2;
        }

        // 6. In-place, zero-allocation normalization pass
        // Reading from 'i' and overwriting straight into 'targetIndex'
        int targetIndex = 0;
        for (int i = dataStartIndex; i < fileSize; i++)
        {
            byte b = fileBytes[i];

            if (b == CR)
            {
                if (i + 1 < fileSize && fileBytes[i + 1] == LF)
                {
                    // CRLF: skip writing CR, let the next loop handle the LF
                    continue;
                }
                else
                {
                    // Isolated CR: Normalize to LF
                    fileBytes[targetIndex++] = LF;
                    continue;
                }
            }

            fileBytes[targetIndex++] = b;
        }

        int cleanTextLength = targetIndex;
        int targetCapacity = cleanTextLength + paddingLength;

        int remainingLength = fileBytes.Length - cleanTextLength;
        // 7. Security: Zero-out only the 1024 padding bytes to clear out "ghost data"
        // This utilizes a highly optimized native CPU memset command under the hood
        fileBytes.AsSpan(cleanTextLength, remainingLength).Clear();

        return new JavaScriptDocument(filePath, fileBytes, targetCapacity, cleanTextLength, hadBomMark, lineEndingKind);
    }
}
