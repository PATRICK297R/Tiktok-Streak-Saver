namespace TiktokStreakSaver.Services;

using System.Text.Json;
using TiktokStreakSaver.Models;

/// <summary>
/// What a backup file holds, read out before anything is written to the device.
/// </summary>
/// <param name="Friends">Number of friend entries in the file.</param>
/// <param name="Groups">Number of group entries in the file.</param>
/// <param name="FormatVersion">Backup format the file was written with.</param>
/// <param name="AppVersion">App version that wrote the file, or empty when unknown.</param>
/// <param name="ExportedAt">When the file was written.</param>
/// <param name="RunHistoryCount">Number of past runs in the file.</param>
public readonly record struct BackupSummary(
    int Friends,
    int Groups,
    int FormatVersion,
    string AppVersion,
    DateTime ExportedAt,
    int RunHistoryCount)
{
    /// <summary>The date, or a neutral phrase when the file carries no timestamp.</summary>
    public string ExportedAtText => ExportedAt == default ? AppStrings.T("Bk_UnknownDate") : ExportedAt.ToString("MMM dd, yyyy HH:mm");
}

/// <summary>
/// Reads and writes the backup file.
/// <para>
/// Reads are strict on purpose. A restore overwrites settings the user cannot easily reconstruct, so
/// a file this build does not fully understand has to be refused with a reason rather than applied
/// as far as it parses.
/// </para>
/// <para>
/// Kept free of MAUI and Android dependencies so it can be unit tested directly.
/// </para>
/// </summary>
[Microsoft.Maui.Controls.Internals.Preserve(AllMembers = true)]
public static class AppBackupCodec
{
    public static string Serialize(AppBackup backup) =>
        JsonSerializer.Serialize(backup, AppJsonContext.Default.AppBackup);

    /// <summary>
    /// Parses a backup file. Returns false with a user-facing reason when the text is not a backup,
    /// is damaged, or was written by a newer build.
    /// </summary>
    public static bool TryDeserialize(string? json, out AppBackup? backup, out string? error)
    {
        backup = null;
        error = null;

        if (string.IsNullOrWhiteSpace(json))
        {
            error = AppStrings.T("Bk_FileEmpty");
            return false;
        }

        AppBackup? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize(json, AppJsonContext.Default.AppBackup);
        }
        catch (JsonException)
        {
            error = AppStrings.T("Bk_NotBackup");
            return false;
        }

        if (parsed is null)
        {
            error = AppStrings.T("Bk_NotBackup");
            return false;
        }

        // A newer format may store things this build would silently drop, so it is refused instead
        // of half-applied.
        if (parsed.FormatVersion > AppBackup.CurrentFormatVersion)
        {
            error = string.Format(AppStrings.T("Bk_NewerFormat"), parsed.FormatVersion);
            return false;
        }

        backup = Normalize(parsed);
        return true;
    }

    /// <summary>
    /// Describes a parsed file so the user can confirm it is the right one before it is applied.
    /// </summary>
    public static BackupSummary Summarize(AppBackup backup)
    {
        var friends = backup.Friends?.Count(f => f is not null && !f.IsGroup) ?? 0;
        var groups = backup.Friends?.Count(f => f is not null && f.IsGroup) ?? 0;

        return new BackupSummary(
            friends,
            groups,
            backup.FormatVersion <= 0 ? AppBackup.CurrentFormatVersion : backup.FormatVersion,
            backup.AppVersion ?? string.Empty,
            backup.ExportedAt,
            backup.RunHistory?.Count ?? 0);
    }

    /// <summary>
    /// Fills in the collections a hand-edited or older file may have left out, so nothing
    /// downstream has to null-check them.
    /// </summary>
    private static AppBackup Normalize(AppBackup backup)
    {
        backup.Friends ??= new List<FriendConfig>();
        backup.RunHistory ??= new List<StreakRunResult>();
        backup.Settings ??= new BackupSettings();
        backup.Settings.FixedDailyTimes ??= new List<string>();
        backup.Settings.MessageText ??= string.Empty;
        backup.SessionDisplayName ??= string.Empty;
        backup.AppVersion ??= string.Empty;
        return backup;
    }
}
