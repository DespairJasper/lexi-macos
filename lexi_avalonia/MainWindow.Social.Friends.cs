using System.Security.Cryptography;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Lexi.Social;

namespace Lexi;

public partial class MainWindow
{
    // Rendered from the last cloud-confirmed reply only. Anything else is treated as stale.
    private IReadOnlyList<SocialFriendView>? _socialFriends;
    private SocialInviteBook? _socialInvites;
    private SocialProfileView? _socialProfile;
    private Bitmap? _socialAvatar;
    private string? _socialAvatarPath;
    private StackPanel? _socialFriendHost;
    private StackPanel? _socialInviteHost;
    private StackPanel? _socialProfileHost;
    private StackPanel? _socialDetailHost;
    private TextBlock? _socialInviteFeedback;
    private Border? _socialGeneratedInviteCard;
    // Free-text fields are created once and reused, so a background refresh can never replace
    // a field the user is currently typing in.
    private TextBox? _socialNicknameBox;
    // The last value the server confirmed. While the field still shows exactly this, it is
    // pristine and may follow a refresh; once it differs the user has typed and it is left alone.
    private string? _socialNicknameConfirmed;
    private TextBox? _socialInviteCodeBox;
    private TextBox? _socialGeneratedInviteBox;
    private Guid? _pendingEndRelationship;

    /// <summary>Drop every cached friend, invite, detail and avatar. A later reply from an
    /// earlier generation must never repopulate this.</summary>
    private void SocialClearPrivateView()
    {
        _socialFriends = null; _socialInvites = null; _socialProfile = null;
        _pendingEndRelationship = null; _socialNicknameConfirmed = null;
        _socialAvatar?.Dispose(); _socialAvatar = null; _socialAvatarPath = null;
        if (_socialGeneratedInviteBox is not null) _socialGeneratedInviteBox.Text = "";
        if (_socialInviteCodeBox is not null) _socialInviteCodeBox.Text = "";
        if (_socialInviteFeedback is not null) SocialSetInviteFeedback("");
        _socialGeneratedInviteCard = null;
        _socialFriendHost?.Children.Clear(); _socialInviteHost?.Children.Clear();
        _socialProfileHost?.Children.Clear(); _socialDetailHost?.Children.Clear();
    }

    /// <summary>Revoking or failing to confirm a sharing direction must stop secondary upload
    /// before the next tick, so a cached aggregate is never trusted across a failure.</summary>
    private void SocialSuspendSecondarySharing(Guid actor)
    {
        try { _socialSync?.SetSecondarySharing(actor, false, false); }
        catch { /* A foreign owner stays blocked; the explicit profile action is rendered instead. */ }
    }

    private void RenderSocialWorkspace(Guid actor)
    {
        if (_socialBody is null) return;
        _socialFriendHost = new StackPanel { Spacing = 12 };
        _socialInviteHost = new StackPanel { Spacing = 12 };
        _socialProfileHost = new StackPanel { Spacing = 12 };
        _socialDetailHost = new StackPanel { Spacing = 12 };

        _socialBody.Children.Add(SocialSectionCard("我的资料", "管理你的昵称和头像。", _socialProfileHost));
        _socialBody.Children.Add(SocialSectionCard("邀请", "创建邀请码，或输入朋友发给你的邀请码。", _socialInviteHost));
        _socialBody.Children.Add(SocialSectionCard("好友", "查看共同学习进度，并分别管理分享权限。", _socialFriendHost));
        _socialBody.Children.Add(_socialDetailHost);

        RenderSocialProfilePanel(actor);
        RenderSocialInvitePanel(actor);
    }

    private Border SocialSectionCard(string title, string description, Control content)
    {
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(SocialText(title, 20));
        panel.Children.Add(SocialText(description, 13));
        panel.Children.Add(content);
        return new Border { Classes = { "card" }, Child = panel };
    }

    private static void DetachSocialControl(Control? control)
    {
        if (control?.Parent is Panel parent) parent.Children.Remove(control);
    }

    // ---------------- profile: nickname and avatar ----------------

