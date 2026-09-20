using Microsoft.Maui.Controls.Shapes;
using TiktokStreakSaver.Models;
using TiktokStreakSaver.Services;

namespace TiktokStreakSaver.Pages;

[Microsoft.Maui.Controls.Internals.Preserve(AllMembers = true)]
public partial class FriendsPage : ContentPage
{
    private readonly SettingsService _settingsService;
    private readonly SessionService _sessionService;
    private bool _lastIsRunning = false;
    private IDispatcherTimer? _statusTimer;
    private bool _isGroupMode = false;

    /// <summary>Whether the "Send order" card is showing its list. Kept across refreshes.</summary>
    private bool _sendOrderExpanded = false;

    public FriendsPage()
    {
        InitializeComponent();
        _settingsService = new SettingsService();
        _sessionService = new SessionService();
    }

    private void OnSessionStateChanged(object? sender, EventArgs e)
    {
        MainThread.BeginInvokeOnMainThread(async () => await RefreshLoginBannerAsync());
    }

    private Color GetThemeColor(string key, string fallbackHex = "#92979E")
    {
        if (Application.Current != null && Application.Current.Resources.TryGetValue(key, out var resource) && resource is Color color)
            return color;
        return Color.FromArgb(fallbackHex);
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        SessionState.Changed -= OnSessionStateChanged;
        SessionState.Changed += OnSessionStateChanged;

        this.Opacity = 0;
        this.TranslationY = 12;
        await Task.WhenAll(
            this.FadeTo(1, 280, Easing.SinInOut),
            this.TranslateTo(0, 0, 280, Easing.SinInOut));
        LoadLists();
        await RefreshLoginBannerAsync();

        if (_statusTimer == null)
        {
            _statusTimer = Dispatcher.CreateTimer();
            _statusTimer.Interval = TimeSpan.FromSeconds(1);
            _statusTimer.Tick += OnStatusTimerTick;
        }
        _statusTimer.Start();
        OnStatusTimerTick(null, EventArgs.Empty);
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        SessionState.Changed -= OnSessionStateChanged;
        if (_statusTimer != null)
        {
            _statusTimer.Stop();
            _statusTimer.Tick -= OnStatusTimerTick;
            _statusTimer = null;
        }
    }

    private void OnStatusTimerTick(object? sender, EventArgs e)
    {
        bool isRunning = false;
#if ANDROID
        isRunning = TiktokStreakSaver.Platforms.Android.Services.StreakService.IsRunning;
#endif
        if (_lastIsRunning != isRunning)
        {
            _lastIsRunning = isRunning;
            LoadLists();

            SearchAndBulkRow.IsEnabled = !isRunning;
            SearchAndBulkRow.Opacity = isRunning ? 0.6 : 1.0;
            ActionButtonsGrid.IsEnabled = !isRunning;
            ActionButtonsGrid.Opacity = isRunning ? 0.6 : 1.0;

            GroupBulkRow.IsEnabled = !isRunning;
            GroupBulkRow.Opacity = isRunning ? 0.6 : 1.0;
            GroupActionButtonsGrid.IsEnabled = !isRunning;
            GroupActionButtonsGrid.Opacity = isRunning ? 0.6 : 1.0;

            if (AddFriendPanel.IsVisible && isRunning) AddFriendPanel.IsVisible = false;
            if (AddGroupPanel.IsVisible && isRunning) AddGroupPanel.IsVisible = false;

            // Reordering mid-run could not affect the run already in flight, so it is not offered.
            SendOrderToggleButton.IsEnabled = !isRunning;
            SendOrderToggleButton.Opacity = isRunning ? 0.6 : 1.0;
        }
    }

    private void OnRefreshing(object? sender, EventArgs e)
    {
        LoadLists();
        MainRefreshView.IsRefreshing = false;
    }

