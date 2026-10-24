using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Paddockside.Domain;

namespace Paddockside.Infrastructure.Persistence.Configurations;

// The domain uses get-only properties and private collections, so every mapped member is named here and EF
// writes through backing fields. Computed members are ignored explicitly. Enums are stored as strings, and
// every foreign key is Restrict: business records are never cascade-deleted.

internal sealed class TenantConfiguration : IEntityTypeConfiguration<Tenant>
{
    public void Configure(EntityTypeBuilder<Tenant> b)
    {
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Name).HasMaxLength(200);
        b.Property(x => x.PriorManagementPeriodsVisible);
        b.Property(x => x.InviteOwnersOnFirstInterest).HasDefaultValue(true);
        b.Property(x => x.HousePartyId);
        b.Property(x => x.Slug).HasMaxLength(30);
        b.Property(x => x.LogoUrl).HasMaxLength(500);
        b.Property(x => x.FooterDetails).HasMaxLength(500);
        // Slugs form reply addresses, so two tenants can never share one. (Empty = set before slugs existed.)
        b.HasIndex(x => x.Slug).IsUnique().HasFilter("[Slug] <> ''");
    }
}

internal sealed class PartyConfiguration : IEntityTypeConfiguration<Party>
{
    public void Configure(EntityTypeBuilder<Party> b)
    {
        b.MapTenantOwned();
        b.Property(x => x.DisplayName).HasMaxLength(200);
        b.Property(x => x.Kind).AsString();
        b.Ignore(x => x.PrimaryEmail);
        b.Ignore(x => x.FirstName);
        b.Ignore(x => x.PrimaryMobile);
        b.Property(x => x.PersonId);
        b.HasIndex(x => x.PersonId);
        b.Property(x => x.HowWeKnowThem).HasMaxLength(Party.HowWeKnowThemLength);
        b.Property(x => x.AddedAt);
        b.Property(x => x.UnsubscribedAt);
        b.Property(x => x.Title).HasMaxLength(50);
        b.Property(x => x.Company).HasMaxLength(200);
        b.Property(x => x.Address).HasMaxLength(Party.DetailLength);
        b.Property(x => x.Notes).HasMaxLength(Party.NotesLength);
        b.Property(x => x.OtherPhones).HasMaxLength(Party.DetailLength);
        b.Property(x => x.Website).HasMaxLength(300);
        b.Property(x => x.Social).HasMaxLength(Party.DetailLength);
        b.Property(x => x.SourceGroups).HasMaxLength(Party.SourceGroupsLength);

        // Contacts belong to their party (and its tenant filter); they are never queried on their own.
        b.OwnsMany(x => x.Contacts, c =>
        {
            c.ToTable("PartyContacts");
            c.WithOwner().HasForeignKey("PartyId");
            c.Property<int>("Id");
            c.HasKey("Id");
            c.Property(x => x.Kind).AsString();
            c.Property(x => x.Value).HasMaxLength(320);
            c.Property(x => x.IsPrimary);
            c.Property(x => x.UndeliverableSince);
            c.Ignore(x => x.IsDeliverable);
            c.HasIndex(x => x.Value);
        });
        b.Navigation(x => x.Contacts).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

internal sealed class MailoutConfiguration : IEntityTypeConfiguration<Mailout>
{
    public void Configure(EntityTypeBuilder<Mailout> b)
    {
        b.MapTenantOwned();
        b.Property(x => x.Subject).HasMaxLength(Mailout.SubjectLength);
        b.Property(x => x.Body).HasMaxLength(Mailout.BodyLength);
        b.Property(x => x.Audience).HasMaxLength(500);
        b.Property(x => x.SentBy).HasMaxLength(320);
        b.Property(x => x.CreatedAt);
        b.Property(x => x.Signature).HasMaxLength(StaffSignature.Length);
        b.HasOne<Horse>().WithMany().HasForeignKey(x => x.HorseId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class MailoutAttachmentConfiguration : IEntityTypeConfiguration<MailoutAttachment>
{
    public void Configure(EntityTypeBuilder<MailoutAttachment> b)
    {
        b.MapTenantOwned();
        b.Property(x => x.FileName).HasMaxLength(200);
        b.Property(x => x.ContentType).HasMaxLength(100);
        b.Property(x => x.Length);
        b.Property(x => x.BlobName).HasMaxLength(300);
        b.Property(x => x.CreatedAt);
        b.HasIndex(x => x.MailoutId);
        b.HasOne<Mailout>().WithMany().HasForeignKey(x => x.MailoutId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class StaffSignatureConfiguration : IEntityTypeConfiguration<StaffSignature>
{
    public void Configure(EntityTypeBuilder<StaffSignature> b)
    {
        b.MapTenantOwned();
        b.Property(x => x.Text).HasMaxLength(StaffSignature.Length);
        b.Property(x => x.UpdatedAt);
        b.HasIndex("TenantId", nameof(StaffSignature.PersonId)).IsUnique();
    }
}

internal sealed class MailoutRecipientConfiguration : IEntityTypeConfiguration<MailoutRecipient>
{
    public void Configure(EntityTypeBuilder<MailoutRecipient> b)
    {
        b.MapTenantOwned();
        b.Property(x => x.Token).HasMaxLength(12);
        b.HasIndex(x => x.Token).IsUnique(); // a reply or an unsubscribe finds its recipient by this alone
        b.Property(x => x.Status).AsString();
        b.Property(x => x.StatusAt);
        b.Property(x => x.Address).HasMaxLength(320);
        b.Property(x => x.ProviderMessageId).HasMaxLength(100);
        b.Property(x => x.Note).HasMaxLength(500);
        b.HasIndex("TenantId", nameof(MailoutRecipient.Status));
        b.HasIndex(x => new { x.MailoutId, x.PartyId }).IsUnique();
        b.HasOne<Mailout>().WithMany().HasForeignKey(x => x.MailoutId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Party>().WithMany().HasForeignKey(x => x.PartyId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class MailoutReplyConfiguration : IEntityTypeConfiguration<MailoutReply>
{
    public void Configure(EntityTypeBuilder<MailoutReply> b)
    {
        b.MapTenantOwned();
        b.Property(x => x.FromAddress).HasMaxLength(320);
        b.Property(x => x.Subject).HasMaxLength(1000);
        b.Property(x => x.Body);
        b.Property(x => x.ReceivedAt);
        b.HasIndex(x => x.MailoutId);
        b.HasOne<Mailout>().WithMany().HasForeignKey(x => x.MailoutId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<MailoutRecipient>().WithMany().HasForeignKey(x => x.RecipientId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Party>().WithMany().HasForeignKey(x => x.PartyId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<InboundMessage>().WithMany().HasForeignKey(x => x.InboundMessageId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ClientListConfiguration : IEntityTypeConfiguration<ClientList>
{
    public void Configure(EntityTypeBuilder<ClientList> b)
    {
        b.MapTenantOwned();
        b.Property(x => x.Name).HasMaxLength(ClientList.NameLength);
        b.Property(x => x.CreatedAt);
        b.HasIndex("TenantId", nameof(ClientList.Name)).IsUnique();

        // Members belong to their list (and its tenant filter); a client is found on lists by querying the lists.
        b.OwnsMany(x => x.Members, m =>
        {
            m.ToTable("ClientListMembers");
            m.WithOwner().HasForeignKey("ClientListId");
            m.Property(x => x.PartyId);
            m.Property(x => x.AddedAt);
            m.HasKey("ClientListId", nameof(ClientListMember.PartyId));
            m.HasIndex(x => x.PartyId);
            m.HasOne<Party>().WithMany().HasForeignKey(x => x.PartyId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Navigation(x => x.Members).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

internal sealed class HoldingEntityConfiguration : IEntityTypeConfiguration<HoldingEntity>
{
    public void Configure(EntityTypeBuilder<HoldingEntity> b)
    {
        b.MapTenantOwned();
        b.Property(x => x.Name).HasMaxLength(200);
        b.Property(x => x.Type).AsString();
        b.Ignore(x => x.ExpandsInternally);
    }
}

internal sealed class HorseConfiguration : IEntityTypeConfiguration<Horse>
{
    public void Configure(EntityTypeBuilder<Horse> b)
    {
        b.MapTenantOwned();
        b.Ignore(x => x.Name);
        b.Ignore(x => x.CurrentManagementPeriod);
        b.Property(x => x.Sex).HasConversion<string>().HasMaxLength(32);
        b.Property(x => x.FoaledOn);
        b.Property(x => x.Sire).HasMaxLength(200);
        b.Property(x => x.Dam).HasMaxLength(200);

        b.OwnsMany(x => x.Names, n =>
        {
            n.ToTable("HorseNames");
            n.WithOwner().HasForeignKey("HorseId");
            n.Property<int>("Id");
            n.HasKey("Id");
            n.Property(x => x.Name).HasMaxLength(200);
            n.Property(x => x.Kind).AsString();
            n.Property(x => x.ValidFrom);
            n.Property(x => x.ValidTo);
            n.Property(x => x.Source).HasMaxLength(200);
            n.Ignore(x => x.IsCurrent);
            n.HasIndex(x => x.Name);
        });
        b.Navigation(x => x.Names).UsePropertyAccessMode(PropertyAccessMode.Field);

        b.HasMany(x => x.ManagementPeriods).WithOne().HasForeignKey(x => x.HorseId).OnDelete(DeleteBehavior.Restrict);
        b.Navigation(x => x.ManagementPeriods).HasField("_periods").UsePropertyAccessMode(PropertyAccessMode.Field);

        b.HasMany(x => x.Interests).WithOne().HasForeignKey(x => x.HorseId).OnDelete(DeleteBehavior.Restrict);
        b.Navigation(x => x.Interests).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

internal sealed class ManagementPeriodConfiguration : IEntityTypeConfiguration<ManagementPeriod>
{
    public void Configure(EntityTypeBuilder<ManagementPeriod> b)
    {
        b.MapTenantOwned();
        b.Property(x => x.HorseId);
        b.Property(x => x.From);
        b.Property(x => x.To);
        b.Property(x => x.EndReason).HasMaxLength(500);
        b.Ignore(x => x.IsOpen);
    }
}

internal sealed class ManagedInterestConfiguration : IEntityTypeConfiguration<ManagedInterest>
{
    public void Configure(EntityTypeBuilder<ManagedInterest> b)
    {
        b.MapTenantOwned();
        b.Property(x => x.HorseId);
        b.Property(x => x.Kind).AsString();
        b.Property(x => x.Units);
        b.Property(x => x.EffectiveFrom);
        b.Property(x => x.EffectiveTo);
        b.Property(x => x.RegisteredFrom);
        b.Property(x => x.RegisteredTo);
        b.Property(x => x.State).AsString();
        b.Property(x => x.AccessPolicy).AsString();
        b.Ignore(x => x.IsActive);

        b.HasOne<ManagementPeriod>().WithMany().HasForeignKey(x => x.ManagementPeriodId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Party>().WithMany().HasForeignKey(x => x.PartyId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<HoldingEntity>().WithMany().HasForeignKey(x => x.HoldingEntityId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ManagedInterest>().WithMany().HasForeignKey(x => x.DerivedFromId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class EventConfiguration : IEntityTypeConfiguration<Event>
{
    public void Configure(EntityTypeBuilder<Event> b)
    {
        b.MapTenantOwned();
        b.Property(x => x.EventType).HasMaxLength(100);
        b.Property(x => x.Title).HasMaxLength(300);
        b.Property(x => x.Status).AsString();
        b.Property(x => x.KeyDate);
        b.Property(x => x.OpenedAt);
        b.Property(x => x.ClosedAt);
        b.Property(x => x.Gate).HasMaxLength(Event.RaceDayDetailLength);
        b.Property(x => x.Dining).HasMaxLength(Event.RaceDayDetailLength);
        b.Property(x => x.RaceDayNote).HasMaxLength(Event.RaceDayDetailLength);

        b.HasOne<Horse>().WithMany().HasForeignKey(x => x.HorseId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Party>().WithMany().HasForeignKey(x => x.ClosedByPartyId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class StreamItemConfiguration : IEntityTypeConfiguration<StreamItem>
{
    public void Configure(EntityTypeBuilder<StreamItem> b)
    {
        b.MapTenantOwned();
        b.Property(x => x.Kind).AsString();
        b.Property(x => x.Scope).AsString();
        b.Property(x => x.Direction).AsString();
        b.Property(x => x.OccurredAt);
        b.Property(x => x.RecordedAt);
        b.Property(x => x.Body);
        b.Property(x => x.StepCode).HasMaxLength(100);
        b.Property(x => x.NamedPartyIds)
            .HasConversion(
                ids => JsonSerializer.Serialize(ids, (JsonSerializerOptions?)null),
                json => JsonSerializer.Deserialize<HashSet<Guid>>(json, (JsonSerializerOptions?)null) ?? new HashSet<Guid>(),
                new ValueComparer<IReadOnlySet<Guid>>(
                    (a, c) => a!.SetEquals(c!),
                    ids => ids.Aggregate(0, (hash, id) => hash ^ id.GetHashCode()),
                    ids => ids.ToHashSet()));

        b.Property(x => x.Title).HasMaxLength(300);
        b.Property(x => x.AuthorName).HasMaxLength(200);
        b.Property(x => x.Source).HasMaxLength(100);
        b.Property(x => x.CorrectionNote).HasMaxLength(500);
        b.Property(x => x.Channel).HasMaxLength(32);
        b.Property(x => x.Fields)
            .HasConversion(
                fields => JsonSerializer.Serialize(fields, (JsonSerializerOptions?)null),
                json => JsonSerializer.Deserialize<List<FactField>>(json, (JsonSerializerOptions?)null) ?? new List<FactField>(),
                new ValueComparer<IReadOnlyList<FactField>>(
                    (a, c) => a!.SequenceEqual(c!),
                    fields => fields.Aggregate(0, (hash, f) => HashCode.Combine(hash, f)),
                    fields => fields.ToList()));

        b.Property(x => x.Prompt)
            .HasConversion(
                prompt => prompt == null ? null : JsonSerializer.Serialize(prompt, (JsonSerializerOptions?)null),
                json => json == null ? null : JsonSerializer.Deserialize<MessagePrompt>(json, (JsonSerializerOptions?)null))
            .HasMaxLength(2000);

        b.HasOne<Horse>().WithMany().HasForeignKey(x => x.HorseId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Event>().WithMany().HasForeignKey(x => x.EventId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Party>().WithMany().HasForeignKey(x => x.AuthorPartyId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<StreamItem>().WithMany().HasForeignKey(x => x.SupersedesId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<StreamItem>().WithMany().HasForeignKey(x => x.InReplyToId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.TenantId, x.HorseId, x.OccurredAt });
        b.HasIndex(x => new { x.TenantId, x.EventId, x.OccurredAt });
    }
}

internal sealed class DeliveryConfiguration : IEntityTypeConfiguration<Delivery>
{
    public void Configure(EntityTypeBuilder<Delivery> b)
    {
        b.MapTenantOwned();
        b.Property(x => x.Channel).AsString();
        b.Property(x => x.Status).AsString();
        b.Property(x => x.StatusAt);
        b.Property(x => x.Address).HasMaxLength(320);
        b.Property(x => x.ProviderMessageId).HasMaxLength(100);
        b.Property(x => x.SentAt);
        b.Property(x => x.Attempts);
        b.Property(x => x.Note).HasMaxLength(500);
        b.Property(x => x.Direct);
        b.Ignore(x => x.IsWaitingToSend);
        b.HasOne<StreamItem>().WithMany().HasForeignKey(x => x.StreamItemId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Party>().WithMany().HasForeignKey(x => x.PartyId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<RoutingAddress>().WithMany().HasForeignKey(x => x.RoutingAddressId).OnDelete(DeleteBehavior.Restrict);
        // Never twice to the same recipient for the same message on the same channel (messaging-channels.md §2.5).
        b.HasIndex(x => new { x.StreamItemId, x.PartyId, x.Channel }).IsUnique();
        b.HasIndex(x => new { x.TenantId, x.Status, x.Channel });
        b.HasIndex(x => x.ProviderMessageId);
    }
}

internal sealed class MediaAssetConfiguration : IEntityTypeConfiguration<MediaAsset>
{
    public void Configure(EntityTypeBuilder<MediaAsset> b)
    {
        b.MapTenantOwned();
        b.Property(x => x.Kind).AsString();
        b.Property(x => x.FileName).HasMaxLength(200);
        b.Property(x => x.ContentType).HasMaxLength(100);
        b.Property(x => x.BlobName).HasMaxLength(300);
        b.Property(x => x.Length);
        b.Property(x => x.CreatedAt);
        b.HasIndex(x => new { x.TenantId, x.StreamItemId });
        b.HasIndex(x => new { x.TenantId, x.HorseId, x.CreatedAt });
        b.HasIndex(x => new { x.TenantId, x.SharedFromId });

        b.HasOne<StreamItem>().WithMany().HasForeignKey(x => x.StreamItemId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Horse>().WithMany().HasForeignKey(x => x.HorseId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class TicketAllocationConfiguration : IEntityTypeConfiguration<TicketAllocation>
{
    public void Configure(EntityTypeBuilder<TicketAllocation> b)
    {
        b.MapTenantOwned();
        b.Property(x => x.Available);
        b.Property(x => x.UpdatedAt);
        b.Property(x => x.ConfirmedAt);
        b.Property(x => x.ConfirmedBy).HasMaxLength(320);
        b.Ignore(x => x.Given);
        b.Ignore(x => x.IsOver);
        b.Property(x => x.Grants)
            .HasField("_grants")
            .UsePropertyAccessMode(PropertyAccessMode.Field)
            .HasConversion(
                grants => JsonSerializer.Serialize(grants, (JsonSerializerOptions?)null),
                json => JsonSerializer.Deserialize<List<TicketGrant>>(json, (JsonSerializerOptions?)null) ?? new List<TicketGrant>(),
                new ValueComparer<IReadOnlyList<TicketGrant>>(
                    (a, c) => a!.SequenceEqual(c!),
                    grants => grants.Aggregate(0, (hash, g) => HashCode.Combine(hash, g)),
                    grants => grants.ToList()));
        b.HasOne<Event>().WithMany().HasForeignKey(x => x.EventId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.EventId).IsUnique(); // one allocation per race start
    }
}

internal sealed class PromptAnswerConfiguration : IEntityTypeConfiguration<PromptAnswer>
{
    public void Configure(EntityTypeBuilder<PromptAnswer> b)
    {
        b.MapTenantOwned();
        b.Property(x => x.Value).HasMaxLength(100);
        b.Property(x => x.Source).AsString();
        b.Property(x => x.RecordedBy).HasMaxLength(320);
        b.Property(x => x.AnsweredAt);
        b.HasOne<StreamItem>().WithMany().HasForeignKey(x => x.StreamItemId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Party>().WithMany().HasForeignKey(x => x.PartyId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.StreamItemId, x.PartyId }).IsUnique(); // the latest answer replaces the last
    }
}

internal sealed class NotificationPreferenceConfiguration : IEntityTypeConfiguration<NotificationPreference>
{
    public void Configure(EntityTypeBuilder<NotificationPreference> b)
    {
        b.MapTenantOwned();
        b.Property(x => x.Category).HasMaxLength(32).IsUnicode(false);
        b.Property(x => x.Channel).AsString();
        b.Property(x => x.Enabled);
        b.Property(x => x.UpdatedAt);
        b.HasOne<Party>().WithMany().HasForeignKey(x => x.PartyId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.PartyId, x.Category, x.Channel }).IsUnique();
    }
}

internal sealed class ItemReadConfiguration : IEntityTypeConfiguration<ItemRead>
{
    public void Configure(EntityTypeBuilder<ItemRead> b)
    {
        b.MapTenantOwned();
        b.Property(x => x.ReadAt);
        b.HasOne<StreamItem>().WithMany().HasForeignKey(x => x.StreamItemId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Party>().WithMany().HasForeignKey(x => x.PartyId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.PartyId, x.StreamItemId }).IsUnique();
    }
}

internal sealed class RoutingAddressConfiguration : IEntityTypeConfiguration<RoutingAddress>
{
    public void Configure(EntityTypeBuilder<RoutingAddress> b)
    {
        b.MapTenantOwned();
        b.Property(x => x.Token).HasMaxLength(HorseSlug.MaxLength).IsUnicode(false);
        b.Property(x => x.Kind).AsString();
        b.Property(x => x.CreatedAt);
        b.Property(x => x.Active);
        // Recipient tokens: unique across every tenant (the index is not tenant-filtered, which is the point).
        // Horse slugs: unique within a tenant; two tenants can each have a horse called Bel Esprit.
        b.HasIndex(x => x.Token).IsUnique().HasFilter("[Kind] = 'Recipient'").HasDatabaseName("IX_RoutingAddresses_RecipientToken");
        b.HasIndex("TenantId", nameof(RoutingAddress.Token)).IsUnique().HasFilter("[Kind] = 'HorseInbox'").HasDatabaseName("IX_RoutingAddresses_HorseSlug");
        b.HasOne<Horse>().WithMany().HasForeignKey(x => x.HorseId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Event>().WithMany().HasForeignKey(x => x.EventId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<StreamItem>().WithMany().HasForeignKey(x => x.StreamItemId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Party>().WithMany().HasForeignKey(x => x.PartyId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class MatchCandidateConfiguration : IEntityTypeConfiguration<MatchCandidate>
{
    public void Configure(EntityTypeBuilder<MatchCandidate> b)
    {
        b.MapTenantOwned();
        b.Property(x => x.Score);
        b.Property(x => x.Chosen);
        b.Property(x => x.Reasons)
            .HasConversion(
                reasons => JsonSerializer.Serialize(reasons, (JsonSerializerOptions?)null),
                json => JsonSerializer.Deserialize<List<string>>(json, (JsonSerializerOptions?)null) ?? new List<string>(),
                new ValueComparer<IReadOnlyList<string>>(
                    (a, c) => a!.SequenceEqual(c!),
                    reasons => reasons.Aggregate(0, (hash, r) => HashCode.Combine(hash, r)),
                    reasons => reasons.ToList()))
            .HasMaxLength(2000);
        b.HasOne<InboundMessage>().WithMany().HasForeignKey(x => x.InboundMessageId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Horse>().WithMany().HasForeignKey(x => x.HorseId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Event>().WithMany().HasForeignKey(x => x.EventId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.InboundMessageId);
    }
}

internal sealed class InboundMessageConfiguration : IEntityTypeConfiguration<InboundMessage>
{
    public void Configure(EntityTypeBuilder<InboundMessage> b)
    {
        b.MapTenantOwned();
        b.Ignore(x => x.IsWaiting);
        b.Property(x => x.Channel).HasMaxLength(16);
        b.Property(x => x.Provider).HasMaxLength(32);
        b.Property(x => x.ProviderMessageId).HasMaxLength(200);
        b.Property(x => x.ReceivedAt);
        b.Property(x => x.FromAddress).HasMaxLength(320);
        b.Property(x => x.FromName).HasMaxLength(200);
        b.Property(x => x.RecipientAddress).HasMaxLength(320);
        b.Property(x => x.Recipients);
        b.Property(x => x.Subject).HasMaxLength(1000);
        b.Property(x => x.TextBody);
        b.Property(x => x.HtmlBody);
        b.Property(x => x.Headers);
        b.Property(x => x.RawBlobName).HasMaxLength(400);
        b.Property(x => x.State).AsString();
        b.Property(x => x.StateAt);
        b.Property(x => x.Reason).HasMaxLength(500);
        b.Property(x => x.MatchTier);
        b.Property(x => x.DisplayBody);

        // A webhook delivered twice is stored once.
        b.HasIndex("TenantId", nameof(InboundMessage.Provider), nameof(InboundMessage.ProviderMessageId)).IsUnique();
        b.HasIndex("TenantId", nameof(InboundMessage.State), nameof(InboundMessage.ReceivedAt));
        b.HasOne<Mailout>().WithMany().HasForeignKey(x => x.MailoutId).OnDelete(DeleteBehavior.Restrict);

        b.HasOne<RoutingAddress>().WithMany().HasForeignKey(x => x.RoutingAddressId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Party>().WithMany().HasForeignKey(x => x.PartyId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Horse>().WithMany().HasForeignKey(x => x.HorseId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Event>().WithMany().HasForeignKey(x => x.EventId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<StreamItem>().WithMany().HasForeignKey(x => x.StreamItemId).OnDelete(DeleteBehavior.Restrict);

        b.OwnsMany(x => x.Attachments, a =>
        {
            a.ToTable("InboundAttachments");
            a.WithOwner().HasForeignKey("InboundMessageId");
            a.Property<int>("Id");
            a.HasKey("Id");
            a.Property(x => x.FileName).HasMaxLength(260);
            a.Property(x => x.ContentType).HasMaxLength(200);
            a.Property(x => x.Length);
            a.Property(x => x.ContentId).HasMaxLength(300);
            a.Property(x => x.BlobName).HasMaxLength(400);
            a.Property(x => x.DroppedReason).HasMaxLength(200);
        });
        b.Navigation(x => x.Attachments).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

internal sealed class OwnerInvitationConfiguration : IEntityTypeConfiguration<OwnerInvitation>
{
    public void Configure(EntityTypeBuilder<OwnerInvitation> b)
    {
        b.MapTenantOwned();
        b.Property(x => x.Status).AsString();
        b.Property(x => x.CreatedAt);
        b.Property(x => x.SentAt);
        b.Property(x => x.Address).HasMaxLength(320);
        b.Property(x => x.Note).HasMaxLength(300);
        b.HasIndex(x => x.PartyId).IsUnique(); // one invitation per party, ever
        b.HasIndex("TenantId", nameof(OwnerInvitation.Status));
        b.HasOne<Party>().WithMany().HasForeignKey(x => x.PartyId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Horse>().WithMany().HasForeignKey(x => x.HorseId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class AuditEntryConfiguration : IEntityTypeConfiguration<AuditEntry>
{
    public void Configure(EntityTypeBuilder<AuditEntry> b)
    {
        b.MapTenantOwned();
        b.Property(x => x.At);
        b.Property(x => x.ActorKind).AsString();
        b.Property(x => x.ActorPersonId);
        b.Property(x => x.ActorName).HasMaxLength(320);
        b.Property(x => x.Action).HasMaxLength(64);
        b.Property(x => x.Summary).HasMaxLength(1000);
        b.Property(x => x.EntityType).HasMaxLength(64);
        b.Property(x => x.EntityId);
        b.Property(x => x.OldValue).HasMaxLength(1000);
        b.Property(x => x.NewValue).HasMaxLength(1000);
        b.Property(x => x.IpAddress).HasMaxLength(64);
        b.Property(x => x.Device).HasMaxLength(300);
        b.HasIndex("TenantId", nameof(AuditEntry.At));
        b.HasIndex("TenantId", nameof(AuditEntry.Action), nameof(AuditEntry.At));
    }
}

internal sealed class SupportSessionConfiguration : IEntityTypeConfiguration<SupportSession>
{
    public void Configure(EntityTypeBuilder<SupportSession> b)
    {
        b.MapTenantOwned();
        b.Property(x => x.OperatorPersonId);
        b.Property(x => x.OperatorName).HasMaxLength(320);
        b.Property(x => x.Reason).HasMaxLength(1000);
        b.Property(x => x.Duration);
        b.Property(x => x.RequestedAt);
        b.Property(x => x.DecidedAt);
        b.Property(x => x.DecidedBy).HasMaxLength(320);
        b.Property(x => x.Approved);
        b.Property(x => x.ExpiresAt);
        b.Property(x => x.EndedAt);
        b.Property(x => x.EndedBy).HasMaxLength(320);
        b.Property(x => x.Emergency);
        b.Property(x => x.SecondOperatorPersonId);
        b.HasIndex("TenantId", nameof(SupportSession.RequestedAt));
        b.HasIndex(x => x.OperatorPersonId);
    }
}

internal static class ConfigurationExtensions
{
    /// <summary>Key, tenant column, tenant index and tenant foreign key — the shape every aggregate shares.</summary>
    public static void MapTenantOwned<T>(this EntityTypeBuilder<T> b)
        where T : class
    {
        const string id = "Id", tenantId = PaddocksideDbContext.TenantIdProperty;
        b.HasKey(id);
        b.Property<Guid>(id).ValueGeneratedNever();
        b.Property<Guid>(tenantId);
        b.HasIndex(tenantId);
        b.HasOne<Tenant>().WithMany().HasForeignKey(tenantId).OnDelete(DeleteBehavior.Restrict);
    }

    public static PropertyBuilder<TEnum> AsString<TEnum>(this PropertyBuilder<TEnum> property) =>
        property.HasConversion<string>().HasMaxLength(32);
}