    private void RenderSocialProfilePanel(Guid actor)
    {
        if (_socialProfileHost is null) return;
        _socialProfileHost.Children.Clear();
        _socialNicknameBox ??= SocialInput("SocialNickname", "昵称（1–32 字）", 32);
        // A field the user has edited must survive any background refresh; only a pristine field
        // follows the server value. Focus alone is not enough: a refresh can land while the
        // window is inactive and the field is not focused but the text is half-typed.
        var server = _socialProfile?.Nickname ?? _socialClient?.Nickname ?? "";
        if (_socialNicknameConfirmed is null || _socialNicknameBox.Text == _socialNicknameConfirmed)
        {
            _socialNicknameBox.Text = server;
            _socialNicknameConfirmed = server;
        }
        var saveNickname = SocialButton("保存昵称", "SocialSaveNickname", async () =>
        {
            var generation = _socialGeneration;
            var saved = await _socialSync!.UpdateProfileAsync(actor, _socialNicknameBox.Text ?? "", _socialCancellation.Token);
            if (generation != _socialGeneration || _socialClient?.UserId != actor) return;
            // The cloud readback is what gets shown, not an assumed local value.
            _socialProfile = saved;
            _socialNicknameBox.Text = saved.Nickname;
            _socialNicknameConfirmed = saved.Nickname;
            SocialSetMessage(TF($"昵称已保存：{saved.Nickname}"));
            RenderSocialProfilePanel(actor);
        });

        Control avatar = _socialAvatar is null
            ? new TextBlock { Text = string.IsNullOrWhiteSpace(server) ? "?" : server[..1], FontSize = 22, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center }
            : new Image { Source = _socialAvatar, Stretch = Avalonia.Media.Stretch.UniformToFill };
        var avatarFrame = new Border
        {
            Width = 64, Height = 64, CornerRadius = new CornerRadius(32), ClipToBounds = true,
            Child = avatar, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top
        };
        avatarFrame.Bind(Border.BackgroundProperty, new DynamicResourceExtension("TintBrush"));
        var details = new StackPanel { Spacing = 8, Margin = new Thickness(16, 0, 0, 0) };
        DetachSocialControl(_socialNicknameBox);
        details.Children.Add(_socialNicknameBox);
        var actions = new WrapPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, ItemSpacing = 8 };
        actions.Children.Add(saveNickname);
        actions.Children.Add(SocialButton("更换头像", "SocialPickAvatar", () => ChooseAvatarAsync(actor)));
        details.Children.Add(actions);
        details.Children.Add(SocialText("头像：私有存储，仅本人与已接受好友可读。自动裁剪为 256×256 的 WebP，不超过 200 KB。", 12));
        var row = new Grid { HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
        row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        row.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
        row.Children.Add(avatarFrame);
        Grid.SetColumn(details, 1);
        row.Children.Add(details);
        _socialProfileHost.Children.Add(row);
    }

