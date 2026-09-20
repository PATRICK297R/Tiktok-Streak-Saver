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
        RefreshReorderControls();
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

        // Hidden while an order is being arranged: the card is drawn from storage, and storage still holds
        // the order being replaced, so showing it then would contradict the list.
        SendOrderCard.IsVisible = _reorderDraft is null && queue.Count >= 2;
        SendOrderSubtitleLabel.Text = $"{queue.Count} entries, messaged top to bottom";

        // "Show" rather than "Reorder": arranging the order is now done in the lists themselves, and a
        // second button called Reorder that behaved differently - it saves as it goes - would be a trap.
        SendOrderToggleButton.Text = _sendOrderExpanded ? "Hide" : "Show";
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
                new ColumnDefinition { Width = new GridLength(20) },
                new ColumnDefinition { Width = new GridLength(24) },
                new ColumnDefinition { Width = GridLength.Star },
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = GridLength.Auto }
            },
            ColumnSpacing = 4
        };

        // The grab handle, and only an affordance: the drag itself is on the whole row, so the target is
        // the row rather than a 20pt glyph the user has to hit exactly.
        var handle = new Label
        {
            Text = "⠿",
            FontSize = 16,
            HorizontalTextAlignment = TextAlignment.Center,
            VerticalTextAlignment = TextAlignment.Center,
            VerticalOptions = LayoutOptions.Center
        };
        handle.SetAppThemeColor(Label.TextColorProperty,
            GetThemeColor("Gray400", "#8B8F96"), GetThemeColor("Gray400", "#8B8F96"));
        grid.Children.Add(handle);

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
        Grid.SetColumn(position, 1);
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
        Grid.SetColumn(infoStack, 2);
        grid.Children.Add(infoStack);

        grid.Children.Add(CreateSendOrderMoveButton(friend, up: true, canMove: index > 0, column: 3));
        grid.Children.Add(CreateSendOrderMoveButton(friend, up: false, canMove: index < total - 1, column: 4));

        var row = new Border
        {
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 12 },
            Padding = new Thickness(10, 6)
        };
        row.SetAppThemeColor(Border.BackgroundColorProperty,
            GetThemeColor("Gray100", "#F3F4F6"), GetThemeColor("Gray900", "#111827"));
        row.Content = grid;

        AttachSendOrderDragAndDrop(row, friend);
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
    /// Applies one arrow press and saves it. Shares the save tail with a drop, so both ways of
    /// reordering are stored the same way and a failed write is reported either way.
    /// </summary>
    private void MoveSendOrderEntry(string friendId, bool up)
    {
        var all = _settingsService.GetFriendsList();
        var moved = up ? FriendOrder.TryMoveUp(all, friendId) : FriendOrder.TryMoveDown(all, friendId);

        if (!moved)
            return;

        SaveSendOrder(all);
    }

    /// <summary>
    /// Applies a drop and saves it: the dragged row takes the place of the row it was dropped on.
    /// </summary>
    private void MoveSendOrderEntryTo(string draggedId, string targetId)
    {
        var all = _settingsService.GetFriendsList();

        if (!FriendOrder.TryMoveTo(all, draggedId, targetId))
            return;

        SaveSendOrder(all);
    }

    /// <summary>
    /// Saves a reordered list and redraws it.
    /// <para>
    /// The save result is checked rather than discarded, because a silent write failure would look
    /// exactly like the reorder not working.
    /// </para>
    /// </summary>
    private void SaveSendOrder(List<FriendConfig> all)
    {
        if (!_settingsService.TrySaveFriendsList(all, out var error))
            _ = DisplayAlert("Could not save order", error ?? "The new order was not saved.", "OK");

        // Re-reads the saved list either way, so the page shows what was actually stored.
        LoadLists();
    }

    /// <summary>
    /// Makes one row draggable onto another row.
    /// <para>
    /// The row is both the drag source and the drop target. Android starts a drag on a long press, so the
    /// row has to be pressed and held - which is why the arrows are kept as well: they are the same move
    /// without a gesture that has to be discovered, and they still work if a drag does not start on a
    /// given device.
    /// </para>
    /// </summary>
    private void AttachSendOrderDragAndDrop(Border row, FriendConfig friend)
    {
        // A run in progress owns the order, so reordering is closed while it works - the same rule the
        // arrows follow.
        var canReorder = !_lastIsRunning;

        var drag = new DragGestureRecognizer { CanDrag = canReorder };

        drag.DragStarting += (_, _) =>
        {
            _draggingFriendId = friend.Id;

            // Dimmed while it is in the air, so the list underneath reads as the place it is going.
            row.Opacity = 0.4;
        };

        // Cleared whatever happened, including a drag that ended nowhere.
        drag.DropCompleted += (_, _) =>
        {
            _draggingFriendId = null;
            row.Opacity = 1.0;
        };

        row.GestureRecognizers.Add(drag);

        var drop = new DropGestureRecognizer { AllowDrop = canReorder };

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
                MoveSendOrderEntryTo(draggedId, friend.Id);
        };

        row.GestureRecognizers.Add(drop);
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

        // The card reads the stored order, which is now the order being replaced.
        _sendOrderExpanded = false;

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
            await DisplayAlert("Could not save order", error ?? "The new order was not saved.", "OK");
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
    /// Puts the arranging state on screen: the banner, whether Save has anything to save, and the controls
    /// that would write to storage while an arrangement is staged.
    /// </summary>
    private void RefreshReorderControls()
    {
        var reordering = _reorderDraft is not null;

        ReorderBanner.IsVisible = reordering;

        // Save stays switched off until something has actually moved, so it cannot write an order identical
        // to the one already stored - and a user who taps it before dragging anything sees why.
        SaveOrderButton.IsEnabled = reordering && _reorderDraft!.HasChanges;

        // Hidden while arranging: Save and Cancel are the only two ways out, and a third button that also
        // left the mode could be taken for one of them.
        ReorderToggleButton.IsVisible = !reordering;
        ReorderToggleButton.IsEnabled = !_lastIsRunning;
        ReorderToggleButton.Opacity = _lastIsRunning ? 0.6 : 1.0;

        // A write during a staged arrangement would be saved against the order being replaced.
        var actionsEnabled = !reordering && !_lastIsRunning;
        ActionButtonsGrid.IsEnabled = actionsEnabled;
        ActionButtonsGrid.Opacity = actionsEnabled ? 1.0 : 0.6;
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
        var subtitle = friend.IsGroup ? "Group" : $"@{friend.Username}";
        if (!friend.IsEnabled)
            subtitle += " · disabled";

        infoStack.Children.Add(new Label
        {
            Text = subtitle,
            FontSize = 13,
            TextColor = GetThemeColor("Gray400", "#8B8F96")
        });

        Grid.SetColumn(infoStack, 1);
        grid.Children.Add(infoStack);

        border.Content = grid;

        AttachReorderDragAndDrop(border, friend);
        return border;
    }

    /// <summary>
    /// Makes one card draggable onto another card of the same kind. The move is staged, so this writes
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
