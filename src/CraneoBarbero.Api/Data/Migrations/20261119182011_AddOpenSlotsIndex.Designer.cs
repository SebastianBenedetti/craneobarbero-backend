using System;
using CraneoBarbero.Api.Data;
using CraneoBarbero.Api.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace CraneoBarbero.Api.Data.Migrations
{
    [DbContext(typeof(AppDbContext))]
    [Migration("20261119182011_AddOpenSlotsIndex")]
    partial class AddOpenSlotsIndex
    {
        protected override void BuildTargetModel(ModelBuilder modelBuilder)
        {
#pragma warning disable 612, 618
            modelBuilder
                .HasAnnotation("ProductVersion", "10.0.4")
                .HasAnnotation("Relational:MaxIdentifierLength", 63);

            NpgsqlModelBuilderExtensions.HasPostgresEnum(modelBuilder, "appointment_status", new[] { "awaiting_payment", "cancelled", "completed", "expired", "pending" });
            NpgsqlModelBuilderExtensions.HasPostgresEnum(modelBuilder, "auth_token_type", new[] { "email_verification", "password_reset" });
            NpgsqlModelBuilderExtensions.HasPostgresEnum(modelBuilder, "holiday_type", new[] { "custom", "national" });
            NpgsqlModelBuilderExtensions.HasPostgresEnum(modelBuilder, "notification_channel", new[] { "email" });
            NpgsqlModelBuilderExtensions.HasPostgresEnum(modelBuilder, "notification_status", new[] { "failed", "pending", "sent" });
            NpgsqlModelBuilderExtensions.HasPostgresEnum(modelBuilder, "notification_type", new[] { "cancellation", "confirmation", "email_verification", "modification", "password_reset", "payment", "reminder" });
            NpgsqlModelBuilderExtensions.HasPostgresEnum(modelBuilder, "order_status", new[] { "cancelled", "delivered", "expired", "paid", "pending_payment", "ready" });
            NpgsqlModelBuilderExtensions.HasPostgresEnum(modelBuilder, "payment_method", new[] { "card", "cash", "mercadopago", "transfer", "wallet" });
            NpgsqlModelBuilderExtensions.HasPostgresEnum(modelBuilder, "payment_status", new[] { "paid", "refunded", "unpaid" });
            NpgsqlModelBuilderExtensions.HasPostgresEnum(modelBuilder, "price_adjustment_source", new[] { "indec_api", "manual" });
            NpgsqlModelBuilderExtensions.HasPostgresEnum(modelBuilder, "price_change_reason", new[] { "inflation", "manual", "purchase", "revert" });
            NpgsqlModelBuilderExtensions.HasPostgresEnum(modelBuilder, "price_entity_type", new[] { "cut", "product" });
            NpgsqlModelBuilderExtensions.HasPostgresEnum(modelBuilder, "price_field", new[] { "cost", "price" });
            NpgsqlModelBuilderExtensions.HasPostgresEnum(modelBuilder, "refund_status", new[] { "none", "not_refunded", "refunded" });
            NpgsqlModelBuilderExtensions.HasPostgresEnum(modelBuilder, "slot_origin", new[] { "auto", "manual" });
            NpgsqlModelBuilderExtensions.HasPostgresEnum(modelBuilder, "stock_movement_type", new[] { "adjustment", "in", "refund", "sale" });
            NpgsqlModelBuilderExtensions.HasPostgresEnum(modelBuilder, "transaction_status", new[] { "approved", "pending", "refunded", "rejected" });
            NpgsqlModelBuilderExtensions.HasPostgresEnum(modelBuilder, "user_role", new[] { "admin", "barber", "client" });
            NpgsqlModelBuilderExtensions.HasPostgresExtension(modelBuilder, "extensions", "btree_gist");
            NpgsqlModelBuilderExtensions.UseIdentityByDefaultColumns(modelBuilder);

            modelBuilder.Entity("CraneoBarbero.Api.Domain.Entities.Appointment", b =>
                {
                    b.Property<int>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("integer")
                        .HasColumnName("id");

                    NpgsqlPropertyBuilderExtensions.UseIdentityByDefaultColumn(b.Property<int>("Id"));

                    b.Property<DateTimeOffset>("AppointmentDate")
                        .HasColumnType("timestamp with time zone")
                        .HasColumnName("appointmentDate");

                    b.Property<int>("BarberId")
                        .HasColumnType("integer")
                        .HasColumnName("barberId");

                    b.Property<int>("BranchId")
                        .HasColumnType("integer")
                        .HasColumnName("branchId");

                    b.Property<string>("CancellationReason")
                        .HasMaxLength(300)
                        .HasColumnType("character varying(300)")
                        .HasColumnName("cancellationReason");

                    b.Property<DateTimeOffset?>("CancelledAt")
                        .HasColumnType("timestamp with time zone")
                        .HasColumnName("cancelledAt");

                    b.Property<int?>("CancelledById")
                        .HasColumnType("integer")
                        .HasColumnName("cancelledById");

                    b.Property<UserRole?>("CancelledByRole")
                        .HasColumnType("user_role")
                        .HasColumnName("cancelledByRole");

                    b.Property<int?>("ClientId")
                        .HasColumnType("integer")
                        .HasColumnName("clientId");

                    b.Property<string>("ClientName")
                        .HasMaxLength(100)
                        .HasColumnType("character varying(100)")
                        .HasColumnName("clientName");

                    b.Property<DateTimeOffset>("CreatedAt")
                        .HasColumnType("timestamp with time zone")
                        .HasColumnName("createdAt");

                    b.Property<int?>("CreatedById")
                        .HasColumnType("integer")
                        .HasColumnName("createdById");

                    b.Property<UserRole>("CreatedByRole")
                        .HasColumnType("user_role")
                        .HasColumnName("createdByRole");

                    b.Property<int>("CutId")
                        .HasColumnType("integer")
                        .HasColumnName("cutId");

                    b.Property<DateTimeOffset>("EndDate")
                        .HasColumnType("timestamp with time zone")
                        .HasColumnName("endDate");

                    b.Property<DateTimeOffset?>("PaidAt")
                        .HasColumnType("timestamp with time zone")
                        .HasColumnName("paidAt");

                    b.Property<DateTimeOffset?>("PaymentExpiresAt")
                        .HasColumnType("timestamp with time zone")
                        .HasColumnName("paymentExpiresAt");

                    b.Property<PaymentMethod?>("PaymentMethod")
                        .HasColumnType("payment_method")
                        .HasColumnName("paymentMethod");

                    b.Property<PaymentStatus>("PaymentStatus")
                        .HasColumnType("payment_status")
                        .HasColumnName("paymentStatus");

                    b.Property<decimal>("Price")
                        .HasPrecision(10, 2)
                        .HasColumnType("numeric(10,2)")
                        .HasColumnName("price");

                    b.Property<AppointmentStatus>("Status")
                        .HasColumnType("appointment_status")
                        .HasColumnName("status");

                    b.Property<DateTimeOffset>("UpdatedAt")
                        .HasColumnType("timestamp with time zone")
                        .HasColumnName("updatedAt");

                    b.HasKey("Id");

                    b.HasIndex("BranchId");

                    b.HasIndex("CancelledById");

                    b.HasIndex("CreatedById");

                    b.HasIndex("CutId");

                    b.HasIndex("BarberId", "AppointmentDate");

                    b.HasIndex("ClientId", "AppointmentDate");

                    b.HasIndex("Status", "PaymentExpiresAt");

                    b.ToTable("Appointments", null, t =>
                        {
                            t.HasCheckConstraint("CK_Appointments_client", "\"clientId\" IS NOT NULL OR \"clientName\" IS NOT NULL");

                            t.HasCheckConstraint("CK_Appointments_endDate", "\"endDate\" > \"appointmentDate\"");

                            t.HasCheckConstraint("CK_Appointments_price", "\"price\" >= 0");
                        });
                });

            modelBuilder.Entity("CraneoBarbero.Api.Domain.Entities.AuthToken", b =>
                {
                    b.Property<int>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("integer")
                        .HasColumnName("id");

                    NpgsqlPropertyBuilderExtensions.UseIdentityByDefaultColumn(b.Property<int>("Id"));

                    b.Property<DateTimeOffset>("CreatedAt")
                        .HasColumnType("timestamp with time zone")
                        .HasColumnName("createdAt");

                    b.Property<DateTimeOffset>("ExpiresAt")
                        .HasColumnType("timestamp with time zone")
                        .HasColumnName("expiresAt");

                    b.Property<string>("TokenHash")
                        .IsRequired()
                        .HasMaxLength(128)
                        .HasColumnType("character varying(128)")
                        .HasColumnName("tokenHash");

                    b.Property<AuthTokenType>("Type")
                        .HasColumnType("auth_token_type")
                        .HasColumnName("type");

                    b.Property<DateTimeOffset?>("UsedAt")
                        .IsConcurrencyToken()
                        .HasColumnType("timestamp with time zone")
                        .HasColumnName("usedAt");

                    b.Property<int>("UserId")
                        .HasColumnType("integer")
                        .HasColumnName("userId");

                    b.HasKey("Id");

                    b.HasIndex("TokenHash")
                        .IsUnique();

                    b.HasIndex("UserId", "Type");

                    b.ToTable("AuthTokens", (string)null);
                });

            modelBuilder.Entity("CraneoBarbero.Api.Domain.Entities.AvailableSlot", b =>
                {
                    b.Property<int>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("integer")
                        .HasColumnName("id");

                    NpgsqlPropertyBuilderExtensions.UseIdentityByDefaultColumn(b.Property<int>("Id"));

                    b.Property<int>("BarberId")
                        .HasColumnType("integer")
                        .HasColumnName("barberId");

                    b.Property<DateTimeOffset>("CreatedAt")
                        .HasColumnType("timestamp with time zone")
                        .HasColumnName("createdAt");

                    b.Property<int?>("CreatedById")
                        .HasColumnType("integer")
                        .HasColumnName("createdById");

                    b.Property<string>("DeleteReason")
                        .HasMaxLength(300)
                        .HasColumnType("character varying(300)")
                        .HasColumnName("deleteReason");

                    b.Property<DateTimeOffset?>("DeletedAt")
                        .HasColumnType("timestamp with time zone")
                        .HasColumnName("deletedAt");

                    b.Property<int?>("DeletedById")
                        .HasColumnType("integer")
                        .HasColumnName("deletedById");

                    b.Property<SlotOrigin>("Origin")
                        .HasColumnType("slot_origin")
                        .HasColumnName("origin");

                    b.Property<DateTimeOffset>("StartAt")
                        .HasColumnType("timestamp with time zone")
                        .HasColumnName("startAt");

                    b.HasKey("Id");

                    b.HasIndex("CreatedById");

                    b.HasIndex("DeletedById");

                    b.HasIndex("StartAt");

                    b.HasIndex(new[] { "BarberId", "StartAt" }, "IX_AvailableSlots_barberId_startAt")
                        .IsUnique();

                    b.HasIndex(new[] { "BarberId", "StartAt" }, "IX_AvailableSlots_open")
                        .HasFilter("\"deletedAt\" IS NULL");

                    b.ToTable("AvailableSlots", (string)null);
                });

            modelBuilder.Entity("CraneoBarbero.Api.Domain.Entities.Branch", b =>
                {
                    b.Property<int>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("integer")
                        .HasColumnName("id");

                    NpgsqlPropertyBuilderExtensions.UseIdentityByDefaultColumn(b.Property<int>("Id"));

                    b.Property<string>("Address")
                        .IsRequired()
                        .HasMaxLength(200)
                        .HasColumnType("character varying(200)")
                        .HasColumnName("address");

                    b.Property<DateTimeOffset>("CreatedAt")
                        .HasColumnType("timestamp with time zone")
                        .HasColumnName("createdAt");

                    b.Property<string>("ImageUrl")
                        .HasMaxLength(500)
                        .HasColumnType("character varying(500)")
                        .HasColumnName("imageUrl");

                    b.Property<bool>("IsActive")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("boolean")
                        .HasDefaultValue(true)
                        .HasColumnName("isActive");

                    b.Property<string>("Name")
                        .IsRequired()
                        .HasMaxLength(100)
                        .HasColumnType("character varying(100)")
                        .HasColumnName("name");

                    b.Property<string>("Phone")
                        .HasMaxLength(30)
                        .HasColumnType("character varying(30)")
                        .HasColumnName("phone");

                    b.Property<DateTimeOffset>("UpdatedAt")
                        .HasColumnType("timestamp with time zone")
                        .HasColumnName("updatedAt");

                    b.HasKey("Id");

                    b.ToTable("Branches", (string)null);
                });

            modelBuilder.Entity("CraneoBarbero.Api.Domain.Entities.Cut", b =>
                {
                    b.Property<int>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("integer")
                        .HasColumnName("id");

                    NpgsqlPropertyBuilderExtensions.UseIdentityByDefaultColumn(b.Property<int>("Id"));

                    b.Property<DateTimeOffset>("CreatedAt")
                        .HasColumnType("timestamp with time zone")
                        .HasColumnName("createdAt");

                    b.Property<string>("Description")
                        .HasColumnType("text")
                        .HasColumnName("description");

                    b.Property<int>("DurationMinutes")
                        .HasColumnType("integer")
                        .HasColumnName("durationMinutes");

                    b.Property<bool>("IsActive")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("boolean")
                        .HasDefaultValue(true)
                        .HasColumnName("isActive");

                    b.Property<string>("Name")
                        .IsRequired()
                        .HasMaxLength(100)
                        .HasColumnType("character varying(100)")
                        .HasColumnName("name");

                    b.Property<decimal>("Price")
                        .HasPrecision(10, 2)
                        .HasColumnType("numeric(10,2)")
                        .HasColumnName("price");

                    b.Property<DateTimeOffset>("UpdatedAt")
                        .HasColumnType("timestamp with time zone")
                        .HasColumnName("updatedAt");

                    b.HasKey("Id");

                    b.ToTable("Cuts", null, t =>
                        {
                            t.HasCheckConstraint("CK_Cuts_durationMinutes", "\"durationMinutes\" > 0");

                            t.HasCheckConstraint("CK_Cuts_price", "\"price\" >= 0");
                        });
                });

            modelBuilder.Entity("CraneoBarbero.Api.Domain.Entities.Holiday", b =>
                {
                    b.Property<int>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("integer")
                        .HasColumnName("id");

                    NpgsqlPropertyBuilderExtensions.UseIdentityByDefaultColumn(b.Property<int>("Id"));

                    b.Property<DateTimeOffset>("CreatedAt")
                        .HasColumnType("timestamp with time zone")
                        .HasColumnName("createdAt");

                    b.Property<DateOnly>("Date")
                        .HasColumnType("date")
                        .HasColumnName("date");

                    b.Property<bool>("IsActive")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("boolean")
                        .HasDefaultValue(true)
                        .HasColumnName("isActive");

                    b.Property<string>("Name")
                        .IsRequired()
                        .HasMaxLength(150)
                        .HasColumnType("character varying(150)")
                        .HasColumnName("name");

                    b.Property<string>("Source")
                        .IsRequired()
                        .HasMaxLength(10)
                        .HasColumnType("character varying(10)")
                        .HasColumnName("source");

                    b.Property<HolidayType>("Type")
                        .HasColumnType("holiday_type")
                        .HasColumnName("type");

                    b.Property<DateTimeOffset>("UpdatedAt")
                        .HasColumnType("timestamp with time zone")
                        .HasColumnName("updatedAt");

                    b.HasKey("Id");

                    b.HasIndex("Date")
                        .IsUnique();

                    b.ToTable("Holidays", null, t =>
                        {
                            t.HasCheckConstraint("CK_Holidays_source", "\"source\" IN ('api', 'manual')");
                        });
                });

            modelBuilder.Entity("CraneoBarbero.Api.Domain.Entities.Notification", b =>
                {
                    b.Property<int>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("integer")
                        .HasColumnName("id");

                    NpgsqlPropertyBuilderExtensions.UseIdentityByDefaultColumn(b.Property<int>("Id"));

                    b.Property<int?>("AppointmentId")
                        .HasColumnType("integer")
                        .HasColumnName("appointmentId");

                    b.Property<NotificationChannel>("Channel")
                        .HasColumnType("notification_channel")
                        .HasColumnName("channel");

                    b.Property<DateTimeOffset>("CreatedAt")
                        .HasColumnType("timestamp with time zone")
                        .HasColumnName("createdAt");

                    b.Property<DateTimeOffset>("ScheduledAt")
                        .HasColumnType("timestamp with time zone")
                        .HasColumnName("scheduledAt");

                    b.Property<DateTimeOffset?>("SentAt")
                        .HasColumnType("timestamp with time zone")
                        .HasColumnName("sentAt");

                    b.Property<NotificationStatus>("Status")
                        .HasColumnType("notification_status")
                        .HasColumnName("status");

                    b.Property<NotificationType>("Type")
                        .HasColumnType("notification_type")
                        .HasColumnName("type");

                    b.Property<int>("UserId")
                        .HasColumnType("integer")
                        .HasColumnName("userId");

                    b.HasKey("Id");

                    b.HasIndex("UserId");

                    b.HasIndex("AppointmentId", "Type");

                    b.HasIndex("Status", "ScheduledAt");

                    b.ToTable("Notifications", (string)null);
                });

            modelBuilder.Entity("CraneoBarbero.Api.Domain.Entities.Order", b =>
                {
                    b.Property<int>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("integer")
                        .HasColumnName("id");

                    NpgsqlPropertyBuilderExtensions.UseIdentityByDefaultColumn(b.Property<int>("Id"));

                    b.Property<int>("BranchId")
                        .HasColumnType("integer")
                        .HasColumnName("branchId");

                    b.Property<string>("CancellationReason")
                        .HasMaxLength(300)
                        .HasColumnType("character varying(300)")
                        .HasColumnName("cancellationReason");

                    b.Property<int?>("CancelledById")
                        .HasColumnType("integer")
                        .HasColumnName("cancelledById");

                    b.Property<int?>("ClientId")
                        .HasColumnType("integer")
                        .HasColumnName("clientId");

                    b.Property<DateTimeOffset>("CreatedAt")
                        .HasColumnType("timestamp with time zone")
                        .HasColumnName("createdAt");

                    b.Property<DateTimeOffset?>("PaymentExpiresAt")
                        .HasColumnType("timestamp with time zone")
                        .HasColumnName("paymentExpiresAt");

                    b.Property<RefundStatus>("RefundStatus")
                        .HasColumnType("refund_status")
                        .HasColumnName("refundStatus");

                    b.Property<OrderStatus>("Status")
                        .HasColumnType("order_status")
                        .HasColumnName("status");

                    b.Property<decimal>("Total")
                        .HasPrecision(10, 2)
                        .HasColumnType("numeric(10,2)")
                        .HasColumnName("total");

                    b.Property<DateTimeOffset>("UpdatedAt")
                        .HasColumnType("timestamp with time zone")
                        .HasColumnName("updatedAt");

                    b.HasKey("Id");

                    b.HasIndex("BranchId");

                    b.HasIndex("CancelledById");

                    b.HasIndex("ClientId");

                    b.HasIndex("Status", "PaymentExpiresAt");

                    b.ToTable("Orders", null, t =>
                        {
                            t.HasCheckConstraint("CK_Orders_total", "\"total\" >= 0");
                        });
                });

            modelBuilder.Entity("CraneoBarbero.Api.Domain.Entities.OrderItem", b =>
                {
                    b.Property<int>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("integer")
                        .HasColumnName("id");

                    NpgsqlPropertyBuilderExtensions.UseIdentityByDefaultColumn(b.Property<int>("Id"));

                    b.Property<int>("OrderId")
                        .HasColumnType("integer")
                        .HasColumnName("orderId");

                    b.Property<int>("ProductId")
                        .HasColumnType("integer")
                        .HasColumnName("productId");

                    b.Property<int>("Quantity")
                        .HasColumnType("integer")
                        .HasColumnName("quantity");

                    b.Property<decimal?>("UnitCost")
                        .HasPrecision(10, 2)
                        .HasColumnType("numeric(10,2)")
                        .HasColumnName("unitCost");

                    b.Property<decimal>("UnitPrice")
                        .HasPrecision(10, 2)
                        .HasColumnType("numeric(10,2)")
                        .HasColumnName("unitPrice");

                    b.HasKey("Id");

                    b.HasIndex("ProductId");

                    b.HasIndex("OrderId", "ProductId")
                        .IsUnique();

                    b.ToTable("OrderItems", null, t =>
                        {
                            t.HasCheckConstraint("CK_OrderItems_quantity", "\"quantity\" > 0");

                            t.HasCheckConstraint("CK_OrderItems_unitPrice", "\"unitPrice\" >= 0");
                        });
                });

            modelBuilder.Entity("CraneoBarbero.Api.Domain.Entities.Payment", b =>
                {
                    b.Property<int>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("integer")
                        .HasColumnName("id");

                    NpgsqlPropertyBuilderExtensions.UseIdentityByDefaultColumn(b.Property<int>("Id"));

                    b.Property<decimal>("Amount")
                        .HasPrecision(10, 2)
                        .HasColumnType("numeric(10,2)")
                        .HasColumnName("amount");

                    b.Property<int?>("AppointmentId")
                        .HasColumnType("integer")
                        .HasColumnName("appointmentId");

                    b.Property<DateTimeOffset>("CreatedAt")
                        .HasColumnType("timestamp with time zone")
                        .HasColumnName("createdAt");

                    b.Property<string>("ExternalId")
                        .HasMaxLength(100)
                        .HasColumnType("character varying(100)")
                        .HasColumnName("externalId");

                    b.Property<PaymentMethod>("Method")
                        .HasColumnType("payment_method")
                        .HasColumnName("method");

                    b.Property<int?>("OrderId")
                        .HasColumnType("integer")
                        .HasColumnName("orderId");

                    b.Property<DateTimeOffset?>("PaidAt")
                        .HasColumnType("timestamp with time zone")
                        .HasColumnName("paidAt");

                    b.Property<string>("PreferenceId")
                        .HasMaxLength(100)
                        .HasColumnType("character varying(100)")
                        .HasColumnName("preferenceId");

                    b.Property<DateTimeOffset?>("RefundedAt")
                        .HasColumnType("timestamp with time zone")
                        .HasColumnName("refundedAt");

                    b.Property<TransactionStatus>("Status")
                        .HasColumnType("transaction_status")
                        .HasColumnName("status");

                    b.Property<DateTimeOffset>("UpdatedAt")
                        .HasColumnType("timestamp with time zone")
                        .HasColumnName("updatedAt");

                    b.HasKey("Id");

                    b.HasIndex("AppointmentId");

                    b.HasIndex("ExternalId")
                        .IsUnique();

                    b.HasIndex("OrderId");

                    b.ToTable("Payments", null, t =>
                        {
                            t.HasCheckConstraint("CK_Payments_amount", "\"amount\" > 0");

                            t.HasCheckConstraint("CK_Payments_single_target", "(\"orderId\" IS NOT NULL AND \"appointmentId\" IS NULL) OR (\"orderId\" IS NULL AND \"appointmentId\" IS NOT NULL)");
                        });
                });

            modelBuilder.Entity("CraneoBarbero.Api.Domain.Entities.PriceAdjustment", b =>
                {
                    b.Property<int>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("integer")
                        .HasColumnName("id");

                    NpgsqlPropertyBuilderExtensions.UseIdentityByDefaultColumn(b.Property<int>("Id"));

                    b.Property<DateTimeOffset>("AppliedAt")
                        .HasColumnType("timestamp with time zone")
                        .HasColumnName("appliedAt");

                    b.Property<int>("AppliedById")
                        .HasColumnType("integer")
                        .HasColumnName("appliedById");

                    b.Property<bool>("IncludesCuts")
                        .HasColumnType("boolean")
                        .HasColumnName("includesCuts");

                    b.Property<int>("ItemsCount")
                        .HasColumnType("integer")
                        .HasColumnName("itemsCount");

                    b.Property<decimal>("Percentage")
                        .HasPrecision(7, 4)
                        .HasColumnType("numeric(7,4)")
                        .HasColumnName("percentage");

                    b.Property<DateOnly?>("PeriodFrom")
                        .HasColumnType("date")
                        .HasColumnName("periodFrom");

                    b.Property<DateOnly?>("PeriodTo")
                        .HasColumnType("date")
                        .HasColumnName("periodTo");

                    b.Property<DateTimeOffset?>("RevertedAt")
                        .HasColumnType("timestamp with time zone")
                        .HasColumnName("revertedAt");

                    b.Property<int?>("RevertedById")
                        .HasColumnType("integer")
                        .HasColumnName("revertedById");

                    b.Property<string>("RoundingMode")
                        .IsRequired()
                        .HasMaxLength(20)
                        .HasColumnType("character varying(20)")
                        .HasColumnName("roundingMode");

                    b.Property<string>("ScopeDescription")
                        .HasMaxLength(300)
                        .HasColumnType("character varying(300)")
                        .HasColumnName("scopeDescription");

                    b.Property<string>("ScopeType")
                        .IsRequired()
                        .HasMaxLength(20)
                        .HasColumnType("character varying(20)")
                        .HasColumnName("scopeType");

                    b.Property<PriceAdjustmentSource>("Source")
                        .HasColumnType("price_adjustment_source")
                        .HasColumnName("source");

                    b.HasKey("Id");

                    b.HasIndex("AppliedById");

                    b.HasIndex("RevertedById");

                    b.ToTable("PriceAdjustments", null, t =>
                        {
                            t.HasCheckConstraint("CK_PriceAdjustments_scopeType", "\"scopeType\" IN ('all', 'category', 'selection')");
                        });
                });

            modelBuilder.Entity("CraneoBarbero.Api.Domain.Entities.PriceHistory", b =>
                {
                    b.Property<int>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("integer")
                        .HasColumnName("id");

                    NpgsqlPropertyBuilderExtensions.UseIdentityByDefaultColumn(b.Property<int>("Id"));

                    b.Property<int?>("AdjustmentId")
                        .HasColumnType("integer")
                        .HasColumnName("adjustmentId");

                    b.Property<DateTimeOffset>("ChangedAt")
                        .HasColumnType("timestamp with time zone")
                        .HasColumnName("changedAt");

                    b.Property<int>("ChangedById")
                        .HasColumnType("integer")
                        .HasColumnName("changedById");

                    b.Property<int>("EntityId")
                        .HasColumnType("integer")
                        .HasColumnName("entityId");

                    b.Property<PriceEntityType>("EntityType")
                        .HasColumnType("price_entity_type")
                        .HasColumnName("entityType");

                    b.Property<PriceField>("Field")
                        .HasColumnType("price_field")
                        .HasColumnName("field");

                    b.Property<decimal>("NewPrice")
                        .HasPrecision(10, 2)
                        .HasColumnType("numeric(10,2)")
                        .HasColumnName("newPrice");

                    b.Property<decimal>("OldPrice")
                        .HasPrecision(10, 2)
                        .HasColumnType("numeric(10,2)")
                        .HasColumnName("oldPrice");

                    b.Property<PriceChangeReason>("Reason")
                        .HasColumnType("price_change_reason")
                        .HasColumnName("reason");

                    b.HasKey("Id");

                    b.HasIndex("AdjustmentId");

                    b.HasIndex("ChangedById");

                    b.HasIndex("EntityType", "EntityId", "ChangedAt");

                    b.ToTable("PriceHistory", (string)null);
                });

            modelBuilder.Entity("CraneoBarbero.Api.Domain.Entities.Product", b =>
                {
                    b.Property<int>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("integer")
                        .HasColumnName("id");

                    NpgsqlPropertyBuilderExtensions.UseIdentityByDefaultColumn(b.Property<int>("Id"));

                    b.Property<string>("Barcode")
                        .HasMaxLength(50)
                        .HasColumnType("character varying(50)")
                        .HasColumnName("barcode");

                    b.Property<int>("CategoryId")
                        .HasColumnType("integer")
                        .HasColumnName("categoryId");

                    b.Property<decimal>("Cost")
                        .ValueGeneratedOnAdd()
                        .HasPrecision(10, 2)
                        .HasColumnType("numeric(10,2)")
                        .HasDefaultValue(0m)
                        .HasColumnName("cost");

                    b.Property<DateTimeOffset>("CreatedAt")
                        .HasColumnType("timestamp with time zone")
                        .HasColumnName("createdAt");

                    b.Property<int?>("DefaultSupplierId")
                        .HasColumnType("integer")
                        .HasColumnName("defaultSupplierId");

                    b.Property<string>("Description")
                        .HasColumnType("text")
                        .HasColumnName("description");

                    b.Property<string>("ImageUrl")
                        .HasMaxLength(500)
                        .HasColumnType("character varying(500)")
                        .HasColumnName("imageUrl");

                    b.Property<bool>("IsActive")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("boolean")
                        .HasDefaultValue(true)
                        .HasColumnName("isActive");

                    b.Property<int>("MinStock")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("integer")
                        .HasDefaultValue(0)
                        .HasColumnName("minStock");

                    b.Property<string>("Name")
                        .IsRequired()
                        .HasMaxLength(150)
                        .HasColumnType("character varying(150)")
                        .HasColumnName("name");

                    b.Property<int?>("ReorderQuantity")
                        .HasColumnType("integer")
                        .HasColumnName("reorderQuantity");

                    b.Property<decimal>("SalePrice")
                        .HasPrecision(10, 2)
                        .HasColumnType("numeric(10,2)")
                        .HasColumnName("salePrice");

                    b.Property<string>("Sku")
                        .HasMaxLength(50)
                        .HasColumnType("character varying(50)")
                        .HasColumnName("sku");

                    b.Property<int>("Stock")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("integer")
                        .HasDefaultValue(0)
                        .HasColumnName("stock");

                    b.Property<DateTimeOffset>("UpdatedAt")
                        .HasColumnType("timestamp with time zone")
                        .HasColumnName("updatedAt");

                    b.HasKey("Id");

                    b.HasIndex("CategoryId");

                    b.HasIndex("DefaultSupplierId");

                    b.HasIndex("Sku")
                        .IsUnique();

                    b.ToTable("Products", null, t =>
                        {
                            t.HasCheckConstraint("CK_Products_cost", "\"cost\" >= 0");

                            t.HasCheckConstraint("CK_Products_minStock", "\"minStock\" >= 0");

                            t.HasCheckConstraint("CK_Products_reorderQuantity", "\"reorderQuantity\" IS NULL OR \"reorderQuantity\" > 0");

                            t.HasCheckConstraint("CK_Products_salePrice", "\"salePrice\" >= 0");

                            t.HasCheckConstraint("CK_Products_stock", "\"stock\" >= 0");
                        });
                });

            modelBuilder.Entity("CraneoBarbero.Api.Domain.Entities.ProductCategory", b =>
                {
                    b.Property<int>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("integer")
                        .HasColumnName("id");

                    NpgsqlPropertyBuilderExtensions.UseIdentityByDefaultColumn(b.Property<int>("Id"));

                    b.Property<DateTimeOffset>("CreatedAt")
                        .HasColumnType("timestamp with time zone")
                        .HasColumnName("createdAt");

                    b.Property<string>("Name")
                        .IsRequired()
                        .HasMaxLength(100)
                        .HasColumnType("character varying(100)")
                        .HasColumnName("name");

                    b.Property<DateTimeOffset>("UpdatedAt")
                        .HasColumnType("timestamp with time zone")
                        .HasColumnName("updatedAt");

                    b.HasKey("Id");

                    b.HasIndex("Name")
                        .IsUnique();

                    b.ToTable("ProductCategories", (string)null);
                });

            modelBuilder.Entity("CraneoBarbero.Api.Domain.Entities.Review", b =>
                {
                    b.Property<int>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("integer")
                        .HasColumnName("id");

                    NpgsqlPropertyBuilderExtensions.UseIdentityByDefaultColumn(b.Property<int>("Id"));

                    b.Property<int>("AppointmentId")
                        .HasColumnType("integer")
                        .HasColumnName("appointmentId");

                    b.Property<string>("Comment")
                        .HasColumnType("text")
                        .HasColumnName("comment");

                    b.Property<DateTimeOffset>("CreatedAt")
                        .HasColumnType("timestamp with time zone")
                        .HasColumnName("createdAt");

                    b.Property<short>("RatingBarber")
                        .HasColumnType("smallint")
                        .HasColumnName("ratingBarber");

                    b.Property<short>("RatingCut")
                        .HasColumnType("smallint")
                        .HasColumnName("ratingCut");

                    b.Property<DateTimeOffset>("UpdatedAt")
                        .HasColumnType("timestamp with time zone")
                        .HasColumnName("updatedAt");

                    b.HasKey("Id");

                    b.HasIndex("AppointmentId")
                        .IsUnique();

                    b.ToTable("Reviews", null, t =>
                        {
                            t.HasCheckConstraint("CK_Reviews_ratingBarber", "\"ratingBarber\" BETWEEN 1 AND 5");

                            t.HasCheckConstraint("CK_Reviews_ratingCut", "\"ratingCut\" BETWEEN 1 AND 5");
                        });
                });

            modelBuilder.Entity("CraneoBarbero.Api.Domain.Entities.StockMovement", b =>
                {
                    b.Property<int>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("integer")
                        .HasColumnName("id");

                    NpgsqlPropertyBuilderExtensions.UseIdentityByDefaultColumn(b.Property<int>("Id"));

                    b.Property<int>("BalanceAfter")
                        .HasColumnType("integer")
                        .HasColumnName("balanceAfter");

                    b.Property<DateTimeOffset>("CreatedAt")
                        .HasColumnType("timestamp with time zone")
                        .HasColumnName("createdAt");

                    b.Property<string>("DocumentNumber")
                        .HasMaxLength(50)
                        .HasColumnType("character varying(50)")
                        .HasColumnName("documentNumber");

                    b.Property<DateTimeOffset>("OccurredAt")
                        .HasColumnType("timestamp with time zone")
                        .HasColumnName("occurredAt");

                    b.Property<int?>("OrderId")
                        .HasColumnType("integer")
                        .HasColumnName("orderId");

                    b.Property<int>("ProductId")
                        .HasColumnType("integer")
                        .HasColumnName("productId");

                    b.Property<int>("Quantity")
                        .HasColumnType("integer")
                        .HasColumnName("quantity");

                    b.Property<string>("Reason")
                        .HasMaxLength(300)
                        .HasColumnType("character varying(300)")
                        .HasColumnName("reason");

                    b.Property<int?>("SupplierId")
                        .HasColumnType("integer")
                        .HasColumnName("supplierId");

                    b.Property<StockMovementType>("Type")
                        .HasColumnType("stock_movement_type")
                        .HasColumnName("type");

                    b.Property<decimal?>("UnitCost")
                        .HasPrecision(10, 2)
                        .HasColumnType("numeric(10,2)")
                        .HasColumnName("unitCost");

                    b.Property<int>("UserId")
                        .HasColumnType("integer")
                        .HasColumnName("userId");

                    b.HasKey("Id");

                    b.HasIndex("OrderId");

                    b.HasIndex("SupplierId");

                    b.HasIndex("UserId");

                    b.HasIndex("ProductId", "OccurredAt");

                    b.ToTable("StockMovements", null, t =>
                        {
                            t.HasCheckConstraint("CK_StockMovements_adjustment_reason", "\"type\" <> 'adjustment' OR \"reason\" IS NOT NULL");

                            t.HasCheckConstraint("CK_StockMovements_balanceAfter", "\"balanceAfter\" >= 0");

                            t.HasCheckConstraint("CK_StockMovements_in_cost", "\"type\" <> 'in' OR \"unitCost\" IS NOT NULL");

                            t.HasCheckConstraint("CK_StockMovements_quantity", "\"quantity\" <> 0");
                        });
                });

            modelBuilder.Entity("CraneoBarbero.Api.Domain.Entities.Supplier", b =>
                {
                    b.Property<int>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("integer")
                        .HasColumnName("id");

                    NpgsqlPropertyBuilderExtensions.UseIdentityByDefaultColumn(b.Property<int>("Id"));

                    b.Property<string>("ContactName")
                        .HasMaxLength(100)
                        .HasColumnType("character varying(100)")
                        .HasColumnName("contactName");

                    b.Property<DateTimeOffset>("CreatedAt")
                        .HasColumnType("timestamp with time zone")
                        .HasColumnName("createdAt");

                    b.Property<string>("Email")
                        .HasMaxLength(254)
                        .HasColumnType("character varying(254)")
                        .HasColumnName("email");

                    b.Property<bool>("IsActive")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("boolean")
                        .HasDefaultValue(true)
                        .HasColumnName("isActive");

                    b.Property<string>("Name")
                        .IsRequired()
                        .HasMaxLength(150)
                        .HasColumnType("character varying(150)")
                        .HasColumnName("name");

                    b.Property<string>("Notes")
                        .HasColumnType("text")
                        .HasColumnName("notes");

                    b.Property<string>("Phone")
                        .HasMaxLength(30)
                        .HasColumnType("character varying(30)")
                        .HasColumnName("phone");

                    b.Property<string>("TaxId")
                        .HasMaxLength(20)
                        .HasColumnType("character varying(20)")
                        .HasColumnName("taxId");

                    b.Property<DateTimeOffset>("UpdatedAt")
                        .HasColumnType("timestamp with time zone")
                        .HasColumnName("updatedAt");

                    b.HasKey("Id");

                    b.ToTable("Suppliers", (string)null);
                });

            modelBuilder.Entity("CraneoBarbero.Api.Domain.Entities.User", b =>
                {
                    b.Property<int>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("integer")
                        .HasColumnName("id");

                    NpgsqlPropertyBuilderExtensions.UseIdentityByDefaultColumn(b.Property<int>("Id"));

                    b.Property<int?>("BranchId")
                        .HasColumnType("integer")
                        .HasColumnName("branchId");

                    b.Property<DateTimeOffset>("CreatedAt")
                        .HasColumnType("timestamp with time zone")
                        .HasColumnName("createdAt");

                    b.Property<string>("Email")
                        .IsRequired()
                        .HasMaxLength(254)
                        .HasColumnType("character varying(254)")
                        .HasColumnName("email");

                    b.Property<bool>("EmailVerified")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("boolean")
                        .HasDefaultValue(false)
                        .HasColumnName("emailVerified");

                    b.Property<string>("ImageUrl")
                        .HasMaxLength(500)
                        .HasColumnType("character varying(500)")
                        .HasColumnName("imageUrl");

                    b.Property<bool>("IsActive")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("boolean")
                        .HasDefaultValue(true)
                        .HasColumnName("isActive");

                    b.Property<string>("Name")
                        .IsRequired()
                        .HasMaxLength(100)
                        .HasColumnType("character varying(100)")
                        .HasColumnName("name");

                    b.Property<string>("PasswordHash")
                        .IsRequired()
                        .HasMaxLength(100)
                        .HasColumnType("character varying(100)")
                        .HasColumnName("password");

                    b.Property<string>("Phone")
                        .HasMaxLength(30)
                        .HasColumnType("character varying(30)")
                        .HasColumnName("phone");

                    b.Property<UserRole>("Role")
                        .HasColumnType("user_role")
                        .HasColumnName("role");

                    b.Property<DateOnly?>("StartDate")
                        .HasColumnType("date")
                        .HasColumnName("startDate");

                    b.Property<DateTimeOffset>("UpdatedAt")
                        .HasColumnType("timestamp with time zone")
                        .HasColumnName("updatedAt");

                    b.HasKey("Id");

                    b.HasIndex("BranchId");

                    b.HasIndex("Email")
                        .IsUnique();

                    b.HasIndex("Role", "IsActive");

                    b.ToTable("Users", null, t =>
                        {
                            t.HasCheckConstraint("CK_Users_barber_without_phone", "\"role\" <> 'barber' OR \"phone\" IS NULL");

                            t.HasCheckConstraint("CK_Users_branch_by_role", "(\"role\" = 'barber' AND \"branchId\" IS NOT NULL) OR (\"role\" <> 'barber' AND \"branchId\" IS NULL)");

                            t.HasCheckConstraint("CK_Users_email_normalized", "\"email\" = lower(btrim(\"email\"))");
                        });
                });

            modelBuilder.Entity("CraneoBarbero.Api.Domain.Entities.Appointment", b =>
                {
                    b.HasOne("CraneoBarbero.Api.Domain.Entities.User", "Barber")
                        .WithMany()
                        .HasForeignKey("BarberId")
                        .OnDelete(DeleteBehavior.Restrict)
                        .IsRequired();

                    b.HasOne("CraneoBarbero.Api.Domain.Entities.Branch", "Branch")
                        .WithMany()
                        .HasForeignKey("BranchId")
                        .OnDelete(DeleteBehavior.Restrict)
                        .IsRequired();

                    b.HasOne("CraneoBarbero.Api.Domain.Entities.User", null)
                        .WithMany()
                        .HasForeignKey("CancelledById")
                        .OnDelete(DeleteBehavior.SetNull);

                    b.HasOne("CraneoBarbero.Api.Domain.Entities.User", "Client")
                        .WithMany()
                        .HasForeignKey("ClientId")
                        .OnDelete(DeleteBehavior.SetNull);

                    b.HasOne("CraneoBarbero.Api.Domain.Entities.User", null)
                        .WithMany()
                        .HasForeignKey("CreatedById")
                        .OnDelete(DeleteBehavior.SetNull);

                    b.HasOne("CraneoBarbero.Api.Domain.Entities.Cut", "Cut")
                        .WithMany()
                        .HasForeignKey("CutId")
                        .OnDelete(DeleteBehavior.Restrict)
                        .IsRequired();

                    b.Navigation("Barber");

                    b.Navigation("Branch");

                    b.Navigation("Client");

                    b.Navigation("Cut");
                });

            modelBuilder.Entity("CraneoBarbero.Api.Domain.Entities.AuthToken", b =>
                {
                    b.HasOne("CraneoBarbero.Api.Domain.Entities.User", null)
                        .WithMany()
                        .HasForeignKey("UserId")
                        .OnDelete(DeleteBehavior.Cascade)
                        .IsRequired();
                });

            modelBuilder.Entity("CraneoBarbero.Api.Domain.Entities.AvailableSlot", b =>
                {
                    b.HasOne("CraneoBarbero.Api.Domain.Entities.User", null)
                        .WithMany()
                        .HasForeignKey("BarberId")
                        .OnDelete(DeleteBehavior.Restrict)
                        .IsRequired();

                    b.HasOne("CraneoBarbero.Api.Domain.Entities.User", null)
                        .WithMany()
                        .HasForeignKey("CreatedById")
                        .OnDelete(DeleteBehavior.SetNull);

                    b.HasOne("CraneoBarbero.Api.Domain.Entities.User", null)
                        .WithMany()
                        .HasForeignKey("DeletedById")
                        .OnDelete(DeleteBehavior.SetNull);
                });

            modelBuilder.Entity("CraneoBarbero.Api.Domain.Entities.Notification", b =>
                {
                    b.HasOne("CraneoBarbero.Api.Domain.Entities.Appointment", null)
                        .WithMany()
                        .HasForeignKey("AppointmentId")
                        .OnDelete(DeleteBehavior.SetNull);

                    b.HasOne("CraneoBarbero.Api.Domain.Entities.User", null)
                        .WithMany()
                        .HasForeignKey("UserId")
                        .OnDelete(DeleteBehavior.Cascade)
                        .IsRequired();
                });

            modelBuilder.Entity("CraneoBarbero.Api.Domain.Entities.Order", b =>
                {
                    b.HasOne("CraneoBarbero.Api.Domain.Entities.Branch", "Branch")
                        .WithMany()
                        .HasForeignKey("BranchId")
                        .OnDelete(DeleteBehavior.Restrict)
                        .IsRequired();

                    b.HasOne("CraneoBarbero.Api.Domain.Entities.User", null)
                        .WithMany()
                        .HasForeignKey("CancelledById")
                        .OnDelete(DeleteBehavior.SetNull);

                    b.HasOne("CraneoBarbero.Api.Domain.Entities.User", "Client")
                        .WithMany()
                        .HasForeignKey("ClientId")
                        .OnDelete(DeleteBehavior.SetNull);

                    b.Navigation("Branch");

                    b.Navigation("Client");
                });

            modelBuilder.Entity("CraneoBarbero.Api.Domain.Entities.OrderItem", b =>
                {
                    b.HasOne("CraneoBarbero.Api.Domain.Entities.Order", "Order")
                        .WithMany("Items")
                        .HasForeignKey("OrderId")
                        .OnDelete(DeleteBehavior.Cascade)
                        .IsRequired();

                    b.HasOne("CraneoBarbero.Api.Domain.Entities.Product", "Product")
                        .WithMany()
                        .HasForeignKey("ProductId")
                        .OnDelete(DeleteBehavior.Restrict)
                        .IsRequired();

                    b.Navigation("Order");

                    b.Navigation("Product");
                });

            modelBuilder.Entity("CraneoBarbero.Api.Domain.Entities.Payment", b =>
                {
                    b.HasOne("CraneoBarbero.Api.Domain.Entities.Appointment", "Appointment")
                        .WithMany()
                        .HasForeignKey("AppointmentId")
                        .OnDelete(DeleteBehavior.Restrict);

                    b.HasOne("CraneoBarbero.Api.Domain.Entities.Order", "Order")
                        .WithMany()
                        .HasForeignKey("OrderId")
                        .OnDelete(DeleteBehavior.Restrict);

                    b.Navigation("Appointment");

                    b.Navigation("Order");
                });

            modelBuilder.Entity("CraneoBarbero.Api.Domain.Entities.PriceAdjustment", b =>
                {
                    b.HasOne("CraneoBarbero.Api.Domain.Entities.User", null)
                        .WithMany()
                        .HasForeignKey("AppliedById")
                        .OnDelete(DeleteBehavior.Restrict)
                        .IsRequired();

                    b.HasOne("CraneoBarbero.Api.Domain.Entities.User", null)
                        .WithMany()
                        .HasForeignKey("RevertedById")
                        .OnDelete(DeleteBehavior.Restrict);
                });

            modelBuilder.Entity("CraneoBarbero.Api.Domain.Entities.PriceHistory", b =>
                {
                    b.HasOne("CraneoBarbero.Api.Domain.Entities.PriceAdjustment", null)
                        .WithMany()
                        .HasForeignKey("AdjustmentId")
                        .OnDelete(DeleteBehavior.Restrict);

                    b.HasOne("CraneoBarbero.Api.Domain.Entities.User", null)
                        .WithMany()
                        .HasForeignKey("ChangedById")
                        .OnDelete(DeleteBehavior.Restrict)
                        .IsRequired();
                });

            modelBuilder.Entity("CraneoBarbero.Api.Domain.Entities.Product", b =>
                {
                    b.HasOne("CraneoBarbero.Api.Domain.Entities.ProductCategory", "Category")
                        .WithMany()
                        .HasForeignKey("CategoryId")
                        .OnDelete(DeleteBehavior.Restrict)
                        .IsRequired();

                    b.HasOne("CraneoBarbero.Api.Domain.Entities.Supplier", "DefaultSupplier")
                        .WithMany()
                        .HasForeignKey("DefaultSupplierId")
                        .OnDelete(DeleteBehavior.SetNull);

                    b.Navigation("Category");

                    b.Navigation("DefaultSupplier");
                });

            modelBuilder.Entity("CraneoBarbero.Api.Domain.Entities.Review", b =>
                {
                    b.HasOne("CraneoBarbero.Api.Domain.Entities.Appointment", "Appointment")
                        .WithMany()
                        .HasForeignKey("AppointmentId")
                        .OnDelete(DeleteBehavior.Restrict)
                        .IsRequired();

                    b.Navigation("Appointment");
                });

            modelBuilder.Entity("CraneoBarbero.Api.Domain.Entities.StockMovement", b =>
                {
                    b.HasOne("CraneoBarbero.Api.Domain.Entities.Order", null)
                        .WithMany()
                        .HasForeignKey("OrderId")
                        .OnDelete(DeleteBehavior.Restrict);

                    b.HasOne("CraneoBarbero.Api.Domain.Entities.Product", "Product")
                        .WithMany()
                        .HasForeignKey("ProductId")
                        .OnDelete(DeleteBehavior.Restrict)
                        .IsRequired();

                    b.HasOne("CraneoBarbero.Api.Domain.Entities.Supplier", null)
                        .WithMany()
                        .HasForeignKey("SupplierId")
                        .OnDelete(DeleteBehavior.Restrict);

                    b.HasOne("CraneoBarbero.Api.Domain.Entities.User", null)
                        .WithMany()
                        .HasForeignKey("UserId")
                        .OnDelete(DeleteBehavior.Restrict)
                        .IsRequired();

                    b.Navigation("Product");
                });

            modelBuilder.Entity("CraneoBarbero.Api.Domain.Entities.User", b =>
                {
                    b.HasOne("CraneoBarbero.Api.Domain.Entities.Branch", "Branch")
                        .WithMany()
                        .HasForeignKey("BranchId")
                        .OnDelete(DeleteBehavior.Restrict);

                    b.Navigation("Branch");
                });

            modelBuilder.Entity("CraneoBarbero.Api.Domain.Entities.Order", b =>
                {
                    b.Navigation("Items");
                });
#pragma warning restore 612, 618
        }
    }
}