    private void LoadLists()
    {
        var allItems = _settingsService.GetFriendsList();
        var friendsAll = allItems.Where(f => !f.IsGroup).ToList();
        var groupsAll = allItems.Where(f => f.IsGroup).ToList();

        // ── Friends ──
        SearchAndBulkRow.IsVisible = friendsAll.Count > 0;
        var friendSearch = SearchFriendEntry.Text?.Trim() ?? string.Empty;
        var friendsToShow = string.IsNullOrEmpty(friendSearch)
            ? friendsAll
            : friendsAll.Where(f =>
                (f.Username != null && f.Username.Contains(friendSearch, StringComparison.OrdinalIgnoreCase)) ||
                (f.DisplayName != null && f.DisplayName.Contains(friendSearch, StringComparison.OrdinalIgnoreCase))
            ).ToList();

        var friendItemsToRemove = FriendsListContainer.Children.Where(c => c != NoFriendsLabel).ToList();
        foreach (var item in friendItemsToRemove) FriendsListContainer.Children.Remove(item);

        if (friendsAll.Count == 0)
        {
            NoFriendsLabel.Text = "No friends added. Tap 'Add' to begin.";
            NoFriendsLabel.IsVisible = true;
        }
        else if (friendsToShow.Count == 0 && !string.IsNullOrEmpty(friendSearch))
        {
            NoFriendsLabel.Text = $"No friends found matching '{friendSearch}'";
            NoFriendsLabel.IsVisible = true;
        }
        else
        {
            NoFriendsLabel.IsVisible = false;
        }
        foreach (var friend in friendsToShow) FriendsListContainer.Children.Add(CreateEntryView(friend));

        // ── Groups ──
        GroupBulkRow.IsVisible = groupsAll.Count > 0;
        var groupSearch = SearchGroupEntry.Text?.Trim() ?? string.Empty;
        var groupsToShow = string.IsNullOrEmpty(groupSearch)
            ? groupsAll
            : groupsAll.Where(g =>
                (g.DisplayName != null && g.DisplayName.Contains(groupSearch, StringComparison.OrdinalIgnoreCase))
            ).ToList();

        var groupItemsToRemove = GroupsListContainer.Children.Where(c => c != NoGroupsLabel).ToList();
        foreach (var item in groupItemsToRemove) GroupsListContainer.Children.Remove(item);

        if (groupsAll.Count == 0)
        {
            NoGroupsLabel.Text = "No groups added. Tap 'Add' to begin.";
            NoGroupsLabel.IsVisible = true;
        }
        else if (groupsToShow.Count == 0 && !string.IsNullOrEmpty(groupSearch))
        {
            NoGroupsLabel.Text = $"No groups found matching '{groupSearch}'";
            NoGroupsLabel.IsVisible = true;
        }
        else
        {
            NoGroupsLabel.IsVisible = false;
        }
        foreach (var group in groupsToShow) GroupsListContainer.Children.Add(CreateEntryView(group));

        UpdateStatsCard(allItems);
        RefreshSendOrder(allItems);
    }

    // ─── Send order ──────────────────────────────────────────────────────────

    /// <summary>
    /// Shows the exact order entries will be messaged in.
    /// <para>
    /// The Friends and Groups sections below split one saved list into two, so neither can show this:
    /// a group saved before a friend is messaged first while being displayed underneath. This list is
    /// the queue itself, so what the user sees is what the runner will do.
    /// </para>
    /// </summary>
    private void RefreshSendOrder(List<FriendConfig>? all = null)
    {
        var queue = FriendOrder.SendQueue(all ?? _settingsService.GetFriendsList());

        // One entry needs no ordering, and nothing at all has nothing to show.
        SendOrderCard.IsVisible = queue.Count >= 2;
        SendOrderSubtitleLabel.Text = $"{queue.Count} entries, messaged top to bottom";

        SendOrderToggleButton.Text = _sendOrderExpanded ? "Done" : "Reorder";
        SendOrderListContainer.IsVisible = _sendOrderExpanded;
        SendOrderHintLabel.IsVisible = _sendOrderExpanded;

        var rowsToRemove = SendOrderListContainer.Children.ToList();
        foreach (var row in rowsToRemove) SendOrderListContainer.Children.Remove(row);

        if (!_sendOrderExpanded)
            return;

        for (var i = 0; i < queue.Count; i++)
            SendOrderListContainer.Children.Add(CreateSendOrderRow(queue[i], i, queue.Count));
    }

