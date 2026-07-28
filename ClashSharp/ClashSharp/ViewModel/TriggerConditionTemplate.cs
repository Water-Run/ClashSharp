namespace ClashSharp.ViewModel;

/// <summary>Identifies one user-facing condition template without weakening typed domain parameters.</summary>
internal enum TriggerConditionTemplate
{
    AppEntered = 0,
    ProxyStarted = 1,
    NotificationRaised = 2,
    RollingTraffic = 3,
    SessionTraffic = 4,
    AllTimeTraffic = 5,
    UploadRate = 6,
    DownloadRate = 7,
    ActiveConnections = 8,
    Runtime = 9,
    SystemTime = 10,
}
