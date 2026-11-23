namespace JavaScriptTranspiler;

public class OutputFileState
{
    public OutputFileState(byte[] bytes, int capacity, int count)
    {
        Bytes = bytes;
        Capacity = capacity;
        Count = count;
    }

    public byte[] Bytes { get; set; }
    public int Capacity { get; set; }
    public int Count { get; set; }

    public void Add(byte b)
    {
        Bytes[Count] = b;
        Count++;
    }
}