    private void OnToggleSendOrderClicked(object? sender, EventArgs e)
    {
        _sendOrderExpanded = !_sendOrderExpanded;
        RefreshSendOrder();
    }

    private View CreateSendOrderRow(FriendConfig friend, int index, int total)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitionCollection
            {
                new ColumnDefinition { Width = new GridLength(26) },
                new ColumnDefinition { Width = GridLength.Star },
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = GridLength.Auto }
            },
            ColumnSpacing = 6
        };

        var position = new Label
        {
            Text = (index + 1).ToString(),
            FontFamily = "InterSemiBold",
            FontSize = 14,
            HorizontalTextAlignment = TextAlignment.Center,
            VerticalTextAlignment = TextAlignment.Center,
            VerticalOptions = LayoutOptions.Center
        };
        position.SetAppThemeColor(Label.TextColorProperty,
            GetThemeColor("Primary", "#FE2C55"), GetThemeColor("Primary", "#FE2C55"));
        grid.Children.Add(position);

        var displayName = string.IsNullOrEmpty(friend.DisplayName) ? friend.Username : friend.DisplayName;
        var infoStack = new VerticalStackLayout { Spacing = 2, VerticalOptions = LayoutOptions.Center };
        infoStack.Children.Add(new Label { Text = displayName, FontSize = 14, FontFamily = "InterMedium" });
        infoStack.Children.Add(new Label
        {
            Text = friend.IsGroup ? "Group" : $"@{friend.Username}",
            FontSize = 12,
            TextColor = GetThemeColor("Gray400", "#8B8F96")
        });
        Grid.SetColumn(infoStack, 1);
        grid.Children.Add(infoStack);

        grid.Children.Add(CreateSendOrderMoveButton(friend, up: true, canMove: index > 0, column: 2));
        grid.Children.Add(CreateSendOrderMoveButton(friend, up: false, canMove: index < total - 1, column: 3));

        var row = new Border
        {
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 12 },
            Padding = new Thickness(10, 6)
        };
        row.SetAppThemeColor(Border.BackgroundColorProperty,
            GetThemeColor("Gray100", "#F3F4F6"), GetThemeColor("Gray900", "#111827"));
        row.Content = grid;
        return row;
    }

    private Button CreateSendOrderMoveButton(FriendConfig friend, bool up, bool canMove, int column)
    {
        var button = new Button
        {
            Text = up ? "↑" : "↓",
            BackgroundColor = Colors.Transparent,
            FontSize = 18,
            Padding = new Thickness(10, 0),
            HeightRequest = 40,
            WidthRequest = 44,
            VerticalOptions = LayoutOptions.Center,
            IsEnabled = canMove && !_lastIsRunning
        };
        button.SetAppThemeColor(Button.TextColorProperty,
            GetThemeColor("Primary", "#FE2C55"), GetThemeColor("Primary", "#FE2C55"));

        // The entry at either end has nowhere to go, so its arrow is dimmed rather than silently
        // doing nothing.
        button.Opacity = button.IsEnabled ? 1.0 : 0.35;

        button.Clicked += (s, e) => MoveSendOrderEntry(friend.Id, up);
        Grid.SetColumn(button, column);
        return button;
    }

    /// <summary>
    /// Applies one move and saves it. The save result is checked rather than discarded, because a
    /// silent write failure would look exactly like the arrows not working.
    /// </summary>
    private void MoveSendOrderEntry(string friendId, bool up)
    {
        var all = _settingsService.GetFriendsList();
        var moved = up ? FriendOrder.TryMoveUp(all, friendId) : FriendOrder.TryMoveDown(all, friendId);

        if (!moved)
            return;

        if (!_settingsService.TrySaveFriendsList(all, out var error))
            _ = DisplayAlert("Could not save order", error ?? "The new order was not saved.", "OK");

        // Re-reads the saved list either way, so the page shows what was actually stored.
        LoadLists();
    }

    private void UpdateStatsCard(List<FriendConfig> allItems)
    {
        FriendsStatsCard.IsVisible = allItems.Count > 0;
        TotalFriendsLabel.Text = allItems.Count.ToString();
        EnabledFriendsLabel.Text = allItems.Count(f => f.IsEnabled).ToString();
        var today = DateTime.Now.Date;
        SentTodayLabel.Text = allItems.Count(f => f.LastMessageSent.HasValue && f.LastMessageSent.Value.Date == today).ToString();
    }

    /// <summary>
    /// Shortens a custom message for the friend row. The full text stays untouched in storage; this is
    /// only so a long message cannot stretch the row.
    /// </summary>
    private static string TruncateForRow(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return string.Empty;

        var flat = message.Replace('\n', ' ').Replace('\r', ' ').Trim();

        return flat.Length <= 40 ? flat : flat.Substring(0, 40) + "…";
    }

    private View CreateEntryView(FriendConfig friend)
    {
        var border = new Border
        {
            StrokeShape = new RoundRectangle { CornerRadius = 16 },
            Stroke = Colors.Transparent,
            Padding = new Thickness(14, 12),
            Opacity = 0,
            TranslationY = 10
        };
        border.SetAppThemeColor(Border.BackgroundColorProperty,
            GetThemeColor("Gray100", "#F3F4F6"),
            GetThemeColor("Gray900", "#111827"));
        _ = border.FadeTo(1, 300, Easing.CubicOut);
        _ = border.TranslateTo(0, 0, 300, Easing.CubicOut);

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitionCollection
            {
                new ColumnDefinition { Width = GridLength.Star },
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = GridLength.Auto }
            },
            ColumnSpacing = 8
        };

        var infoStack = new VerticalStackLayout { Spacing = 3 };
        var displayName = string.IsNullOrEmpty(friend.DisplayName) ? friend.Username : friend.DisplayName;
        infoStack.Children.Add(new Label { Text = displayName, FontSize = 15, FontFamily = "InterSemiBold" });
        var subtitleText = friend.IsGroup ? "Group" : $"@{friend.Username}";
        infoStack.Children.Add(new Label { Text = subtitleText, FontSize = 13, TextColor = GetThemeColor("Gray400", "#8B8F96") });
        if (friend.LastMessageSent.HasValue)
            infoStack.Children.Add(new Label { Text = $"Last sent: {friend.LastMessageSent.Value:MMM dd}", FontSize = 12, TextColor = GetThemeColor("Gray400", "#8B8F96") });

        // Shows at a glance that this entry sends something different, so a custom message is never
        // set by accident and forgotten.
        if (friend.HasCustomMessage)
        {
            var customLabel = new Label
            {
                Text = $"Own message: {TruncateForRow(friend.CustomMessage)}",
                FontSize = 12,
                LineBreakMode = LineBreakMode.TailTruncation,
                MaxLines = 1,
            };
            customLabel.SetAppThemeColor(Label.TextColorProperty,
                Color.FromArgb("#B45309"), Color.FromArgb("#F59E0B"));
            infoStack.Children.Add(customLabel);
        }

        grid.Children.Add(infoStack);

        var messageButton = new Button { Text = "Message", BackgroundColor = Colors.Transparent, FontSize = 12, Padding = new Thickness(8), HeightRequest = 44, VerticalOptions = LayoutOptions.Center, IsEnabled = !_lastIsRunning, Opacity = _lastIsRunning ? 0.6 : 1.0 };
        messageButton.SetAppThemeColor(Button.TextColorProperty, GetThemeColor("Gray400"), GetThemeColor("Gray400"));
        messageButton.Clicked += async (s, e) =>
        {
            var noun = friend.IsGroup ? "group" : $"@{friend.Username}";
            var promptTitle = friend.IsGroup ? "Group Message" : "Friend Message";
            var promptMessage = $"Message to send to {noun}.\n\nLeave blank to use the shared message from the Dashboard. "
                              + "You can still use {name} and {username} here.";

            var entered = await DisplayPromptAsync(promptTitle, promptMessage, initialValue: friend.CustomMessage ?? string.Empty);

            // null means the prompt was cancelled, which must not wipe an existing message.
            if (entered == null) return;

            friend.CustomMessage = MessageResolver.Normalize(entered);
            _settingsService.UpdateFriend(friend);
            LoadLists();
        };
        Grid.SetColumn(messageButton, 1); grid.Children.Add(messageButton);

        var editButton = new Button { Text = "Edit", BackgroundColor = Colors.Transparent, FontSize = 12, Padding = new Thickness(8), HeightRequest = 44, VerticalOptions = LayoutOptions.Center, IsEnabled = !_lastIsRunning, Opacity = _lastIsRunning ? 0.6 : 1.0 };
        editButton.SetAppThemeColor(Button.TextColorProperty, GetThemeColor("Gray400"), GetThemeColor("Gray400"));
        editButton.Clicked += async (s, e) =>
        {
            var promptTitle = friend.IsGroup ? "Edit Group" : "Edit Friend";
            var promptMessage = friend.IsGroup
                ? "Enter the group name (must match the TikTok chat header):"
                : "Enter new display name:";
            var newName = await DisplayPromptAsync(promptTitle, promptMessage, initialValue: friend.DisplayName ?? friend.Username);
            if (newName != null) { friend.DisplayName = newName; _settingsService.UpdateFriend(friend); LoadLists(); }
        };
        Grid.SetColumn(editButton, 2); grid.Children.Add(editButton);

        var deleteButton = new Button { Text = "Delete", BackgroundColor = Colors.Transparent, FontSize = 12, Padding = new Thickness(8), HeightRequest = 44, VerticalOptions = LayoutOptions.Center, IsEnabled = !_lastIsRunning, Opacity = _lastIsRunning ? 0.6 : 1.0 };
        deleteButton.TextColor = GetThemeColor("DeleteColor", "#EE1D52");
        deleteButton.Clicked += async (s, e) =>
        {
            var noun = friend.IsGroup ? "group" : "friend";
            var confirm = await DisplayAlert($"Remove {noun}", $"Remove {displayName} from the list?", "Remove", "Cancel");
            if (confirm) { _settingsService.RemoveFriend(friend.Id); LoadLists(); }
        };
        Grid.SetColumn(deleteButton, 3); grid.Children.Add(deleteButton);

        var toggleSwitch = new Switch { IsToggled = friend.IsEnabled, VerticalOptions = LayoutOptions.Center, IsEnabled = !_lastIsRunning, Opacity = _lastIsRunning ? 0.6 : 1.0 };
        toggleSwitch.SetAppThemeColor(Switch.ThumbColorProperty, GetThemeColor("White"), GetThemeColor("White"));
        toggleSwitch.SetAppThemeColor(Switch.OnColorProperty, GetThemeColor("Primary", "#FE2C55"), GetThemeColor("Primary", "#FE2C55"));
        toggleSwitch.Toggled += (s, e) =>
        {
            friend.IsEnabled = e.Value;
            _settingsService.UpdateFriend(friend);

            // Disabling an entry takes it out of the queue, so the send order has to be redrawn.
            RefreshSendOrder();
        };
        Grid.SetColumn(toggleSwitch, 4); grid.Children.Add(toggleSwitch);

        border.Content = grid;
        return border;
    }

    // ─── Mode switching ──────────────────────────────────────────────────────

    private void OnFriendsModeTapped(object? sender, TappedEventArgs e)
    {
        if (!_isGroupMode) return;
        _isGroupMode = false;

        FriendsModeTabBorder.BackgroundColor = GetThemeColor("Primary", "#FE2C55");
        FriendsModeTabLabel.TextColor = GetThemeColor("White", "#FFFFFF");
        GroupsModeTabBorder.BackgroundColor = Colors.Transparent;
        GroupsModeTabLabel.TextColor = GetThemeColor("Gray600", "#5F636A");

        FriendsModeContainer.IsVisible = true;
        GroupsModeContainer.IsVisible = false;
    }

    private void OnGroupsModeTapped(object? sender, TappedEventArgs e)
    {
        if (_isGroupMode) return;
        _isGroupMode = true;

        GroupsModeTabBorder.BackgroundColor = GetThemeColor("Primary", "#FE2C55");
        GroupsModeTabLabel.TextColor = GetThemeColor("White", "#FFFFFF");
        FriendsModeTabBorder.BackgroundColor = Colors.Transparent;
        FriendsModeTabLabel.TextColor = GetThemeColor("Gray600", "#5F636A");

        FriendsModeContainer.IsVisible = false;
        GroupsModeContainer.IsVisible = true;
    }

    // ─── Friends actions ─────────────────────────────────────────────────────

    private void OnSearchFriendTextChanged(object? sender, TextChangedEventArgs e) => LoadLists();

    private void OnAddFriendClicked(object? sender, EventArgs e)
    {
        AddFriendPanel.IsVisible = true;
        NewFriendUsernameEntry.Text = string.Empty;
        NewFriendDisplayNameEntry.Text = string.Empty;
        NewFriendMessageEditor.Text = string.Empty;
        NewFriendUsernameEntry.Focus();
    }

    private void OnCancelAddFriend(object? sender, EventArgs e) => AddFriendPanel.IsVisible = false;

    private async void OnSaveFriend(object? sender, EventArgs e)
    {
        var username = NewFriendUsernameEntry.Text?.Trim().TrimStart('@');
        var displayName = NewFriendDisplayNameEntry.Text?.Trim();
        if (string.IsNullOrEmpty(username)) { await DisplayAlert("Error", "Please enter a username", "OK"); return; }
        var existing = _settingsService.GetFriendsList();
        if (existing.Any(f => !f.IsGroup && f.Username.Equals(username, StringComparison.OrdinalIgnoreCase)))
        { await DisplayAlert("Error", "This friend is already in your list", "OK"); return; }
        var friend = new FriendConfig
        {
            Username = username,
            DisplayName = displayName ?? string.Empty,
            IsEnabled = true,
            IsGroup = false,
            CustomMessage = MessageResolver.Normalize(NewFriendMessageEditor.Text)
        };
        if (!_settingsService.TryAddFriend(friend, out var error))
        {
            await DisplayAlert("Could Not Save", error ?? "Friend could not be saved on this device.", "OK");
            return;
        }
        AddFriendPanel.IsVisible = false;
        LoadLists();
    }

    private void OnEnableAllClicked(object? sender, EventArgs e) => SetEnabledForAll(group: false, enabled: true);
    private void OnDisableAllClicked(object? sender, EventArgs e) => SetEnabledForAll(group: false, enabled: false);

    private void SetEnabledForAll(bool group, bool enabled)
    {
        var all = _settingsService.GetFriendsList();
        var subset = all.Where(f => f.IsGroup == group).ToList();
        if (subset.Count == 0) return;
        foreach (var f in subset) f.IsEnabled = enabled;
        _settingsService.SaveFriendsList(all);
        LoadLists();
    }

    private async void OnDeleteAllFriendsClicked(object? sender, EventArgs e)
    {
        var all = _settingsService.GetFriendsList();
        var groupsOnly = all.Where(f => f.IsGroup).ToList();
        if (all.Count == groupsOnly.Count) return;
        bool confirm = await DisplayAlert("Clear Friends",
            "Remove all direct messages? Group chats will not be affected.", "Clear", "Cancel");
        if (confirm)
        {
            _settingsService.SaveFriendsList(groupsOnly);
            LoadLists();
        }
    }

    // ─── Groups actions ──────────────────────────────────────────────────────

    private void OnSearchGroupTextChanged(object? sender, TextChangedEventArgs e) => LoadLists();

    private void OnAddGroupClicked(object? sender, EventArgs e)
    {
        AddGroupPanel.IsVisible = true;
        NewGroupNameEntry.Text = string.Empty;
        NewGroupDisplayNameEntry.Text = string.Empty;
        NewGroupMessageEditor.Text = string.Empty;
        NewGroupNameEntry.Focus();
    }

    private void OnCancelAddGroup(object? sender, EventArgs e) => AddGroupPanel.IsVisible = false;

    private async void OnSaveGroup(object? sender, EventArgs e)
    {
        var matchName = NewGroupNameEntry.Text?.Trim();
        var displayName = NewGroupDisplayNameEntry.Text?.Trim();
        if (string.IsNullOrEmpty(matchName)) { await DisplayAlert("Error", "Please enter a group chat name", "OK"); return; }

        var existing = _settingsService.GetFriendsList();
        if (existing.Any(f => f.IsGroup && f.DisplayName.Equals(matchName, StringComparison.OrdinalIgnoreCase)))
        { await DisplayAlert("Error", "This group is already in your list", "OK"); return; }

        // For groups, DisplayName doubles as the matching key against the TikTok chat header.
        var group = new FriendConfig
        {
            Username = string.Empty,
            DisplayName = string.IsNullOrEmpty(displayName) ? matchName : matchName,
            IsGroup = true,
            IsEnabled = true,
            CustomMessage = MessageResolver.Normalize(NewGroupMessageEditor.Text)
        };
        if (!_settingsService.TryAddFriend(group, out var error))
        {
            await DisplayAlert("Could Not Save", error ?? "Group could not be saved on this device.", "OK");
            return;
        }
        AddGroupPanel.IsVisible = false;
        LoadLists();
    }

    private void OnEnableAllGroupsClicked(object? sender, EventArgs e) => SetEnabledForAll(group: true, enabled: true);
    private void OnDisableAllGroupsClicked(object? sender, EventArgs e) => SetEnabledForAll(group: true, enabled: false);

    private async void OnDeleteAllGroupsClicked(object? sender, EventArgs e)
    {
        var all = _settingsService.GetFriendsList();
        var friendsOnly = all.Where(f => !f.IsGroup).ToList();
        if (all.Count == friendsOnly.Count) return;
        bool confirm = await DisplayAlert("Clear Groups",
            "Remove all group chats? Direct messages will not be affected.", "Clear", "Cancel");
        if (confirm)
        {
            _settingsService.SaveFriendsList(friendsOnly);
            LoadLists();
        }
    }

    // ─── Import / Export (friend list — both groups & friends are persisted together) ──

    private async void OnImportFriendsClicked(object? sender, EventArgs e)
    {
        try
        {
            var result = await FilePicker.Default.PickAsync(new PickOptions
            {
                PickerTitle = "Select friend list JSON file",
                FileTypes = new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
                {
                    { DevicePlatform.Android, new[] { "application/json", "*/*" } },
                    { DevicePlatform.iOS, new[] { "public.json" } },
                    { DevicePlatform.WinUI, new[] { ".json" } },
                    { DevicePlatform.macOS, new[] { "json" } },
                })
            });
            if (result == null) return;
            string json;
            using (var stream = await result.OpenReadAsync())
            using (var reader = new System.IO.StreamReader(stream))
                json = await reader.ReadToEndAsync();
            if (string.IsNullOrWhiteSpace(json)) { await DisplayAlert("Import Failed", "The selected file is empty.", "OK"); return; }

            List<FriendConfig>? imported;
            try
            {
                var options = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                imported = System.Text.Json.JsonSerializer.Deserialize<List<FriendConfig>>(json, options);
            }
            catch { await DisplayAlert("Import Failed", "The file is not a valid friend list.", "OK"); return; }
            if (imported == null || imported.Count == 0) { await DisplayAlert("Import", "The file contains no entries.", "OK"); return; }

            var existing = _settingsService.GetFriendsList();
            int added = 0, updated = 0, skipped = 0;
            var seenFriends = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in imported)
            {
                if (entry.IsGroup)
                {
                    var name = (entry.DisplayName ?? string.Empty).Trim();
                    if (string.IsNullOrEmpty(name)) { skipped++; continue; }
                    entry.DisplayName = name;
                    entry.Username = string.Empty;
                    if (!seenGroups.Add(name)) { skipped++; continue; }
                    var match = existing.FirstOrDefault(f => f.IsGroup && f.DisplayName.Equals(name, StringComparison.OrdinalIgnoreCase));
                    if (match != null) { entry.Id = match.Id; existing[existing.IndexOf(match)] = entry; updated++; }
                    else { if (string.IsNullOrEmpty(entry.Id)) entry.Id = Guid.NewGuid().ToString(); existing.Add(entry); added++; }
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(entry.Username) || entry.Username.Trim().TrimStart('@').Length < 2) { skipped++; continue; }
                    entry.Username = entry.Username.Trim().TrimStart('@');
                    entry.DisplayName = entry.DisplayName?.Trim() ?? string.Empty;
                    if (!seenFriends.Add(entry.Username)) { skipped++; continue; }
                    var match = existing.FirstOrDefault(f => !f.IsGroup && f.Username.Equals(entry.Username, StringComparison.OrdinalIgnoreCase));
                    if (match != null) { entry.Id = match.Id; existing[existing.IndexOf(match)] = entry; updated++; }
                    else { if (string.IsNullOrEmpty(entry.Id)) entry.Id = Guid.NewGuid().ToString(); existing.Add(entry); added++; }
                }
            }
            _settingsService.SaveFriendsList(existing);
            LoadLists();
            await DisplayAlert("Import Complete", $"Import complete.\n\nAdded: {added}\nUpdated: {updated}\nSkipped: {skipped}", "OK");
        }
        catch (Exception ex) { await DisplayAlert("Import Failed", $"Unexpected error: {ex.Message}", "OK"); }
    }

    // ─── Login banner ────────────────────────────────────────────────────────

    private async Task RefreshLoginBannerAsync()
    {
        bool runReady = await SessionRefreshHelper.RefreshAndGetRunReadyAsync(_sessionService);
        LoginRequiredBanner.IsVisible = !runReady;
        UpdateBatteryRestrictionsBanner();
    }

    /// <summary>
    /// Show a non-blocking warning banner when background automation is enabled but
    /// the OS is still allowed to apply battery optimizations to the app. In that
    /// state Android can defer or skip the streak alarm entirely, which is exactly
    /// what would make the function "never get executed". The banner disappears
    /// automatically once the user grants the exemption from the settings sheet.
    /// </summary>
    private void UpdateBatteryRestrictionsBanner()
    {
#if ANDROID
        var context = Platform.CurrentActivity ?? Android.App.Application.Context;
        bool scheduled = _settingsService.IsScheduled();
        bool ignoring = TiktokStreakSaver.Platforms.Android.StreakScheduler
            .IsIgnoringBatteryOptimizations(context);
        BatteryRestrictionsBanner.IsVisible = scheduled && !ignoring;
#else
        BatteryRestrictionsBanner.IsVisible = false;
#endif
    }

    private async void OnLoginRequiredClicked(object? sender, EventArgs e)
    {
        await Navigation.PushAsync(new LoginPage());
        await RefreshLoginBannerAsync();
    }

    private void OnBatteryRestrictionsClicked(object? sender, EventArgs e)
    {
#if ANDROID
        var context = Platform.CurrentActivity ?? Android.App.Application.Context;
        TiktokStreakSaver.Platforms.Android.StreakScheduler
            .RequestBatteryOptimizationExemption(context);
#endif
    }

    private async void OnExportFriendsClicked(object? sender, EventArgs e)
    {
        try
        {
            var entries = _settingsService.GetFriendsList();
            if (entries.Count == 0) { await DisplayAlert("Export", "Your list is empty.", "OK"); return; }
            var options = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
            var json = System.Text.Json.JsonSerializer.Serialize(entries, options);
            var fileName = $"streak_friends_{DateTime.Now:yyyyMMdd_HHmm}.json";
            var filePath = System.IO.Path.Combine(FileSystem.CacheDirectory, fileName);
            await System.IO.File.WriteAllTextAsync(filePath, json);
            await Share.Default.RequestAsync(new ShareFileRequest { Title = "Export Friend List", File = new ShareFile(filePath, "application/json") });
        }
        catch (Exception ex) { await DisplayAlert("Export Failed", $"Could not export: {ex.Message}", "OK"); }
    }
}
