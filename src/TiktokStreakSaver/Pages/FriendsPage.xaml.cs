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

    /// <summary>
    /// The id of the row being dragged right now, or null.
    /// <para>
    /// Held here rather than carried in the drag's data package because the two halves of that package are
    /// different types - a drag in progress exposes a DataPackage and a completed drop exposes a
    /// DataPackageView, whose property collections do not convert to each other. Only one drag can be in
    /// flight at a time and it always starts on this page, so a single field is enough. A drag that came
    /// from somewhere else leaves this null, which reads as "not one of ours" - the right answer.
    /// </para>
    /// </summary>
    private string? _draggingFriendId;

    /// <summary>
    /// The order being arranged, or null when no arrangement is in progress.
    /// <para>
    /// While this is set the list is drawn from it rather than from storage, and dragging a card moves
    /// entries inside it. Nothing is written until Save, so leaving the mode by Cancel leaves the stored
    /// order exactly as it was.
    /// </para>
    /// </summary>
    private ReorderDraft? _reorderDraft;

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
            ActionButtonsRow.IsEnabled = !isRunning;
            ActionButtonsRow.Opacity = isRunning ? 0.6 : 1.0;

            GroupBulkRow.IsEnabled = !isRunning;
            GroupBulkRow.Opacity = isRunning ? 0.6 : 1.0;
            GroupActionButtonsGrid.IsEnabled = !isRunning;
            GroupActionButtonsGrid.Opacity = isRunning ? 0.6 : 1.0;

            if (AddFriendPanel.IsVisible && isRunning) AddFriendPanel.IsVisible = false;
            if (AddGroupPanel.IsVisible && isRunning) AddGroupPanel.IsVisible = false;

            // Applied on top of the busy state above, because an arrangement in progress closes the same
            // controls for its own reason.
            RefreshReorderControls();
        }
    }

    private void OnRefreshing(object? sender, EventArgs e)
    {
        LoadLists();
        MainRefreshView.IsRefreshing = false;
    }

    private void LoadLists()
    {
        // While an order is being arranged the list is drawn from the draft rather than from storage, which
        // is what lets the arrangement be seen without being saved.
        var reordering = _reorderDraft is not null;

        var allItems = reordering
            ? _reorderDraft!.Entries.ToList()
            : _settingsService.GetFriendsList();

        var friendsAll = allItems.Where(f => !f.IsGroup).ToList();
        var groupsAll = allItems.Where(f => f.IsGroup).ToList();

        // ── Friends ──
        // Searching is off while arranging: dropping a card on a row that is only on screen because of a
        // search would move it past entries the user cannot see.
        SearchAndBulkRow.IsVisible = !reordering && friendsAll.Count > 0;

        // Importing is off while arranging for the same reason the toolbar and search are: it would add
        // entries to the list underneath a draft that was built from the list as it was, and the new ones
        // would not be part of the arrangement the user is about to save.
        ImportChatsCard.IsVisible = !reordering;
        var friendSearch = reordering ? string.Empty : (SearchFriendEntry.Text?.Trim() ?? string.Empty);
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
            NoFriendsLabel.Text = Localization.T("Fr_NoFriendsAdd");
            NoFriendsLabel.IsVisible = true;
        }
        else if (friendsToShow.Count == 0 && !string.IsNullOrEmpty(friendSearch))
        {
            NoFriendsLabel.Text = string.Format(Localization.T("Fr_NoFriendMatch"), friendSearch);
            NoFriendsLabel.IsVisible = true;
        }
        else
        {
            NoFriendsLabel.IsVisible = false;
        }
        foreach (var friend in friendsToShow) FriendsListContainer.Children.Add(CreateEntryView(friend));

        // ── Groups ──
        GroupBulkRow.IsVisible = !reordering && groupsAll.Count > 0;
        var groupSearch = reordering ? string.Empty : (SearchGroupEntry.Text?.Trim() ?? string.Empty);
        var groupsToShow = string.IsNullOrEmpty(groupSearch)
            ? groupsAll
            : groupsAll.Where(g =>
                (g.DisplayName != null && g.DisplayName.Contains(groupSearch, StringComparison.OrdinalIgnoreCase))
            ).ToList();

        var groupItemsToRemove = GroupsListContainer.Children.Where(c => c != NoGroupsLabel).ToList();
        foreach (var item in groupItemsToRemove) GroupsListContainer.Children.Remove(item);

        if (groupsAll.Count == 0)
        {
            NoGroupsLabel.Text = Localization.T("Fr_NoGroupsAdd");
            NoGroupsLabel.IsVisible = true;
        }
        else if (groupsToShow.Count == 0 && !string.IsNullOrEmpty(groupSearch))
        {
            NoGroupsLabel.Text = string.Format(Localization.T("Fr_NoGroupMatch"), groupSearch);
            NoGroupsLabel.IsVisible = true;
        }
        else
        {
            NoGroupsLabel.IsVisible = false;
        }
        foreach (var group in groupsToShow) GroupsListContainer.Children.Add(CreateEntryView(group));

        UpdateStatsCard(allItems);

        // While arranging, the list drawn is the sequence itself rather than the two filtered sections.
        if (reordering)
            RenderReorderList(allItems);

        RefreshReorderControls();
    }

    /// <summary>
    /// Draws the one list the order is actually arranged in: both kinds together, in the order they will
    /// be messaged.
    /// </summary>
    /// <remarks>
    /// The normal view splits the saved sequence into a Friends section and a Groups section, so it cannot
    /// show where a group sits relative to a friend. This shows the sequence, which is what the runner
    /// walks and what the drag operates on.
    /// </remarks>
    private void RenderReorderList(List<FriendConfig> allItems)
    {
        var rowsToRemove = ReorderListContainer.Children.ToList();
        foreach (var row in rowsToRemove) ReorderListContainer.Children.Remove(row);

        foreach (var entry in allItems)
            ReorderListContainer.Children.Add(CreateReorderRow(entry));

        ReorderCountLabel.Text = allItems.Count == 1
            ? Localization.T("Fr_ReorderCountOne")
            : string.Format(Localization.T("Fr_ReorderCountMany"), allItems.Count);
    }

    /// <summary>
    /// Whether a row is being dragged onto a different one.
    /// <para>
    /// Used for the highlight and for the drop itself, so the row that lights up is exactly the row a
    /// drop would move.
    /// </para>
    /// </summary>
    private bool IsDraggingAnotherEntry(string rowId) =>
        !string.IsNullOrEmpty(_draggingFriendId) && _draggingFriendId != rowId;

    /// <summary>
    /// Tints the row the finger is over, so a drop has a visible target instead of having to be aimed
    /// blind. A tint rather than the accent colour itself, so the row's own text stays readable.
    /// </summary>
    private void HighlightDropTarget(Border row, bool highlighted)
    {
        row.SetAppThemeColor(Border.BackgroundColorProperty,
            highlighted ? GetThemeColor("PrimarySubtle", "#FFE4EA") : GetThemeColor("Gray100", "#F3F4F6"),
            highlighted ? GetThemeColor("PrimarySubtleDark", "#3AFE2C55") : GetThemeColor("Gray900", "#111827"));
    }

    // ─── Arranging the order in the list itself ──────────────────────────────

    /// <summary>
    /// Opens the arrangement. The stored order is copied into a draft, and from here on the list is drawn
    /// from that copy, so nothing done in the mode reaches storage until Save.
    /// </summary>
    private void OnToggleReorderModeClicked(object? sender, EventArgs e)
    {
        if (_lastIsRunning || _reorderDraft is not null)
            return;

        _reorderDraft = ReorderDraft.Begin(_settingsService.GetFriendsList());

        // Both of these write to storage, so they are closed for the duration.
        AddFriendPanel.IsVisible = false;
        AddGroupPanel.IsVisible = false;

        LoadLists();
    }

    /// <summary>
    /// Writes the arranged order.
    /// <para>
    /// The mode stays open when the write fails, so the arrangement is not lost along with the error
    /// message - the user can try Save again.
    /// </para>
    /// </summary>
    private async void OnSaveReorderClicked(object? sender, EventArgs e)
    {
        var draft = _reorderDraft;
        if (draft is null)
            return;

        if (!_settingsService.TrySaveFriendsList(draft.ToSave(), out var error))
        {
            await DisplayAlert(Localization.T("Fr_SaveOrderFailedTitle"), error ?? Localization.T("Fr_SaveOrderFailedBody"), Localization.T("Common_OK"));
            return;
        }

        _reorderDraft = null;
        LoadLists();
    }

    /// <summary>
    /// Drops the arrangement. Nothing was written while it was open, so there is nothing to undo.
    /// </summary>
    private void OnCancelReorderClicked(object? sender, EventArgs e)
    {
        if (_reorderDraft is null)
            return;

        _reorderDraft = null;
        LoadLists();
    }

    /// <summary>
    /// Shows or hides one of the Reorder buttons, and greys it out while a run is in progress.
    /// </summary>
    /// <remarks>
    /// There are two - one in each mode's toolbar - and they have to agree, so they are painted by one
    /// method rather than by two copies of the same three lines.
    /// <para>
    /// A run in progress owns the order, so reordering is not offered while it works: a move could not
    /// affect the run already in flight.
    /// </para>
    /// </remarks>
    private void SetReorderButtonState(Button button, bool reordering)
    {
        button.IsVisible = !reordering;
        button.IsEnabled = !_lastIsRunning;
        button.Opacity = _lastIsRunning ? 0.6 : 1.0;
    }

    /// <summary>
    /// Decides which of the three views is on screen: the Friends list, the Groups list, or the arranging
    /// list.
    /// </summary>
    /// <remarks>
    /// Arranging is a mode of its own rather than a state of one of the two lists, because it edits the one
    /// saved sequence that both lists are filtered views of. Taking the switcher and the toolbars off the
    /// screen while it is on leaves the two ways out - Save and Cancel - as the only things to press.
    /// </remarks>
    private void ApplyModeVisibility()
    {
        var reordering = _reorderDraft is not null;

        ModeSwitcher.IsVisible = !reordering;
        ReorderModeContainer.IsVisible = reordering;

        FriendsModeContainer.IsVisible = !reordering && !_isGroupMode;
        GroupsModeContainer.IsVisible = !reordering && _isGroupMode;
    }

    /// <summary>
    /// Puts the arranging state on screen: the banner, whether Save has anything to save, and the controls
    /// that would write to storage while an arrangement is staged.
    /// </summary>
    private void RefreshReorderControls()
    {
        var reordering = _reorderDraft is not null;

        ReorderBanner.IsVisible = reordering;
        ApplyModeVisibility();

        // Save stays switched off until something has actually moved, so it cannot write an order identical
        // to the one already stored - and a user who taps it before dragging anything sees why.
        SaveOrderButton.IsEnabled = reordering && _reorderDraft!.HasChanges;

        // Hidden while arranging: Save and Cancel are the only two ways out, and a third button that also
        // left the mode could be taken for one of them. Both toolbars carry one - friends rearrange friends,
        // groups rearrange groups - so both are painted from here and cannot disagree.
        SetReorderButtonState(ReorderToggleButton, reordering);
        SetReorderButtonState(GroupReorderToggleButton, reordering);

        // A write during a staged arrangement would be saved against the order being replaced.
        var actionsEnabled = !reordering && !_lastIsRunning;
        ActionButtonsRow.IsEnabled = actionsEnabled;
        ActionButtonsRow.Opacity = actionsEnabled ? 1.0 : 0.6;
        GroupActionButtonsGrid.IsEnabled = actionsEnabled;
        GroupActionButtonsGrid.Opacity = actionsEnabled ? 1.0 : 0.6;
    }

    /// <summary>
    /// One card while the order is being arranged: a handle and the entry, and nothing that writes.
    /// <para>
    /// The per-row buttons are left out rather than dimmed. None of them can be used in this mode, and
    /// leaving them out gives the card more room to be dragged by.
    /// </para>
    /// </summary>
    private View CreateReorderRow(FriendConfig friend)
    {
        var border = new Border
        {
            StrokeShape = new RoundRectangle { CornerRadius = 16 },
            Stroke = Colors.Transparent,
            Padding = new Thickness(14, 12)
        };
        border.SetAppThemeColor(Border.BackgroundColorProperty,
            GetThemeColor("Gray100", "#F3F4F6"),
            GetThemeColor("Gray900", "#111827"));

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitionCollection
            {
                new ColumnDefinition { Width = new GridLength(26) },
                new ColumnDefinition { Width = GridLength.Star }
            },
            ColumnSpacing = 8
        };

        var handle = new Label
        {
            Text = "⠿",
            FontSize = 18,
            HorizontalTextAlignment = TextAlignment.Center,
            VerticalTextAlignment = TextAlignment.Center,
            VerticalOptions = LayoutOptions.Center
        };
        handle.SetAppThemeColor(Label.TextColorProperty,
            GetThemeColor("Gray400", "#8B8F96"), GetThemeColor("Gray400", "#8B8F96"));
        grid.Children.Add(handle);

        var displayName = string.IsNullOrEmpty(friend.DisplayName) ? friend.Username : friend.DisplayName;
        var infoStack = new VerticalStackLayout { Spacing = 3, VerticalOptions = LayoutOptions.Center };
        infoStack.Children.Add(new Label { Text = displayName, FontSize = 15, FontFamily = "InterSemiBold" });

        // A disabled entry is listed here too, and says so: it is still part of the list and can still be
        // moved, but it will not be messaged.
        var subtitle = friend.IsGroup ? Localization.T("Common_Group") : $"@{friend.Username}";
        if (!friend.IsEnabled)
            subtitle += Localization.T("Fr_DisabledSuffix");

        infoStack.Children.Add(new Label
        {
            Text = subtitle,
            FontSize = 13,
            TextColor = GetThemeColor("Gray400", "#8B8F96")
        });

        // Health dot: red once a chat has failed three times in a row, amber after one or two, green
        // when it last sent successfully. A disabled chat is skipped - it is not being messaged, so its
        // health would read as a problem the user cannot act on.
        if (friend.IsEnabled)
            infoStack.Children.Add(BuildHealthRow(friend));

        Grid.SetColumn(infoStack, 1);
        grid.Children.Add(infoStack);

        border.Content = grid;

        AttachReorderDragAndDrop(border, friend);
        return border;
    }

    /// <summary>
    /// One small coloured dot plus a plain-language reason, so the colour is never the only signal.
    /// </summary>
    private HorizontalStackLayout BuildHealthRow(FriendConfig friend)
    {
        Color dotColor;
        string healthText;

        if (friend.ConsecutiveFailures >= 3)
        {
            dotColor = GetThemeColor("DeleteColor", "#DC2626");
            healthText = string.Format(Localization.T("Fr_HealthFailing"), friend.ConsecutiveFailures);
        }
        else if (friend.ConsecutiveFailures > 0)
        {
            dotColor = GetThemeColor("Warning", "#A87A2A");
            healthText = string.Format(Localization.T("Fr_HealthWarning"), friend.ConsecutiveFailures);
        }
        else if (friend.LastMessageSent.HasValue)
        {
            dotColor = GetThemeColor("Success", "#22946E");
            healthText = string.Format(Localization.T("Fr_HealthOk"), friend.LastMessageSent.Value.ToString("MMM dd"));
        }
        else
        {
            dotColor = GetThemeColor("Gray400", "#8B8F96");
            healthText = Localization.T("Fr_HealthNeverSent");
        }

        var row = new HorizontalStackLayout { Spacing = 5, VerticalOptions = LayoutOptions.Center };

        // An Ellipse keeps the dot a fixed size regardless of the text length next to it.
        var dot = new Microsoft.Maui.Controls.Shapes.Ellipse
        {
            WidthRequest = 7,
            HeightRequest = 7,
            Fill = new SolidColorBrush(dotColor),
            VerticalOptions = LayoutOptions.Center
        };

        row.Children.Add(dot);
        row.Children.Add(new Label
        {
            Text = healthText,
            FontSize = 11,
            TextColor = dotColor,
            VerticalOptions = LayoutOptions.Center
        });

        return row;
    }

    /// <summary>
    /// Makes one card draggable onto another card, of either kind. The move is staged, so this writes
    /// nothing: it redraws the list from the draft.
    /// </summary>
    private void AttachReorderDragAndDrop(Border row, FriendConfig friend)
    {
        var drag = new DragGestureRecognizer();

        drag.DragStarting += (_, _) =>
        {
            _draggingFriendId = friend.Id;
            row.Opacity = 0.4;
        };

        drag.DropCompleted += (_, _) =>
        {
            _draggingFriendId = null;
            row.Opacity = 1.0;
        };

        row.GestureRecognizers.Add(drag);

        var drop = new DropGestureRecognizer();

        drop.DragOver += (_, _) =>
        {
            if (IsDraggingAnotherEntry(friend.Id))
                HighlightDropTarget(row, true);
        };

        drop.DragLeave += (_, _) => HighlightDropTarget(row, false);

        drop.Drop += (_, _) =>
        {
            HighlightDropTarget(row, false);

            if (!IsDraggingAnotherEntry(friend.Id))
                return;

            // Read before the move, which rebuilds every row and so replaces the handler holding this.
            var draggedId = _draggingFriendId;
            if (!string.IsNullOrEmpty(draggedId))
                MoveStagedEntry(draggedId, friend.Id);
        };

        row.GestureRecognizers.Add(drop);
    }

    /// <summary>
    /// Applies one drop to the arrangement. Storage is not touched: the list is redrawn from the draft, and
    /// Save is what writes it.
    /// </summary>
    private void MoveStagedEntry(string draggedId, string targetId)
    {
        if (_reorderDraft is null || !_reorderDraft.Move(draggedId, targetId))
            return;

        LoadLists();
    }

    private void UpdateStatsCard(List<FriendConfig> allItems)
    {
        // Hidden while arranging: the figures describe the stored list, and the list on screen is a draft
        // that has not been saved yet.
        FriendsStatsCard.IsVisible = _reorderDraft is null && allItems.Count > 0;
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
        // While the order is being arranged a row is a card to be moved rather than an entry to be managed.
        if (_reorderDraft is not null)
            return CreateReorderRow(friend);

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
        var subtitleText = friend.IsGroup ? Localization.T("Common_Group") : $"@{friend.Username}";
        infoStack.Children.Add(new Label { Text = subtitleText, FontSize = 13, TextColor = GetThemeColor("Gray400", "#8B8F96") });

        // The health line replaces the old "Last sent" label rather than sitting next to it: it already
        // carries the last-sent date when things are healthy, and two date lines in one row is noise.
        infoStack.Children.Add(BuildHealthRow(friend));

        // Shows at a glance that this entry sends something different, so a custom message is never
        // set by accident and forgotten.
        if (friend.HasCustomMessage)
        {
            var customLabel = new Label
            {
                Text = string.Format(Localization.T("Fr_OwnMessage"), TruncateForRow(friend.CustomMessage)),
                FontSize = 12,
                LineBreakMode = LineBreakMode.TailTruncation,
                MaxLines = 1,
            };
            customLabel.SetAppThemeColor(Label.TextColorProperty,
                Color.FromArgb("#B45309"), Color.FromArgb("#F59E0B"));
            infoStack.Children.Add(customLabel);
        }

        grid.Children.Add(infoStack);

        var messageButton = new Button { Text = Localization.T("Fr_Message"), BackgroundColor = Colors.Transparent, FontSize = 12, Padding = new Thickness(8), HeightRequest = 44, VerticalOptions = LayoutOptions.Center, IsEnabled = !_lastIsRunning, Opacity = _lastIsRunning ? 0.6 : 1.0 };
        messageButton.SetAppThemeColor(Button.TextColorProperty, GetThemeColor("Gray400"), GetThemeColor("Gray400"));
        messageButton.Clicked += async (s, e) =>
        {
            var promptTitle = friend.IsGroup ? Localization.T("Fr_MessagePromptGroupTitle") : Localization.T("Fr_MessagePromptFriendTitle");
            var promptMessage = (friend.IsGroup
                    ? Localization.T("Fr_MessagePromptGroup")
                    : string.Format(Localization.T("Fr_MessagePromptFriend"), friend.Username))
                + "\n\n" + Localization.T("Fr_MessagePromptBlank") + " " + Localization.T("Fr_MessagePromptTokens");

            var entered = await DisplayPromptAsync(promptTitle, promptMessage, initialValue: friend.CustomMessage ?? string.Empty);

            // null means the prompt was cancelled, which must not wipe an existing message.
            if (entered == null) return;

            friend.CustomMessage = MessageResolver.Normalize(entered);
            _settingsService.UpdateFriend(friend);
            LoadLists();
        };
        Grid.SetColumn(messageButton, 1); grid.Children.Add(messageButton);

        var editButton = new Button { Text = Localization.T("Fr_Edit"), BackgroundColor = Colors.Transparent, FontSize = 12, Padding = new Thickness(8), HeightRequest = 44, VerticalOptions = LayoutOptions.Center, IsEnabled = !_lastIsRunning, Opacity = _lastIsRunning ? 0.6 : 1.0 };
        editButton.SetAppThemeColor(Button.TextColorProperty, GetThemeColor("Gray400"), GetThemeColor("Gray400"));
        editButton.Clicked += async (s, e) =>
        {
            var promptTitle = friend.IsGroup ? Localization.T("Fr_EditGroupTitle") : Localization.T("Fr_EditFriendTitle");
            var promptMessage = friend.IsGroup
                ? Localization.T("Fr_EditGroupPrompt")
                : Localization.T("Fr_EditFriendPrompt");
            var newName = await DisplayPromptAsync(promptTitle, promptMessage, initialValue: friend.DisplayName ?? friend.Username);
            if (newName != null) { friend.DisplayName = newName; _settingsService.UpdateFriend(friend); LoadLists(); }
        };
        Grid.SetColumn(editButton, 2); grid.Children.Add(editButton);

        var deleteButton = new Button { Text = Localization.T("Fr_Delete"), BackgroundColor = Colors.Transparent, FontSize = 12, Padding = new Thickness(8), HeightRequest = 44, VerticalOptions = LayoutOptions.Center, IsEnabled = !_lastIsRunning, Opacity = _lastIsRunning ? 0.6 : 1.0 };
        deleteButton.TextColor = GetThemeColor("DeleteColor", "#EE1D52");
        deleteButton.Clicked += async (s, e) =>
        {
            var removeTitle = friend.IsGroup ? Localization.T("Fr_RemoveGroupTitle") : Localization.T("Fr_RemoveFriendTitle");
            var confirm = await DisplayAlert(removeTitle, string.Format(Localization.T("Fr_RemoveBody"), displayName), Localization.T("Fr_RemoveConfirm"), Localization.T("Common_Cancel"));
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

        ApplyModeVisibility();
    }

    private void OnGroupsModeTapped(object? sender, TappedEventArgs e)
    {
        if (_isGroupMode) return;
        _isGroupMode = true;

        GroupsModeTabBorder.BackgroundColor = GetThemeColor("Primary", "#FE2C55");
        GroupsModeTabLabel.TextColor = GetThemeColor("White", "#FFFFFF");
        FriendsModeTabBorder.BackgroundColor = Colors.Transparent;
        FriendsModeTabLabel.TextColor = GetThemeColor("Gray600", "#5F636A");

        ApplyModeVisibility();
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
        if (string.IsNullOrEmpty(username)) { await DisplayAlert(Localization.T("Fr_ErrorTitle"), Localization.T("Fr_EnterUsername"), Localization.T("Common_OK")); return; }
        var existing = _settingsService.GetFriendsList();
        if (existing.Any(f => !f.IsGroup && f.Username.Equals(username, StringComparison.OrdinalIgnoreCase)))
        { await DisplayAlert(Localization.T("Fr_ErrorTitle"), Localization.T("Fr_FriendExists"), Localization.T("Common_OK")); return; }
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
            await DisplayAlert(Localization.T("Fr_CouldNotSaveTitle"), error ?? Localization.T("Fr_FriendSaveFailed"), Localization.T("Common_OK"));
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
        bool confirm = await DisplayAlert(Localization.T("Fr_ClearFriendsTitle"),
            Localization.T("Fr_ClearFriendsBody"), Localization.T("Fr_Clear"), Localization.T("Common_Cancel"));
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
        if (string.IsNullOrEmpty(matchName)) { await DisplayAlert(Localization.T("Fr_ErrorTitle"), Localization.T("Fr_EnterGroupName"), Localization.T("Common_OK")); return; }

        var existing = _settingsService.GetFriendsList();
        if (existing.Any(f => f.IsGroup && f.DisplayName.Equals(matchName, StringComparison.OrdinalIgnoreCase)))
        { await DisplayAlert(Localization.T("Fr_ErrorTitle"), Localization.T("Fr_GroupExists"), Localization.T("Common_OK")); return; }

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
            await DisplayAlert(Localization.T("Fr_CouldNotSaveTitle"), error ?? Localization.T("Fr_GroupSaveFailed"), Localization.T("Common_OK"));
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
        bool confirm = await DisplayAlert(Localization.T("Fr_ClearGroupsTitle"),
            Localization.T("Fr_ClearGroupsBody"), Localization.T("Fr_Clear"), Localization.T("Common_Cancel"));
        if (confirm)
        {
            _settingsService.SaveFriendsList(friendsOnly);
            LoadLists();
        }
    }

    // ─── Import / Export (friend list — both groups & friends are persisted together) ──

    /// <summary>
    /// Opens the scanner that reads the TikTok inbox, so a long contact list does not have to be
    /// typed in one entry at a time.
    /// </summary>
    private async void OnImportFromChatsClicked(object? sender, EventArgs e)
    {
        await Navigation.PushAsync(new ImportContactsPage());
    }

    private async void OnImportFriendsClicked(object? sender, EventArgs e)
    {
        try
        {
            var result = await FilePicker.Default.PickAsync(new PickOptions
            {
                PickerTitle = Localization.T("Fr_PickFileTitle"),
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
            if (string.IsNullOrWhiteSpace(json)) { await DisplayAlert(Localization.T("Fr_ImportFailedTitle"), Localization.T("Fr_ImportFileEmpty"), Localization.T("Common_OK")); return; }

            List<FriendConfig>? imported;
            try
            {
                var options = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                imported = System.Text.Json.JsonSerializer.Deserialize<List<FriendConfig>>(json, options);
            }
            catch { await DisplayAlert(Localization.T("Fr_ImportFailedTitle"), Localization.T("Fr_ImportFileInvalid"), Localization.T("Common_OK")); return; }
            if (imported == null || imported.Count == 0) { await DisplayAlert(Localization.T("Fr_Import"), Localization.T("Fr_ImportNoEntries"), Localization.T("Common_OK")); return; }

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
            await DisplayAlert(Localization.T("Fr_ImportCompleteTitle"), string.Format(Localization.T("Fr_ImportCompleteBody"), added, updated, skipped), Localization.T("Common_OK"));
        }
        catch (Exception ex) { await DisplayAlert(Localization.T("Fr_ImportFailedTitle"), string.Format(Localization.T("Fr_ImportUnexpected"), ex.Message), Localization.T("Common_OK")); }
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
            if (entries.Count == 0) { await DisplayAlert(Localization.T("Fr_Export"), Localization.T("Fr_ExportEmpty"), Localization.T("Common_OK")); return; }
            var options = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
            var json = System.Text.Json.JsonSerializer.Serialize(entries, options);
            var fileName = $"streak_friends_{DateTime.Now:yyyyMMdd_HHmm}.json";
            var filePath = System.IO.Path.Combine(FileSystem.CacheDirectory, fileName);
            await System.IO.File.WriteAllTextAsync(filePath, json);
            await Share.Default.RequestAsync(new ShareFileRequest { Title = Localization.T("Fr_ExportShareTitle"), File = new ShareFile(filePath, "application/json") });
        }
        catch (Exception ex) { await DisplayAlert(Localization.T("Fr_ExportFailedTitle"), string.Format(Localization.T("Fr_ExportFailedBody"), ex.Message), Localization.T("Common_OK")); }
    }
}
