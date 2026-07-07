namespace AdbSync.Core.Transfer;

public interface IExcludeMatcher
{
    bool IsExcluded(string relativePath, bool isDirectory);
}
