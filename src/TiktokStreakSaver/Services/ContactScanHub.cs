namespace TiktokStreakSaver.Services;

/// <summary>
/// The one <see cref="ContactScanStore"/> the app uses.
/// <para>
/// A single instance matters: the scan runs in a service and is watched by a page, both in the same
/// process, and it is the in-memory copy of the store that makes that page's progress live rather than
/// two seconds stale. Two instances would silently give each its own copy and the page would only ever
/// see what had been flushed to disk.
/// </para>
/// </summary>
[Microsoft.Maui.Controls.Internals.Preserve(AllMembers = true)]
public static class ContactScanHub
{
    private static readonly object _lock = new();
    private static ContactScanStore? _store;

    public static ContactScanStore Store
    {
        get
        {
            lock (_lock)
            {
                return _store ??= new ContactScanStore(
                    System.IO.Path.Combine(FileSystem.AppDataDirectory, AppConstants.ContactScanFileName));
            }
        }
    }
}
