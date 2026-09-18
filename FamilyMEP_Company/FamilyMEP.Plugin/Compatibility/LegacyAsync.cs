#if NET48
using System.Diagnostics;
namespace FamilyMEP.Plugin.Compatibility;
internal static class LegacyAsync
{
    public static async Task<string> ReadToEndAsync(this StreamReader reader, CancellationToken token)
    {
        Task<string> read = reader.ReadToEndAsync();
        var cancelled = new TaskCompletionSource<bool>();
        using (token.Register(() => cancelled.TrySetResult(true)))
        {
            if (await Task.WhenAny(read, cancelled.Task).ConfigureAwait(false) != read)
                throw new OperationCanceledException(token);
            return await read.ConfigureAwait(false);
        }
    }
    public static async Task WaitForExitAsync(this Process process, CancellationToken token)
    {
        while (!process.HasExited) await Task.Delay(50,token).ConfigureAwait(false);
    }
}
#endif
