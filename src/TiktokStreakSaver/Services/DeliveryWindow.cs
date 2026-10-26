namespace TiktokStreakSaver.Services;

/// <summary>
/// Works out the stretch of time a message is expected to appear in, and decides whether a chat's messages
/// fall inside it.
/// <para>
/// The check is text <em>and</em> time, because neither is enough alone. Text by itself cannot tell today's
/// message from the identical one sent yesterday - the default streak message never changes - and time by
/// itself cannot tell this chat's message from any other message in the same minute.
/// </para>
/// <para>
/// The window is not the exact minute the user scheduled. A run takes time, and a message that lands a few
/// minutes after the scheduled minute is still a delivered message; treating that as missing would send a
/// second copy. So the window opens when the send was actually attempted and stays open for
/// <see cref="WindowMinutes"/> after it.
/// </para>
/// <para>
/// Kept free of MAUI and Android dependencies so the arithmetic can be tested directly.
/// </para>
/// </summary>
public static class DeliveryWindow
{
    /// <summary>
    /// How long after the send attempt a message still counts as the one that was sent.
    /// <para>
    /// Fifteen minutes, not five: the automation types, clicks, waits for the bubble and moves on, and a
    /// slow phone or a busy chat list can stretch a run well past a couple of minutes. The window only
    /// widens the search - it never causes a resend on its own - so being generous here is the safe
    /// direction.
    /// </para>
    /// </summary>
    public const int WindowMinutes = 15;

    /// <summary>The end of the window that opens at <paramref name="sentAt"/>.</summary>
    public static DateTime EndOf(DateTime sentAt) => sentAt.AddMinutes(WindowMinutes);

    /// <summary>
    /// Whether a message timestamped <paramref name="messageTime"/> counts as the send attempted at
    /// <paramref name="sentAt"/>.
    /// <para>
    /// Only the lower bound matters for the outcome: TikTok groups messages under a single time separator,
    /// and that separator carries the time of the <em>first</em> message in the group, so a later message
    /// can be stamped earlier than it really was. Accepting anything at or after the attempt is what keeps
    /// that from turning into a false "missing".
    /// </para>
    /// </summary>
    public static bool Covers(DateTime sentAt, DateTime messageTime) =>
        messageTime >= sentAt && messageTime <= EndOf(sentAt);
}
