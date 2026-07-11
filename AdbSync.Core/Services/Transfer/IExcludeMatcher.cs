using AdbSync.Core.Models.Transfer;

namespace AdbSync.Core.Services.Transfer;

public interface IExcludeMatcher
{
    bool IsExcluded(string relativePath, bool isDirectory);
}
