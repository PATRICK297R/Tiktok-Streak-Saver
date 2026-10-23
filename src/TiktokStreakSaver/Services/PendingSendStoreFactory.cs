namespace TiktokStreakSaver.Services;

/// <summary>
/// Builds the <see cref="PendingSendStore"/> that points at the app's own data directory - the
/// same file StreakService writes.
/// <para>
/// This lives here rather than as a static factory on <see cref="PendingSendStore"/> on purpose.
/// The store is compiled into the unit-test project, which targets plain net9.0 with no MAUI
/// reference, so touching FileSystem or AppConstants from inside it breaks the test build with
/// "The name 'FileSystem' does not exist in the current context". Keeping the app-only dependency
/// in its own file lets the store stay testable.
/// </para>
/// </summary>
[Microsoft.Maui.Controls.Internals.Preserve(AllMembers = true)]
public static class PendingSendStoreFactory
{
    /// <summary>
    /// The store backed by the app's data directory, for a deliberate reset.
    /// </summary>
    public static PendingSendStore ForCurrentApp() =>
        new(System.IO.Path.Combine(FileSystem.AppDataDirectory, AppConstants.PendingSendsFileName));
}