    /// <summary>Read the chosen file once, transcode it, then upload the new object before
    /// publishing it. The user's original file is only ever read.</summary>
    private async Task ChooseAvatarAsync(Guid actor)
    {
        if (App.IsAutomatedTestRun())
        { SocialSetMessage("自动测试模式：头像选择已禁用。"); return; }
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = T("选择头像图片"), AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(T("图片")) { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.webp", "*.heic", "*.bmp", "*.gif"] }]
        });
        if (files.Count == 0) return;
        var generation = _socialGeneration;
        byte[] raw;
        await using (var input = await files[0].OpenReadAsync())
        {
            using var buffer = new MemoryStream();
            var chunk = new byte[8192]; int read;
            while ((read = await input.ReadAsync(chunk)) > 0)
            {
                if (buffer.Length + read > AvatarCodec.MaximumInputBytes)
                { SocialSetMessage("图片超过 5 MB，请换一张。"); return; }
                buffer.Write(chunk, 0, read);
            }
            raw = buffer.ToArray();
        }
        if (generation != _socialGeneration || _socialClient?.UserId != actor) return;
        if (!AvatarCodec.TryCreate(raw, out var webp) || webp is null)
        { SocialSetMessage("无法处理这张图片，请换一张常见的图片格式。"); return; }
        await PublishAvatarAsync(actor, AvatarCodec.FileName(DateTimeOffset.UtcNow, Guid.NewGuid()), webp, generation);
    }

    /// <summary>Upload, publish, and only then retire the object it replaced. A failure at any
    /// earlier step removes just the object this attempt created.</summary>
    private async Task PublishAvatarAsync(Guid actor, string name, byte[] webp, long generation)
    {
        var previous = _socialProfile?.AvatarPath;
        var path = AvatarCodec.Path(actor, name);
        var uploaded = false;
        Bitmap? preview = null;
        try
        {
            using (var imageStream = new MemoryStream(webp)) preview = new Bitmap(imageStream);
            await _socialClient!.UploadAvatarAsync(actor, name, webp, _socialCancellation.Token);
            uploaded = true;
            if (generation != _socialGeneration || _socialClient.UserId != actor) { await TryDeleteAvatarAsync(actor, path); return; }
            await _socialSync!.PublishAvatarAsync(actor, path, _socialCancellation.Token);
            // Publish succeeded, so the new object is now the live one and must not be cleaned up.
            uploaded = false;
            if (generation != _socialGeneration || _socialClient.UserId != actor) return;
            if (previous is not null && previous != path) await TryDeleteAvatarAsync(actor, previous);
            if (generation != _socialGeneration || _socialClient.UserId != actor) return;
            _socialAvatar?.Dispose(); _socialAvatar = preview; preview = null; _socialAvatarPath = path;
            _socialProfile = _socialProfile is null ? null : _socialProfile with { AvatarPath = path };
            SocialSetMessage("头像已更新。");
            RenderSocialProfilePanel(actor);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // Only the artifact this attempt created is removed; the previous avatar still stands.
            if (uploaded) await TryDeleteAvatarAsync(actor, path);
            if (generation == _socialGeneration)
                SocialSetMessage("头像暂未更新，原有头像和本机图片都未受影响。");
        }
        finally { preview?.Dispose(); }
    }

    /// <summary>Cleanup is best-effort by design: a leftover object is the server's to collect,
    /// and a failed cleanup must never surface as a failed user action.</summary>
    private async Task TryDeleteAvatarAsync(Guid actor, string path)
    {
        try { await _socialClient!.DeleteAvatarAsync(actor, path.Split('/').Last(), _socialCancellation.Token); }
        catch (Exception error) when (error is not OperationCanceledException) { }
    }

    private async Task LoadOwnAvatarAsync(Guid actor, string path, long generation)
    {
        try
        {
            var bytes = await _socialClient!.ReadAvatarAsync(actor, path, _socialCancellation.Token);
            if (generation != _socialGeneration || _socialClient.UserId != actor) return;
            using var stream = new MemoryStream(bytes);
            var bitmap = new Bitmap(stream);
            if (generation != _socialGeneration || _socialClient.UserId != actor) { bitmap.Dispose(); return; }
            _socialAvatar?.Dispose(); _socialAvatar = bitmap; _socialAvatarPath = path;
            if (_socialProfileHost is not null && _socialClient.UserId == actor) RenderSocialProfilePanel(actor);
        }
        catch (Exception error) when (error is not OperationCanceledException) { }
    }

    // ---------------- invitations ----------------

    private void RenderSocialInvitePanel(Guid actor)
    {
        if (_socialInviteHost is null) return;
        _socialInviteHost.Children.Clear();
        _socialInviteCodeBox ??= SocialInput("SocialInviteCode", "邀请码（64 位十六进制）", 64);
        _socialGeneratedInviteBox ??= SocialInput("SocialGeneratedInvite", "生成后在此显示，只会保存摘要", 64);
        DetachSocialControl(_socialInviteCodeBox);
        DetachSocialControl(_socialGeneratedInviteBox);
        DetachSocialControl(_socialInviteFeedback);
        _socialGeneratedInviteBox.IsReadOnly = true;
        _socialGeneratedInviteBox.FontSize = 16;
        _socialGeneratedInviteBox.FontFamily = new FontFamily("Menlo");
        _socialGeneratedInviteBox.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
        _socialInviteFeedback ??= SocialText("");
        _socialInviteFeedback.IsVisible = !string.IsNullOrWhiteSpace(_socialInviteFeedback.Text);
        _socialInviteHost.Children.Add(SocialText("邀请只有双人，没有群组。邀请码 24 小时有效，明文只在本机显示一次。"));
        _socialInviteHost.Children.Add(SocialButton("生成邀请码", "SocialCreateInvite", async () =>
        {
            // Entropy is created locally; only its hash ever leaves this device.
            var fresh = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            var generation = _socialGeneration;
            _socialGeneratedInviteBox.Text = "";
            if (_socialGeneratedInviteCard is not null) _socialGeneratedInviteCard.IsVisible = false;
            SocialSetInviteFeedback("正在生成邀请码…");
            SocialSetMessage("");
            SocialInviteCreated created;
            try { created = await _socialSync!.CreateInviteAsync(actor, fresh, _socialCancellation.Token); }
            catch (OperationCanceledException) { throw; }
            catch { SocialSetInviteFeedback("邀请码暂未生成，请检查网络后重试。"); return; }
            if (generation != _socialGeneration || _socialClient?.UserId != actor) return;
            _socialGeneratedInviteBox.Text = fresh;
            if (_socialGeneratedInviteCard is not null) _socialGeneratedInviteCard.IsVisible = true;
            SocialSetInviteFeedback(TF($"邀请码已生成 · 有效期至 {created.ExpiresAt.ToOffset(SocialDayRules.BeijingOffset):MM-dd HH:mm}"));
            await RefreshSocialInvitesAsync(actor, generation);
        }));
        var result = new StackPanel { Spacing = 8 };
        result.Children.Add(SocialText("新邀请码", 16));
        result.Children.Add(_socialGeneratedInviteBox);
        result.Children.Add(SocialButton("复制邀请码", "SocialCopyInvite", async () =>
        {
            // Clipboard access only ever follows this explicit button.
            if (string.IsNullOrEmpty(_socialGeneratedInviteBox.Text))
            { SocialSetInviteFeedback("请先生成邀请码。"); return; }
            var clipboard = GetTopLevel(this)?.Clipboard;
            if (clipboard is null) { SocialSetInviteFeedback("无法访问剪贴板。"); return; }
            try
            {
                await clipboard.SetTextAsync(_socialGeneratedInviteBox.Text);
                SocialSetInviteFeedback("邀请码已复制，可以发给朋友了。");
            }
            catch { SocialSetInviteFeedback("复制失败，请手动选择并复制邀请码。"); }
        }));
        _socialGeneratedInviteCard = new Border { Classes = { "card" }, Child = result,
            IsVisible = !string.IsNullOrWhiteSpace(_socialGeneratedInviteBox.Text) };
        _socialInviteHost.Children.Add(_socialGeneratedInviteCard);
        _socialInviteHost.Children.Add(_socialInviteFeedback);
        var claimRow = new StackPanel { Spacing = 8 };
        claimRow.Children.Add(_socialInviteCodeBox);
        claimRow.Children.Add(SocialButton("兑换邀请码", "SocialClaimInvite", () => ClaimAsync(actor)));
        _socialInviteHost.Children.Add(claimRow);

        // Both directions of the invitation book, from the confirmed reply only.
        if (_socialInvites is { } book)
        {
            var list = new StackPanel { Spacing = 4 };
            foreach (var invite in book.Incoming)
                list.Children.Add(SocialInviteRow(actor, invite, incoming: true));
            foreach (var invite in book.Outgoing)
                list.Children.Add(SocialInviteRow(actor, invite, incoming: false));
            if (list.Children.Count == 0) list.Children.Add(SocialText("暂时没有待处理的邀请。"));
            _socialInviteHost.Children.Add(SocialExpander("收到的与发出的邀请", new Border { Classes = { "card" }, Child = list }));
        }
    }

    private void SocialSetInviteFeedback(string text)
    {
        if (_socialInviteFeedback is null) return;
        SocialLocalize(_socialInviteFeedback, text);
        _socialInviteFeedback.IsVisible = !string.IsNullOrWhiteSpace(text);
    }

    private Control SocialInviteRow(Guid actor, SocialInviteView invite, bool incoming)
    {
        var panel = new StackPanel { Spacing = 4 };
        var label = invite.State switch
        {
            SocialInviteState.Pending => incoming ? "待你回应" : "等待对方接受",
            SocialInviteState.Accepted => "已接受",
            SocialInviteState.Declined => "已拒绝",
            SocialInviteState.Cancelled => "已失效",
            SocialInviteState.Expired => "已过期",
            _ => "状态未知"
        };
        var who = invite.Nickname ?? "TA";
        var directionChinese = incoming ? "来自" : "发给";
        var directionEnglish = incoming ? "From" : "To";
        panel.Children.Add(SocialTextPair($"{directionChinese} {who} · {label}",
            $"{directionEnglish} {who} · {UiText.English(label)}", 14));
        panel.Children.Add(SocialText(TF($"创建 {invite.CreatedAt.ToOffset(SocialDayRules.BeijingOffset):MM-dd HH:mm} · 到期 {invite.ExpiresAt.ToOffset(SocialDayRules.BeijingOffset):MM-dd HH:mm}")));
        // Only an invitation that is awaiting the recipient can still be answered.
        if (incoming && invite.State == SocialInviteState.Pending)
        {
            panel.Children.Add(SocialButton("接受并分享", "SocialAcceptInvite", () => AnswerAsync(actor, invite.Id, true)));
            panel.Children.Add(SocialButton("拒绝", "SocialDeclineInvite", () => AnswerAsync(actor, invite.Id, false)));
        }
        return panel;
    }

    private async Task ClaimAsync(Guid actor)
    {
        var generation = _socialGeneration;
        var claim = await _socialSync!.ClaimInviteAsync(actor, _socialInviteCodeBox?.Text?.Trim() ?? "", _socialCancellation.Token);
        if (generation != _socialGeneration || _socialClient?.UserId != actor) return;
        // The nickname is only a preview; accepting is a separate, explicit action.
        SocialSetMessage(TF($"确认与“{claim.Nickname ?? "TA"}”成为好友？接受后默认分享新学 / 复习、曲线与每日时长，可逐好友关闭。"));
        _socialInviteHost!.Children.Add(SocialText(TF($"待确认：{claim.Nickname ?? "TA"}")));
        _socialInviteHost.Children.Add(SocialButton("接受并分享", "SocialAcceptInvite", () => AnswerAsync(actor, claim.Id, true)));
        _socialInviteHost.Children.Add(SocialButton("拒绝", "SocialDeclineInvite", () => AnswerAsync(actor, claim.Id, false)));
    }

    private async Task AnswerAsync(Guid actor, Guid invite, bool accept)
    {
        var generation = _socialGeneration;
        var answer = await _socialSync!.AnswerInviteAsync(actor, invite, accept, _socialCancellation.Token);
        if (generation != _socialGeneration || _socialClient?.UserId != actor) return;
        var status = answer.State switch
            {
                SocialInviteState.Accepted => "已成为好友。新学 / 复习、曲线与每日时长默认分享，可逐好友关闭。",
                SocialInviteState.Declined => "已拒绝这次邀请。",
                _ => "这条邀请已不再有效。"
            };
        RenderSocialEntry();
        SocialSetMessage(status);
        await SocialRefreshAsync(actor);
    }

    private async Task RefreshSocialInvitesAsync(Guid actor, long generation)
    {
        try
        {
            var book = await _socialSync!.InvitesAsync(actor, _socialCancellation.Token);
            if (generation != _socialGeneration || _socialClient?.UserId != actor) return;
            _socialInvites = book;
            RenderSocialInvitePanel(actor);
        }
        catch (OperationCanceledException) { }
        catch { if (generation == _socialGeneration) _socialInvites = null; }
    }

    // ---------------- friends ----------------

    private void RenderSocialFriends(Guid actor, IReadOnlyList<SocialFriendView> friends)
    {
        if (_socialFriendHost is null) return;
        _socialFriendHost.Children.Clear();
        if (friends.Count == 0)
        { _socialFriendHost.Children.Add(SocialText("还没有好友。生成邀请码发给朋友，或兑换对方的邀请码。")); return; }
        foreach (var friend in friends) _socialFriendHost.Children.Add(SocialFriendCard(actor, friend));
    }

    private Control SocialFriendCard(Guid actor, SocialFriendView friend)
    {
        var panel = new StackPanel { Spacing = 8 };
        var pair = friend.Pair with { FriendName = friend.Nickname };
        panel.Children.Add(SocialText(friend.Nickname[..1], 28));
        var copy = SocialCopyPolicy.FriendList(pair);
        var continuity = pair.RunId is null
            ? UiText.Language == "en" ? TF($"你和{friend.Nickname}的续命局还没开始 · 0 天等待") : copy.Continuity
            : UiText.Language == "en" ? TF($"你和{friend.Nickname}已经连续苟住了{pair.Streak}天") : copy.Continuity;
        panel.Children.Add(SocialText(continuity, 16));
        panel.Children.Add(SocialText(copy.Today));

        var detail = new StackPanel { Spacing = 6 };
        panel.Children.Add(SocialExpander("向这位好友分享自己的数据 · 默认开启，可逐好友关闭",
            new Border { Classes = { "card" }, Child = detail }));
        // Our own direction only. What the peer shares is their setting and never our switch.
        detail.Children.Add(SharingSwitch(actor, friend, "新学 / 复习", friend.ShareCounts, "counts"));
        detail.Children.Add(SharingSwitch(actor, friend, "预测曲线", friend.ShareCurve, "curve"));
        detail.Children.Add(SharingSwitch(actor, friend, "每日时长", friend.ShareTime, "time"));
        detail.Children.Add(SocialText("对方是否分享由对方独立设置，不受我的开关影响。"));

        panel.Children.Add(SocialButton("查看这局", "SocialOpenDetail", () => OpenDetailAsync(actor, friend)));
        // Only a run that has actually broken can be reopened, and the other side has to agree.
        if (friend.Pair.Lifecycle == PairLifecycle.Broken)
            panel.Children.Add(SocialButton("再开一局", "SocialRestartRun", () => RestartAsync(actor, friend)));

        if (_pendingEndRelationship == friend.RelationshipId)
        {
            panel.Children.Add(SocialText(TF($"确认解除与“{friend.Nickname}”的关系？对方将立即无法再读取你的数据。")));
            panel.Children.Add(SocialButton("确认解除", "SocialConfirmEndRelationship", () => EndRelationshipAsync(actor, friend)));
            panel.Children.Add(SocialButton("取消", "SocialCancelEndRelationship", () =>
            { _pendingEndRelationship = null; RenderSocialFriends(actor, _socialFriends ?? []); return Task.CompletedTask; }));
        }
        else
        {
            panel.Children.Add(SocialButton("解除关系", "SocialEndRelationship", () =>
            {
                // Confirmation happens inside the app, not in a modal the fixture cannot drive.
                _pendingEndRelationship = friend.RelationshipId;
                RenderSocialFriends(actor, _socialFriends ?? []);
                return Task.CompletedTask;
            }));
        }
        return new Border { Classes = { "card" }, Child = panel };
    }

    private Control SharingSwitch(Guid actor, SocialFriendView friend, string label, bool current, string which)
    {
        var box = new CheckBox { IsChecked = current, Name = "SocialShare-" + which + "-" + friend.RelationshipId.ToString("N") };
        SocialLocalize(box, label);
        box.IsCheckedChanged += async (_, _) =>
        {
            if (box.IsChecked is not { } wanted) return;
            var generation = _socialGeneration;
            var counts = which == "counts" ? wanted : friend.ShareCounts;
            var curve = which == "curve" ? wanted : friend.ShareCurve;
            var time = which == "time" ? wanted : friend.ShareTime;
            try
            {
                var confirmed = await _socialSync!.SetSharingAsync(actor, friend.RelationshipId, counts, curve, time, _socialCancellation.Token);
                if (generation != _socialGeneration || _socialClient?.UserId != actor) return;
                // The server's own reply replaces the optimistic state for every direction.
                box.IsChecked = which switch { "curve" => confirmed.Curve, "time" => confirmed.Time, _ => box.IsChecked };
                ReplaceFriend(friend.RelationshipId, friend with { ShareCounts = counts, ShareCurve = confirmed.Curve, ShareTime = confirmed.Time });
                if (_socialFriends is not null) _socialSync.ApplyOwnAggregate(actor, _socialFriends);
            }
            catch (OperationCanceledException) { if (generation == _socialGeneration) box.IsChecked = current; }
            catch
            {
                if (generation != _socialGeneration) return;
                // Uncertainty revokes the direction locally and drops the private view.
                box.IsChecked = current;
                SocialSuspendSecondarySharing(actor);
                _socialFriends = null; _socialInvites = null;
                _socialFriendHost?.Children.Clear(); _socialDetailHost?.Children.Clear();
                SocialSetMessage("分享设置暂未确认，已暂停副通道上传并清空好友缓存视图。");
            }
        };
        return box;
    }

    private void ReplaceFriend(Guid relationship, SocialFriendView replaced)
    {
        if (_socialFriends is null) return;
        _socialFriends = _socialFriends.Select(f => f.RelationshipId == relationship ? replaced : f).ToArray();
    }

    private async Task RestartAsync(Guid actor, SocialFriendView friend)
    {
        var generation = _socialGeneration;
        var answer = await _socialSync!.RestartAsync(actor, friend.RelationshipId, true, _socialCancellation.Token);
        if (generation != _socialGeneration || _socialClient?.UserId != actor) return;
        SocialSetMessage(answer.State switch
            {
                SocialRestartState.Started => "新的一局已经开始了。",
                SocialRestartState.Waiting => "已发出再开一局的请求，等对方接受后才会开始。",
                SocialRestartState.Declined => "对方拒绝了这一次重开。",
                _ => "重开请求暂未完成。"
            });
        await SocialRefreshAsync(actor);
    }

    private async Task EndRelationshipAsync(Guid actor, SocialFriendView friend)
    {
        var generation = _socialGeneration;
        await _socialSync!.EndRelationshipAsync(actor, friend.RelationshipId, _socialCancellation.Token);
        if (generation != _socialGeneration || _socialClient?.UserId != actor) return;
        // The ended relation's cached view and its persisted joint days are dropped immediately:
        // the other side can no longer be read, so keeping its history would be stale data.
        _pendingEndRelationship = null;
        try { _socialStore?.ForgetRelationship(actor, friend.RelationshipId); }
        catch (Exception error) when (error is not OperationCanceledException) { }
        SocialStopCharts();
        _socialSeries.Clear();
        if (_socialFriends is not null) _socialFriends = _socialFriends.Where(f => f.RelationshipId != friend.RelationshipId).ToArray();
        RenderSocialFriends(actor, _socialFriends ?? []);
        _socialDetailHost?.Children.Clear();
        SocialSetMessage(TF($"已解除与“{friend.Nickname}”的关系，对方无法再读取你的数据。"));
        await SocialRefreshAsync(actor);
    }

    private async Task OpenDetailAsync(Guid actor, SocialFriendView friend)
    {
        if (_socialDetailHost is null) return;
        var generation = _socialGeneration;
        var detail = await _socialSync!.PairDetailAsync(actor, friend.RelationshipId, _socialCancellation.Token);
        if (generation != _socialGeneration || _socialClient?.UserId != actor) return;
        var host = _socialDetailHost;
        SocialStopCharts();
        host.Children.Clear();
        if (detail.PendingDays.Count > 0)
            host.Children.Add(SocialText(TF($"有 {detail.PendingDays.Count} 天待云端确认，暂不计入任何一组。")));
        // Charts and the comparison are built from the decoded reply only.
        await BuildSocialDetailAsync(actor, friend, detail, host, generation);
    }
}
