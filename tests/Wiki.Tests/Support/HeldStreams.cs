using System.Threading;

namespace Wiki.Tests.Support;

// A stdin that blocks the command reading it until the test releases it.
// `page upsert --stdin` reads its body inside its CLI step, so handing it one
// of these parks a writer mid-step, holding whatever the step holds, for as
// long as the test needs.
public sealed class HeldStdin(string body) : System.IO.TextReader
{
    private readonly ManualResetEventSlim _entered = new();
    private readonly ManualResetEventSlim _released = new();

    public bool WaitUntilEntered(System.TimeSpan timeout) => _entered.Wait(timeout);
    public void Release() => _released.Set();

    public override string ReadToEnd()
    {
        _entered.Set();
        _released.Wait();
        return body;
    }
}

// A stdout that blocks on its first write until the test releases it. A read
// command writes its result inside its CLI step, so this parks a reader
// mid-step.
public sealed class HeldStdout : System.IO.TextWriter
{
    private readonly ManualResetEventSlim _entered = new();
    private readonly ManualResetEventSlim _released = new();

    public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;
    public bool WaitUntilEntered(System.TimeSpan timeout) => _entered.Wait(timeout);
    public void Release() => _released.Set();

    private void Hold()
    {
        _entered.Set();
        _released.Wait();
    }

    public override void Write(char value) => Hold();
    public override void Write(string? value) => Hold();
    public override void Write(char[] buffer, int index, int count) => Hold();
}
