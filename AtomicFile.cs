using System.IO;

namespace EazyLoop;

internal static class AtomicFile
{
    internal static void Replace(string target, Action<string> write)
    {
        target = Path.GetFullPath(target);
        string folder = Path.GetDirectoryName(target)!;
        string temporary = Path.Combine(folder, $".eazyloop-{Guid.NewGuid():N}.tmp");
        try
        {
            write(temporary);
            File.Move(temporary, target, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
