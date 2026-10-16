namespace Lexi.Social;

/// <summary>Read and write side of the social directory. Every reply is decoded by
/// <see cref="SocialDirectory"/> before it can reach the UI, and every call is bound to
/// the authenticated account so a late reply can never be published after logout.</summary>
public sealed partial class SocialSyncCoordinator
{
    public async Task<IReadOnlyList<SocialFriendView>> FriendsAsync(Guid actor, CancellationToken cancellation = default)
    {
        var reply = await _cloud.RpcAsync(actor, "lexi_friends", new { }, cancellation);
        return SocialDirectory.Friends(reply);
    }

    public async Task<SocialInviteBook> InvitesAsync(Guid actor, CancellationToken cancellation = default)
    {
        var reply = await _cloud.RpcAsync(actor, "lexi_invites", new { }, cancellation);
        return SocialDirectory.Invites(reply);
    }

    public async Task<SocialProfileView> ProfileAsync(Guid actor, CancellationToken cancellation = default)
    {
        var reply = await _cloud.RpcAsync(actor, "lexi_profile", new { }, cancellation);
        return SocialDirectory.Profile(reply);
    }

    /// <summary>The cloud readback is returned; a nickname is never assumed applied locally.</summary>
    public async Task<SocialProfileView> UpdateProfileAsync(Guid actor, string nickname, CancellationToken cancellation = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nickname);
        var trimmed = nickname.Trim();
        if (trimmed.Length is < 1 or > 32) throw new ArgumentException("Nicknames are 1 to 32 characters.", nameof(nickname));
        var reply = await _cloud.RpcAsync(actor, "lexi_update_profile", new { p_nickname = trimmed }, cancellation);
        return SocialDirectory.Profile(reply);
    }

    /// <summary>The 256-bit code is generated locally and only ever leaves this device as a hash.</summary>
    public async Task<SocialInviteCreated> CreateInviteAsync(Guid actor, string code, CancellationToken cancellation = default)
    {
        RequireInviteCode(code);
        var reply = await _cloud.RpcAsync(actor, "lexi_create_invite", new { p_code = code }, cancellation);
        return SocialDirectory.InviteCreated(reply);
    }

    public async Task<SocialInviteClaim> ClaimInviteAsync(Guid actor, string code, CancellationToken cancellation = default)
    {
        RequireInviteCode(code);
        var reply = await _cloud.RpcAsync(actor, "lexi_claim_invite", new { p_code = code }, cancellation);
        return SocialDirectory.InviteClaim(reply);
    }

    public async Task<SocialInviteAnswer> AnswerInviteAsync(Guid actor, Guid invite, bool accept, CancellationToken cancellation = default)
    {
        RequireRelationship(invite, nameof(invite));
        var reply = await _cloud.RpcAsync(actor, "lexi_answer_invite", new { p_invite_id = invite, p_accept = accept }, cancellation);
        return SocialDirectory.InviteAnswer(reply);
    }

    /// <summary>Returns what the server confirmed. On revocation or any uncertainty the caller
    /// must drop the cached private view; the confirmed switches are the only ones it may keep.</summary>
    public async Task<SocialSecondarySharing> SetSharingAsync(Guid actor, Guid relationship,
        bool counts, bool curve, bool time, CancellationToken cancellation = default)
    {
        RequireRelationship(relationship, nameof(relationship));
        var reply = await _cloud.RpcAsync(actor, "lexi_set_sharing",
            new { p_relation_id = relationship, p_counts = counts, p_curve = curve, p_time = time }, cancellation);
        return SocialDirectory.Sharing(reply);
    }

    public async Task EndRelationshipAsync(Guid actor, Guid relationship, CancellationToken cancellation = default)
    {
        RequireRelationship(relationship, nameof(relationship));
        await _cloud.RpcAsync(actor, "lexi_end_relationship", new { p_relation_id = relationship }, cancellation);
    }

    public async Task<SocialPairDetail> PairDetailAsync(Guid actor, Guid relationship, CancellationToken cancellation = default)
    {
        RequireRelationship(relationship, nameof(relationship));
        var reply = await _cloud.RpcAsync(actor, "lexi_pair_detail", new { p_relation_id = relationship }, cancellation);
        return SocialDirectory.PairDetail(reply);
    }

    /// <summary>Ask to reopen a broken run. The reply says whether the other side still has to agree.</summary>
    public async Task<SocialRestartAnswer> RestartAsync(Guid actor, Guid relationship, bool accept, CancellationToken cancellation = default)
    {
        RequireRelationship(relationship, nameof(relationship));
        var reply = await _cloud.RpcAsync(actor, "lexi_restart", new { p_relation_id = relationship, p_accept = accept }, cancellation);
        return SocialDirectory.Restart(reply);
    }

    /// <summary>A published avatar is only recorded once Storage already holds the object.</summary>
    public async Task PublishAvatarAsync(Guid actor, string path, CancellationToken cancellation = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        await _cloud.RpcAsync(actor, "lexi_publish_avatar", new { p_path = path }, cancellation);
    }

    /// <summary>Adopt the cloud-confirmed aggregate so secondary uploads follow the same switches
    /// the server just acknowledged, never a local guess.</summary>
    public void ApplyOwnAggregate(Guid actor, IReadOnlyList<SocialFriendView> friends)
    {
        var aggregate = SocialDirectory.OwnAggregate(friends);
        SetSecondarySharing(actor, aggregate.Time, aggregate.Curve);
    }

    private static void RequireInviteCode(string code)
    {
        if (code is null || code.Length != 64 || code.Any(c => !Uri.IsHexDigit(c)))
            throw new ArgumentException("Invite entropy must be 64 hexadecimal characters.", nameof(code));
    }

    private static void RequireRelationship(Guid relationship, string name)
    {
        if (relationship == Guid.Empty) throw new ArgumentException("A real identifier is required.", name);
    }
}
