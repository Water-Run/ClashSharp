using ClashSharp.Model;

namespace ClashSharp.Service;

public sealed partial class LogStorageService
{
    int ILogStorage.AppendTrafficSnapshot(MihomoTrafficSnapshot snapshot)
    {
        return AppendTrafficSnapshot(snapshot);
    }
}
