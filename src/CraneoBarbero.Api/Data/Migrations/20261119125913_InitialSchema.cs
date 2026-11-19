using System;
using CraneoBarbero.Api.Domain.Enums;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace CraneoBarbero.Api.Data.Migrations
{
    public partial class InitialSchema : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:appointment_status", "awaiting_payment,cancelled,completed,expired,pending")
                .Annotation("Npgsql:Enum:auth_token_type", "email_verification,password_reset")
                .Annotation("Npgsql:Enum:holiday_type", "custom,national")
                .Annotation("Npgsql:Enum:notification_channel", "email")
                .Annotation("Npgsql:Enum:notification_status", "failed,pending,sent")
                .Annotation("Npgsql:Enum:notification_type", "cancellation,confirmation,reminder")
                .Annotation("Npgsql:Enum:order_status", "cancelled,delivered,expired,paid,pending_payment,ready")
                .Annotation("Npgsql:Enum:payment_method", "card,cash,mercadopago,transfer,wallet")
                .Annotation("Npgsql:Enum:payment_status", "paid,refunded,unpaid")
                .Annotation("Npgsql:Enum:price_adjustment_source", "indec_api,manual")
                .Annotation("Npgsql:Enum:price_change_reason", "inflation,manual,purchase,revert")
                .Annotation("Npgsql:Enum:price_entity_type", "cut,product")
                .Annotation("Npgsql:Enum:price_field", "cost,price")
                .Annotation("Npgsql:Enum:refund_status", "none,not_refunded,refunded")
                .Annotation("Npgsql:Enum:slot_origin", "auto,manual")
                .Annotation("Npgsql:Enum:stock_movement_type", "adjustment,in,refund,sale")
                .Annotation("Npgsql:Enum:transaction_status", "approved,pending,refunded,rejected")
                .Annotation("Npgsql:Enum:user_role", "admin,barber,client")
                .Annotation("Npgsql:PostgresExtension:extensions.btree_gist", ",,");

            migrationBuilder.CreateTable(
                name: "Branches",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    address = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    phone = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    imageUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    isActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    createdAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Branches", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "Cuts",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    price = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    durationMinutes = table.Column<int>(type: "integer", nullable: false),
                    isActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    createdAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Cuts", x => x.id);
                    table.CheckConstraint("CK_Cuts_durationMinutes", "\"durationMinutes\" > 0");
                    table.CheckConstraint("CK_Cuts_price", "\"price\" >= 0");
                });

            migrationBuilder.CreateTable(
                name: "Holidays",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    date = table.Column<DateOnly>(type: "date", nullable: false),
                    name = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    type = table.Column<HolidayType>(type: "holiday_type", nullable: false),
                    source = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    isActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    createdAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Holidays", x => x.id);
                    table.CheckConstraint("CK_Holidays_source", "\"source\" IN ('api', 'manual')");
                });

            migrationBuilder.CreateTable(
                name: "ProductCategories",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    createdAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductCategories", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "Suppliers",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    contactName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    phone = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: true),
                    taxId = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    notes = table.Column<string>(type: "text", nullable: true),
                    isActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    createdAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Suppliers", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false),
                    password = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    phone = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    imageUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    branchId = table.Column<int>(type: "integer", nullable: true),
                    startDate = table.Column<DateOnly>(type: "date", nullable: true),
                    role = table.Column<UserRole>(type: "user_role", nullable: false),
                    isActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    emailVerified = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    createdAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.id);
                    table.CheckConstraint("CK_Users_branch_by_role", "(\"role\" = 'barber' AND \"branchId\" IS NOT NULL) OR (\"role\" <> 'barber' AND \"branchId\" IS NULL)");
                    table.CheckConstraint("CK_Users_email_normalized", "\"email\" = lower(btrim(\"email\"))");
                    table.ForeignKey(
                        name: "FK_Users_Branches_branchId",
                        column: x => x.branchId,
                        principalTable: "Branches",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Products",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    categoryId = table.Column<int>(type: "integer", nullable: false),
                    name = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    sku = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    barcode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    salePrice = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: false),
                    cost = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: false, defaultValue: 0m),
                    stock = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    minStock = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    reorderQuantity = table.Column<int>(type: "integer", nullable: true),
                    defaultSupplierId = table.Column<int>(type: "integer", nullable: true),
                    imageUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    isActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    createdAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Products", x => x.id);
                    table.CheckConstraint("CK_Products_cost", "\"cost\" >= 0");
                    table.CheckConstraint("CK_Products_minStock", "\"minStock\" >= 0");
                    table.CheckConstraint("CK_Products_reorderQuantity", "\"reorderQuantity\" IS NULL OR \"reorderQuantity\" > 0");
                    table.CheckConstraint("CK_Products_salePrice", "\"salePrice\" >= 0");
                    table.CheckConstraint("CK_Products_stock", "\"stock\" >= 0");
                    table.ForeignKey(
                        name: "FK_Products_ProductCategories_categoryId",
                        column: x => x.categoryId,
                        principalTable: "ProductCategories",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Products_Suppliers_defaultSupplierId",
                        column: x => x.defaultSupplierId,
                        principalTable: "Suppliers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "Appointments",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    appointmentDate = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    endDate = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<AppointmentStatus>(type: "appointment_status", nullable: false),
                    clientId = table.Column<int>(type: "integer", nullable: true),
                    clientName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    barberId = table.Column<int>(type: "integer", nullable: false),
                    cutId = table.Column<int>(type: "integer", nullable: false),
                    branchId = table.Column<int>(type: "integer", nullable: false),
                    price = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: false),
                    paymentStatus = table.Column<PaymentStatus>(type: "payment_status", nullable: false),
                    paymentMethod = table.Column<PaymentMethod>(type: "payment_method", nullable: true),
                    paymentExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    paidAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    createdByRole = table.Column<UserRole>(type: "user_role", nullable: false),
                    createdById = table.Column<int>(type: "integer", nullable: true),
                    cancelledAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    cancelledById = table.Column<int>(type: "integer", nullable: true),
                    cancelledByRole = table.Column<UserRole>(type: "user_role", nullable: true),
                    cancellationReason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    createdAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Appointments", x => x.id);
                    table.CheckConstraint("CK_Appointments_client", "\"clientId\" IS NOT NULL OR \"clientName\" IS NOT NULL");
                    table.CheckConstraint("CK_Appointments_endDate", "\"endDate\" > \"appointmentDate\"");
                    table.CheckConstraint("CK_Appointments_price", "\"price\" >= 0");
                    table.ForeignKey(
                        name: "FK_Appointments_Branches_branchId",
                        column: x => x.branchId,
                        principalTable: "Branches",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Appointments_Cuts_cutId",
                        column: x => x.cutId,
                        principalTable: "Cuts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Appointments_Users_barberId",
                        column: x => x.barberId,
                        principalTable: "Users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Appointments_Users_cancelledById",
                        column: x => x.cancelledById,
                        principalTable: "Users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Appointments_Users_clientId",
                        column: x => x.clientId,
                        principalTable: "Users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Appointments_Users_createdById",
                        column: x => x.createdById,
                        principalTable: "Users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "AuthTokens",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    userId = table.Column<int>(type: "integer", nullable: false),
                    type = table.Column<AuthTokenType>(type: "auth_token_type", nullable: false),
                    tokenHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    expiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    usedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    createdAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuthTokens", x => x.id);
                    table.ForeignKey(
                        name: "FK_AuthTokens_Users_userId",
                        column: x => x.userId,
                        principalTable: "Users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AvailableSlots",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    barberId = table.Column<int>(type: "integer", nullable: false),
                    startAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    origin = table.Column<SlotOrigin>(type: "slot_origin", nullable: false),
                    createdAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    createdById = table.Column<int>(type: "integer", nullable: true),
                    deletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    deletedById = table.Column<int>(type: "integer", nullable: true),
                    deleteReason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AvailableSlots", x => x.id);
                    table.ForeignKey(
                        name: "FK_AvailableSlots_Users_barberId",
                        column: x => x.barberId,
                        principalTable: "Users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AvailableSlots_Users_createdById",
                        column: x => x.createdById,
                        principalTable: "Users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_AvailableSlots_Users_deletedById",
                        column: x => x.deletedById,
                        principalTable: "Users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "Orders",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    clientId = table.Column<int>(type: "integer", nullable: true),
                    branchId = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<OrderStatus>(type: "order_status", nullable: false),
                    total = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: false),
                    refundStatus = table.Column<RefundStatus>(type: "refund_status", nullable: false),
                    paymentExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    cancellationReason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    cancelledById = table.Column<int>(type: "integer", nullable: true),
                    createdAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Orders", x => x.id);
                    table.CheckConstraint("CK_Orders_total", "\"total\" >= 0");
                    table.ForeignKey(
                        name: "FK_Orders_Branches_branchId",
                        column: x => x.branchId,
                        principalTable: "Branches",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Orders_Users_cancelledById",
                        column: x => x.cancelledById,
                        principalTable: "Users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Orders_Users_clientId",
                        column: x => x.clientId,
                        principalTable: "Users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "PriceAdjustments",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    appliedById = table.Column<int>(type: "integer", nullable: false),
                    appliedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    source = table.Column<PriceAdjustmentSource>(type: "price_adjustment_source", nullable: false),
                    percentage = table.Column<decimal>(type: "numeric(7,4)", precision: 7, scale: 4, nullable: false),
                    periodFrom = table.Column<DateOnly>(type: "date", nullable: true),
                    periodTo = table.Column<DateOnly>(type: "date", nullable: true),
                    scopeType = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    scopeDescription = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    roundingMode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    includesCuts = table.Column<bool>(type: "boolean", nullable: false),
                    itemsCount = table.Column<int>(type: "integer", nullable: false),
                    revertedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revertedById = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PriceAdjustments", x => x.id);
                    table.CheckConstraint("CK_PriceAdjustments_scopeType", "\"scopeType\" IN ('all', 'category', 'selection')");
                    table.ForeignKey(
                        name: "FK_PriceAdjustments_Users_appliedById",
                        column: x => x.appliedById,
                        principalTable: "Users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PriceAdjustments_Users_revertedById",
                        column: x => x.revertedById,
                        principalTable: "Users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Notifications",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    userId = table.Column<int>(type: "integer", nullable: false),
                    appointmentId = table.Column<int>(type: "integer", nullable: true),
                    type = table.Column<NotificationType>(type: "notification_type", nullable: false),
                    channel = table.Column<NotificationChannel>(type: "notification_channel", nullable: false),
                    status = table.Column<NotificationStatus>(type: "notification_status", nullable: false),
                    scheduledAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    sentAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    createdAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Notifications", x => x.id);
                    table.ForeignKey(
                        name: "FK_Notifications_Appointments_appointmentId",
                        column: x => x.appointmentId,
                        principalTable: "Appointments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Notifications_Users_userId",
                        column: x => x.userId,
                        principalTable: "Users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Reviews",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    appointmentId = table.Column<int>(type: "integer", nullable: false),
                    ratingBarber = table.Column<short>(type: "smallint", nullable: false),
                    ratingCut = table.Column<short>(type: "smallint", nullable: false),
                    comment = table.Column<string>(type: "text", nullable: true),
                    createdAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Reviews", x => x.id);
                    table.CheckConstraint("CK_Reviews_ratingBarber", "\"ratingBarber\" BETWEEN 1 AND 5");
                    table.CheckConstraint("CK_Reviews_ratingCut", "\"ratingCut\" BETWEEN 1 AND 5");
                    table.ForeignKey(
                        name: "FK_Reviews_Appointments_appointmentId",
                        column: x => x.appointmentId,
                        principalTable: "Appointments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "OrderItems",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    orderId = table.Column<int>(type: "integer", nullable: false),
                    productId = table.Column<int>(type: "integer", nullable: false),
                    quantity = table.Column<int>(type: "integer", nullable: false),
                    unitPrice = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: false),
                    unitCost = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrderItems", x => x.id);
                    table.CheckConstraint("CK_OrderItems_quantity", "\"quantity\" > 0");
                    table.CheckConstraint("CK_OrderItems_unitPrice", "\"unitPrice\" >= 0");
                    table.ForeignKey(
                        name: "FK_OrderItems_Orders_orderId",
                        column: x => x.orderId,
                        principalTable: "Orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_OrderItems_Products_productId",
                        column: x => x.productId,
                        principalTable: "Products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Payments",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    orderId = table.Column<int>(type: "integer", nullable: true),
                    appointmentId = table.Column<int>(type: "integer", nullable: true),
                    amount = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: false),
                    method = table.Column<PaymentMethod>(type: "payment_method", nullable: false),
                    status = table.Column<TransactionStatus>(type: "transaction_status", nullable: false),
                    externalId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    preferenceId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    paidAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    refundedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    createdAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Payments", x => x.id);
                    table.CheckConstraint("CK_Payments_amount", "\"amount\" > 0");
                    table.CheckConstraint("CK_Payments_single_target", "(\"orderId\" IS NOT NULL AND \"appointmentId\" IS NULL) OR (\"orderId\" IS NULL AND \"appointmentId\" IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_Payments_Appointments_appointmentId",
                        column: x => x.appointmentId,
                        principalTable: "Appointments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Payments_Orders_orderId",
                        column: x => x.orderId,
                        principalTable: "Orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "StockMovements",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    productId = table.Column<int>(type: "integer", nullable: false),
                    type = table.Column<StockMovementType>(type: "stock_movement_type", nullable: false),
                    quantity = table.Column<int>(type: "integer", nullable: false),
                    unitCost = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: true),
                    supplierId = table.Column<int>(type: "integer", nullable: true),
                    documentNumber = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    occurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    balanceAfter = table.Column<int>(type: "integer", nullable: false),
                    orderId = table.Column<int>(type: "integer", nullable: true),
                    userId = table.Column<int>(type: "integer", nullable: false),
                    reason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    createdAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StockMovements", x => x.id);
                    table.CheckConstraint("CK_StockMovements_adjustment_reason", "\"type\" <> 'adjustment' OR \"reason\" IS NOT NULL");
                    table.CheckConstraint("CK_StockMovements_balanceAfter", "\"balanceAfter\" >= 0");
                    table.CheckConstraint("CK_StockMovements_in_cost", "\"type\" <> 'in' OR \"unitCost\" IS NOT NULL");
                    table.CheckConstraint("CK_StockMovements_quantity", "\"quantity\" <> 0");
                    table.ForeignKey(
                        name: "FK_StockMovements_Orders_orderId",
                        column: x => x.orderId,
                        principalTable: "Orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StockMovements_Products_productId",
                        column: x => x.productId,
                        principalTable: "Products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StockMovements_Suppliers_supplierId",
                        column: x => x.supplierId,
                        principalTable: "Suppliers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StockMovements_Users_userId",
                        column: x => x.userId,
                        principalTable: "Users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PriceHistory",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    entityType = table.Column<PriceEntityType>(type: "price_entity_type", nullable: false),
                    entityId = table.Column<int>(type: "integer", nullable: false),
                    field = table.Column<PriceField>(type: "price_field", nullable: false),
                    oldPrice = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: false),
                    newPrice = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: false),
                    adjustmentId = table.Column<int>(type: "integer", nullable: true),
                    reason = table.Column<PriceChangeReason>(type: "price_change_reason", nullable: false),
                    changedById = table.Column<int>(type: "integer", nullable: false),
                    changedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PriceHistory", x => x.id);
                    table.ForeignKey(
                        name: "FK_PriceHistory_PriceAdjustments_adjustmentId",
                        column: x => x.adjustmentId,
                        principalTable: "PriceAdjustments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PriceHistory_Users_changedById",
                        column: x => x.changedById,
                        principalTable: "Users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Appointments_barberId_appointmentDate",
                table: "Appointments",
                columns: new[] { "barberId", "appointmentDate" });

            migrationBuilder.CreateIndex(
                name: "IX_Appointments_branchId",
                table: "Appointments",
                column: "branchId");

            migrationBuilder.CreateIndex(
                name: "IX_Appointments_cancelledById",
                table: "Appointments",
                column: "cancelledById");

            migrationBuilder.CreateIndex(
                name: "IX_Appointments_clientId_appointmentDate",
                table: "Appointments",
                columns: new[] { "clientId", "appointmentDate" });

            migrationBuilder.CreateIndex(
                name: "IX_Appointments_createdById",
                table: "Appointments",
                column: "createdById");

            migrationBuilder.CreateIndex(
                name: "IX_Appointments_cutId",
                table: "Appointments",
                column: "cutId");

            migrationBuilder.CreateIndex(
                name: "IX_Appointments_status_paymentExpiresAt",
                table: "Appointments",
                columns: new[] { "status", "paymentExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AuthTokens_tokenHash",
                table: "AuthTokens",
                column: "tokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuthTokens_userId_type",
                table: "AuthTokens",
                columns: new[] { "userId", "type" });

            migrationBuilder.CreateIndex(
                name: "IX_AvailableSlots_barberId_startAt",
                table: "AvailableSlots",
                columns: new[] { "barberId", "startAt" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AvailableSlots_createdById",
                table: "AvailableSlots",
                column: "createdById");

            migrationBuilder.CreateIndex(
                name: "IX_AvailableSlots_deletedById",
                table: "AvailableSlots",
                column: "deletedById");

            migrationBuilder.CreateIndex(
                name: "IX_AvailableSlots_startAt",
                table: "AvailableSlots",
                column: "startAt");

            migrationBuilder.CreateIndex(
                name: "IX_Holidays_date",
                table: "Holidays",
                column: "date",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_appointmentId_type",
                table: "Notifications",
                columns: new[] { "appointmentId", "type" });

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_status_scheduledAt",
                table: "Notifications",
                columns: new[] { "status", "scheduledAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_userId",
                table: "Notifications",
                column: "userId");

            migrationBuilder.CreateIndex(
                name: "IX_OrderItems_orderId_productId",
                table: "OrderItems",
                columns: new[] { "orderId", "productId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OrderItems_productId",
                table: "OrderItems",
                column: "productId");

            migrationBuilder.CreateIndex(
                name: "IX_Orders_branchId",
                table: "Orders",
                column: "branchId");

            migrationBuilder.CreateIndex(
                name: "IX_Orders_cancelledById",
                table: "Orders",
                column: "cancelledById");

            migrationBuilder.CreateIndex(
                name: "IX_Orders_clientId",
                table: "Orders",
                column: "clientId");

            migrationBuilder.CreateIndex(
                name: "IX_Orders_status_paymentExpiresAt",
                table: "Orders",
                columns: new[] { "status", "paymentExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Payments_appointmentId",
                table: "Payments",
                column: "appointmentId");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_externalId",
                table: "Payments",
                column: "externalId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Payments_orderId",
                table: "Payments",
                column: "orderId");

            migrationBuilder.CreateIndex(
                name: "IX_PriceAdjustments_appliedById",
                table: "PriceAdjustments",
                column: "appliedById");

            migrationBuilder.CreateIndex(
                name: "IX_PriceAdjustments_revertedById",
                table: "PriceAdjustments",
                column: "revertedById");

            migrationBuilder.CreateIndex(
                name: "IX_PriceHistory_adjustmentId",
                table: "PriceHistory",
                column: "adjustmentId");

            migrationBuilder.CreateIndex(
                name: "IX_PriceHistory_changedById",
                table: "PriceHistory",
                column: "changedById");

            migrationBuilder.CreateIndex(
                name: "IX_PriceHistory_entityType_entityId_changedAt",
                table: "PriceHistory",
                columns: new[] { "entityType", "entityId", "changedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ProductCategories_name",
                table: "ProductCategories",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Products_categoryId",
                table: "Products",
                column: "categoryId");

            migrationBuilder.CreateIndex(
                name: "IX_Products_defaultSupplierId",
                table: "Products",
                column: "defaultSupplierId");

            migrationBuilder.CreateIndex(
                name: "IX_Products_sku",
                table: "Products",
                column: "sku",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Reviews_appointmentId",
                table: "Reviews",
                column: "appointmentId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StockMovements_orderId",
                table: "StockMovements",
                column: "orderId");

            migrationBuilder.CreateIndex(
                name: "IX_StockMovements_productId_occurredAt",
                table: "StockMovements",
                columns: new[] { "productId", "occurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_StockMovements_supplierId",
                table: "StockMovements",
                column: "supplierId");

            migrationBuilder.CreateIndex(
                name: "IX_StockMovements_userId",
                table: "StockMovements",
                column: "userId");

            migrationBuilder.CreateIndex(
                name: "IX_Users_branchId",
                table: "Users",
                column: "branchId");

            migrationBuilder.CreateIndex(
                name: "IX_Users_email",
                table: "Users",
                column: "email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Users_role_isActive",
                table: "Users",
                columns: new[] { "role", "isActive" });

            InitialSchemaSql.Apply(migrationBuilder);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuthTokens");

            migrationBuilder.DropTable(
                name: "AvailableSlots");

            migrationBuilder.DropTable(
                name: "Holidays");

            migrationBuilder.DropTable(
                name: "Notifications");

            migrationBuilder.DropTable(
                name: "OrderItems");

            migrationBuilder.DropTable(
                name: "Payments");

            migrationBuilder.DropTable(
                name: "PriceHistory");

            migrationBuilder.DropTable(
                name: "Reviews");

            migrationBuilder.DropTable(
                name: "StockMovements");

            migrationBuilder.DropTable(
                name: "PriceAdjustments");

            migrationBuilder.DropTable(
                name: "Appointments");

            migrationBuilder.DropTable(
                name: "Orders");

            migrationBuilder.DropTable(
                name: "Products");

            migrationBuilder.DropTable(
                name: "Cuts");

            migrationBuilder.DropTable(
                name: "Users");

            migrationBuilder.DropTable(
                name: "ProductCategories");

            migrationBuilder.DropTable(
                name: "Suppliers");

            migrationBuilder.DropTable(
                name: "Branches");
        }
    }
}
