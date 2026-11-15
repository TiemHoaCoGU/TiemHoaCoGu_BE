IF OBJECT_ID(N'[__EFMigrationsHistory]') IS NULL
BEGIN
    CREATE TABLE [__EFMigrationsHistory] (
        [MigrationId] nvarchar(150) NOT NULL,
        [ProductVersion] nvarchar(32) NOT NULL,
        CONSTRAINT [PK___EFMigrationsHistory] PRIMARY KEY ([MigrationId])
    );
END;
GO

BEGIN TRANSACTION;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261022090137_InitialProduction'
)
BEGIN
    CREATE TABLE [Categories] (
        [Id] uniqueidentifier NOT NULL,
        [Code] nvarchar(450) NOT NULL,
        [Name] nvarchar(max) NOT NULL,
        [Description] nvarchar(max) NOT NULL,
        CONSTRAINT [PK_Categories] PRIMARY KEY ([Id])
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261022090137_InitialProduction'
)
BEGIN
    CREATE TABLE [Customers] (
        [Id] uniqueidentifier NOT NULL,
        [FullName] nvarchar(max) NOT NULL,
        [PhoneNumber] nvarchar(450) NOT NULL,
        [Email] nvarchar(450) NOT NULL,
        [PasswordHash] nvarchar(max) NOT NULL,
        [Role] nvarchar(max) NOT NULL,
        [IsGuest] bit NOT NULL,
        [CreatedAt] datetime2 NOT NULL,
        CONSTRAINT [PK_Customers] PRIMARY KEY ([Id])
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261022090137_InitialProduction'
)
BEGIN
    CREATE TABLE [Instructors] (
        [Id] uniqueidentifier NOT NULL,
        [FullName] nvarchar(max) NOT NULL,
        [Bio] nvarchar(max) NOT NULL,
        [AvatarUrl] nvarchar(max) NOT NULL,
        [PhoneNumber] nvarchar(max) NOT NULL,
        [Email] nvarchar(max) NOT NULL,
        CONSTRAINT [PK_Instructors] PRIMARY KEY ([Id])
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261022090137_InitialProduction'
)
BEGIN
    CREATE TABLE [Vouchers] (
        [Id] uniqueidentifier NOT NULL,
        [Code] nvarchar(450) NOT NULL,
        [Description] nvarchar(max) NOT NULL,
        [DiscountType] nvarchar(max) NOT NULL,
        [DiscountValue] decimal(18,2) NOT NULL,
        [MinOrderAmount] decimal(18,2) NOT NULL,
        [MaxDiscountAmount] decimal(18,2) NULL,
        [StartDate] datetime2 NOT NULL,
        [EndDate] datetime2 NOT NULL,
        [UsageLimit] int NOT NULL,
        [UsedCount] int NOT NULL,
        [IsActive] bit NOT NULL,
        CONSTRAINT [PK_Vouchers] PRIMARY KEY ([Id])
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261022090137_InitialProduction'
)
BEGIN
    CREATE TABLE [Products] (
        [Id] uniqueidentifier NOT NULL,
        [CategoryId] uniqueidentifier NOT NULL,
        [Code] nvarchar(450) NOT NULL,
        [Name] nvarchar(max) NOT NULL,
        [Price] decimal(18,2) NOT NULL,
        [OriginalPrice] decimal(18,2) NULL,
        [ThumbnailUrl] nvarchar(max) NOT NULL,
        [Description] nvarchar(max) NOT NULL,
        [Badge] nvarchar(max) NOT NULL,
        [Section] nvarchar(max) NOT NULL,
        [IsCustomizable] bit NOT NULL,
        [IsPreOrder] bit NOT NULL,
        [IsActive] bit NOT NULL,
        [CreatedAt] datetime2 NOT NULL,
        CONSTRAINT [PK_Products] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_Product_Price] CHECK (Price >= 0 AND (OriginalPrice IS NULL OR OriginalPrice >= 0)),
        CONSTRAINT [FK_Products_Categories_CategoryId] FOREIGN KEY ([CategoryId]) REFERENCES [Categories] ([Id]) ON DELETE NO ACTION
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261022090137_InitialProduction'
)
BEGIN
    CREATE TABLE [Addresses] (
        [Id] uniqueidentifier NOT NULL,
        [CustomerId] uniqueidentifier NOT NULL,
        [ReceiverName] nvarchar(max) NOT NULL,
        [ReceiverPhone] nvarchar(max) NOT NULL,
        [DetailAddress] nvarchar(max) NOT NULL,
        [Ward] nvarchar(max) NOT NULL,
        [District] nvarchar(max) NOT NULL,
        [Province] nvarchar(max) NOT NULL,
        [IsDefault] bit NOT NULL,
        CONSTRAINT [PK_Addresses] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_Addresses_Customers_CustomerId] FOREIGN KEY ([CustomerId]) REFERENCES [Customers] ([Id]) ON DELETE NO ACTION
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261022090137_InitialProduction'
)
BEGIN
    CREATE TABLE [Workshops] (
        [Id] uniqueidentifier NOT NULL,
        [InstructorId] uniqueidentifier NULL,
        [Code] nvarchar(450) NOT NULL,
        [Name] nvarchar(max) NOT NULL,
        [ShortName] nvarchar(max) NOT NULL,
        [Category] nvarchar(max) NOT NULL,
        [BasePrice] decimal(18,2) NOT NULL,
        [Duration] int NOT NULL,
        [MaxCapacity] int NOT NULL,
        [Description] nvarchar(max) NOT NULL,
        [LeadContent] nvarchar(max) NOT NULL,
        [GoalsJson] nvarchar(max) NOT NULL,
        [IncludesJson] nvarchar(max) NOT NULL,
        [ForWho] nvarchar(max) NOT NULL,
        [IsFeatured] bit NOT NULL,
        [IsActive] bit NOT NULL,
        CONSTRAINT [PK_Workshops] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_Workshops_Instructors_InstructorId] FOREIGN KEY ([InstructorId]) REFERENCES [Instructors] ([Id]) ON DELETE NO ACTION
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261022090137_InitialProduction'
)
BEGIN
    CREATE TABLE [Orders] (
        [Id] uniqueidentifier NOT NULL,
        [IdempotencyKey] nvarchar(100) NULL,
        [RequestFingerprint] nvarchar(64) NULL,
        [CustomerId] uniqueidentifier NULL,
        [VoucherId] uniqueidentifier NULL,
        [OrderCode] nvarchar(450) NOT NULL,
        [SubTotal] decimal(18,2) NOT NULL,
        [DiscountAmount] decimal(18,2) NOT NULL,
        [ShippingFee] decimal(18,2) NOT NULL,
        [TotalAmount] decimal(18,2) NOT NULL,
        [OrderStatus] nvarchar(max) NOT NULL,
        [PaymentStatus] nvarchar(max) NOT NULL,
        [Note] nvarchar(max) NOT NULL,
        [ShippingAddress] nvarchar(max) NOT NULL,
        [CreatedAt] datetime2 NOT NULL,
        [ConcurrencyToken] uniqueidentifier NOT NULL,
        CONSTRAINT [PK_Orders] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_Order_Amounts] CHECK (SubTotal >= 0 AND DiscountAmount >= 0 AND ShippingFee >= 0 AND TotalAmount >= 0),
        CONSTRAINT [FK_Orders_Customers_CustomerId] FOREIGN KEY ([CustomerId]) REFERENCES [Customers] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_Orders_Vouchers_VoucherId] FOREIGN KEY ([VoucherId]) REFERENCES [Vouchers] ([Id]) ON DELETE NO ACTION
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261022090137_InitialProduction'
)
BEGIN
    CREATE TABLE [Inventories] (
        [Id] uniqueidentifier NOT NULL,
        [ProductId] uniqueidentifier NOT NULL,
        [QuantityAvailable] int NOT NULL,
        [QuantityReserved] int NOT NULL,
        [LastUpdated] datetime2 NOT NULL,
        [ConcurrencyToken] uniqueidentifier NOT NULL,
        CONSTRAINT [PK_Inventories] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_Inventory_Quantities] CHECK (QuantityAvailable >= 0 AND QuantityReserved >= 0),
        CONSTRAINT [FK_Inventories_Products_ProductId] FOREIGN KEY ([ProductId]) REFERENCES [Products] ([Id]) ON DELETE NO ACTION
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261022090137_InitialProduction'
)
BEGIN
    CREATE TABLE [ProductImages] (
        [Id] uniqueidentifier NOT NULL,
        [ProductId] uniqueidentifier NOT NULL,
        [ImageUrl] nvarchar(max) NOT NULL,
        [DisplayOrder] int NOT NULL,
        CONSTRAINT [PK_ProductImages] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_ProductImages_Products_ProductId] FOREIGN KEY ([ProductId]) REFERENCES [Products] ([Id]) ON DELETE CASCADE
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261022090137_InitialProduction'
)
BEGIN
    CREATE TABLE [ShoppingCartItems] (
        [Id] uniqueidentifier NOT NULL,
        [CustomerId] uniqueidentifier NOT NULL,
        [ProductId] uniqueidentifier NOT NULL,
        [Quantity] int NOT NULL,
        [CustomNote] nvarchar(max) NOT NULL,
        [ReferenceImageUrl] nvarchar(max) NOT NULL,
        [CreatedAt] datetime2 NOT NULL,
        CONSTRAINT [PK_ShoppingCartItems] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_ShoppingCartItems_Customers_CustomerId] FOREIGN KEY ([CustomerId]) REFERENCES [Customers] ([Id]) ON DELETE CASCADE,
        CONSTRAINT [FK_ShoppingCartItems_Products_ProductId] FOREIGN KEY ([ProductId]) REFERENCES [Products] ([Id]) ON DELETE NO ACTION
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261022090137_InitialProduction'
)
BEGIN
    CREATE TABLE [WorkshopImages] (
        [Id] uniqueidentifier NOT NULL,
        [WorkshopId] uniqueidentifier NOT NULL,
        [ImageUrl] nvarchar(max) NOT NULL,
        [Caption] nvarchar(max) NOT NULL,
        [DisplayOrder] int NOT NULL,
        CONSTRAINT [PK_WorkshopImages] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_WorkshopImages_Workshops_WorkshopId] FOREIGN KEY ([WorkshopId]) REFERENCES [Workshops] ([Id]) ON DELETE CASCADE
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261022090137_InitialProduction'
)
BEGIN
    CREATE TABLE [WorkshopSessions] (
        [Id] uniqueidentifier NOT NULL,
        [WorkshopId] uniqueidentifier NOT NULL,
        [SessionDate] datetime2 NOT NULL,
        [StartTime] time NOT NULL,
        [EndTime] time NOT NULL,
        [Location] nvarchar(max) NOT NULL,
        [Capacity] int NOT NULL,
        [BookedSlots] int NOT NULL,
        [SessionStatus] nvarchar(max) NOT NULL,
        [ConcurrencyToken] uniqueidentifier NOT NULL,
        CONSTRAINT [PK_WorkshopSessions] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_Session_Capacity] CHECK (Capacity > 0 AND BookedSlots >= 0 AND BookedSlots <= Capacity),
        CONSTRAINT [FK_WorkshopSessions_Workshops_WorkshopId] FOREIGN KEY ([WorkshopId]) REFERENCES [Workshops] ([Id]) ON DELETE NO ACTION
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261022090137_InitialProduction'
)
BEGIN
    CREATE TABLE [Deliveries] (
        [Id] uniqueidentifier NOT NULL,
        [OrderId] uniqueidentifier NOT NULL,
        [DeliveryCode] nvarchar(450) NOT NULL,
        [ShipperUnit] nvarchar(max) NOT NULL,
        [TrackingNumber] nvarchar(max) NOT NULL,
        [ShippingCost] decimal(18,2) NOT NULL,
        [DeliveryStatus] nvarchar(max) NOT NULL,
        [ShippedAt] datetime2 NULL,
        [DeliveredAt] datetime2 NULL,
        [Note] nvarchar(max) NOT NULL,
        CONSTRAINT [PK_Deliveries] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_Deliveries_Orders_OrderId] FOREIGN KEY ([OrderId]) REFERENCES [Orders] ([Id]) ON DELETE NO ACTION
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261022090137_InitialProduction'
)
BEGIN
    CREATE TABLE [OrderItems] (
        [Id] uniqueidentifier NOT NULL,
        [OrderId] uniqueidentifier NOT NULL,
        [ProductId] uniqueidentifier NOT NULL,
        [Quantity] int NOT NULL,
        [UnitPrice] decimal(18,2) NOT NULL,
        [FinalPrice] decimal(18,2) NOT NULL,
        [CustomNote] nvarchar(max) NOT NULL,
        [ReferenceImageUrl] nvarchar(max) NOT NULL,
        [AdminPriceNote] nvarchar(max) NOT NULL,
        CONSTRAINT [PK_OrderItems] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_OrderItem_Quantities] CHECK (Quantity > 0 AND UnitPrice >= 0 AND FinalPrice >= 0),
        CONSTRAINT [FK_OrderItems_Orders_OrderId] FOREIGN KEY ([OrderId]) REFERENCES [Orders] ([Id]) ON DELETE CASCADE,
        CONSTRAINT [FK_OrderItems_Products_ProductId] FOREIGN KEY ([ProductId]) REFERENCES [Products] ([Id]) ON DELETE NO ACTION
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261022090137_InitialProduction'
)
BEGIN
    CREATE TABLE [WorkshopBookings] (
        [Id] uniqueidentifier NOT NULL,
        [IdempotencyKey] nvarchar(100) NULL,
        [RequestFingerprint] nvarchar(64) NULL,
        [SessionId] uniqueidentifier NOT NULL,
        [CustomerId] uniqueidentifier NULL,
        [BookingCode] nvarchar(450) NOT NULL,
        [CustomerName] nvarchar(max) NOT NULL,
        [CustomerPhone] nvarchar(max) NOT NULL,
        [CustomerEmail] nvarchar(max) NOT NULL,
        [TicketCount] int NOT NULL,
        [TotalAmount] decimal(18,2) NOT NULL,
        [DepositAmount] decimal(18,2) NOT NULL,
        [RemainingAmount] decimal(18,2) NOT NULL,
        [BookingStatus] nvarchar(450) NOT NULL,
        [HoldExpiresAt] datetime2 NOT NULL,
        [CheckedInAt] datetime2 NULL,
        [CancelReason] nvarchar(max) NOT NULL,
        [CreatedAt] datetime2 NOT NULL,
        [ConcurrencyToken] uniqueidentifier NOT NULL,
        CONSTRAINT [PK_WorkshopBookings] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_Booking_Tickets] CHECK (TicketCount > 0 AND TotalAmount >= 0 AND DepositAmount >= 0 AND RemainingAmount >= 0),
        CONSTRAINT [FK_WorkshopBookings_Customers_CustomerId] FOREIGN KEY ([CustomerId]) REFERENCES [Customers] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_WorkshopBookings_WorkshopSessions_SessionId] FOREIGN KEY ([SessionId]) REFERENCES [WorkshopSessions] ([Id]) ON DELETE NO ACTION
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261022090137_InitialProduction'
)
BEGIN
    CREATE TABLE [DeliveryItems] (
        [Id] uniqueidentifier NOT NULL,
        [DeliveryId] uniqueidentifier NOT NULL,
        [OrderItemId] uniqueidentifier NOT NULL,
        [ShippedQuantity] int NOT NULL,
        CONSTRAINT [PK_DeliveryItems] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_DeliveryItems_Deliveries_DeliveryId] FOREIGN KEY ([DeliveryId]) REFERENCES [Deliveries] ([Id]) ON DELETE CASCADE,
        CONSTRAINT [FK_DeliveryItems_OrderItems_OrderItemId] FOREIGN KEY ([OrderItemId]) REFERENCES [OrderItems] ([Id]) ON DELETE NO ACTION
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261022090137_InitialProduction'
)
BEGIN
    CREATE TABLE [Payments] (
        [Id] uniqueidentifier NOT NULL,
        [RecordedBy] uniqueidentifier NULL,
        [OrderId] uniqueidentifier NULL,
        [BookingId] uniqueidentifier NULL,
        [TransactionCode] nvarchar(100) NOT NULL,
        [PaymentType] nvarchar(max) NOT NULL,
        [PaymentMethod] nvarchar(max) NOT NULL,
        [Amount] decimal(18,2) NOT NULL,
        [PaymentStatus] nvarchar(max) NOT NULL,
        [PaidAt] datetime2 NULL,
        [PaymentGatewayResponse] nvarchar(max) NOT NULL,
        CONSTRAINT [PK_Payments] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_Payment_Amount] CHECK (Amount >= 0 AND ((OrderId IS NOT NULL AND BookingId IS NULL) OR (OrderId IS NULL AND BookingId IS NOT NULL))),
        CONSTRAINT [FK_Payments_Orders_OrderId] FOREIGN KEY ([OrderId]) REFERENCES [Orders] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_Payments_WorkshopBookings_BookingId] FOREIGN KEY ([BookingId]) REFERENCES [WorkshopBookings] ([Id]) ON DELETE NO ACTION
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261022090137_InitialProduction'
)
BEGIN
    CREATE TABLE [Refunds] (
        [Id] uniqueidentifier NOT NULL,
        [ConcurrencyToken] uniqueidentifier NOT NULL,
        [TransactionCode] nvarchar(100) NULL,
        [RequestedAt] datetime2 NOT NULL,
        [BookingId] uniqueidentifier NULL,
        [OrderId] uniqueidentifier NULL,
        [RefundAmount] decimal(18,2) NOT NULL,
        [RefundReason] nvarchar(max) NOT NULL,
        [RefundStatus] nvarchar(max) NOT NULL,
        [ProcessedAt] datetime2 NULL,
        [ApprovedBy] nvarchar(max) NOT NULL,
        CONSTRAINT [PK_Refunds] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_Refund_Target] CHECK (RefundAmount >= 0 AND ((OrderId IS NOT NULL AND BookingId IS NULL) OR (OrderId IS NULL AND BookingId IS NOT NULL))),
        CONSTRAINT [FK_Refunds_Orders_OrderId] FOREIGN KEY ([OrderId]) REFERENCES [Orders] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_Refunds_WorkshopBookings_BookingId] FOREIGN KEY ([BookingId]) REFERENCES [WorkshopBookings] ([Id]) ON DELETE NO ACTION
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261022090137_InitialProduction'
)
BEGIN
    CREATE INDEX [IX_Addresses_CustomerId] ON [Addresses] ([CustomerId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261022090137_InitialProduction'
)
BEGIN
    CREATE UNIQUE INDEX [IX_Categories_Code] ON [Categories] ([Code]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261022090137_InitialProduction'
)
BEGIN
    CREATE UNIQUE INDEX [IX_Customers_Email] ON [Customers] ([Email]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261022090137_InitialProduction'
)
BEGIN
    CREATE INDEX [IX_Customers_PhoneNumber] ON [Customers] ([PhoneNumber]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261022090137_InitialProduction'
)
BEGIN
    CREATE UNIQUE INDEX [IX_Deliveries_DeliveryCode] ON [Deliveries] ([DeliveryCode]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261022090137_InitialProduction'
)
BEGIN
    CREATE INDEX [IX_Deliveries_OrderId] ON [Deliveries] ([OrderId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261022090137_InitialProduction'
)
BEGIN
    CREATE INDEX [IX_DeliveryItems_DeliveryId] ON [DeliveryItems] ([DeliveryId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261022090137_InitialProduction'
)
BEGIN
    CREATE INDEX [IX_DeliveryItems_OrderItemId] ON [DeliveryItems] ([OrderItemId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261022090137_InitialProduction'
)
BEGIN
    CREATE UNIQUE INDEX [IX_Inventories_ProductId] ON [Inventories] ([ProductId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261022090137_InitialProduction'
)
BEGIN
    CREATE INDEX [IX_OrderItems_OrderId] ON [OrderItems] ([OrderId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261022090137_InitialProduction'
)
BEGIN
    CREATE INDEX [IX_OrderItems_ProductId] ON [OrderItems] ([ProductId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261022090137_InitialProduction'
)
BEGIN
    CREATE INDEX [IX_Orders_CustomerId_CreatedAt] ON [Orders] ([CustomerId], [CreatedAt]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261022090137_InitialProduction'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [IX_Orders_CustomerId_IdempotencyKey] ON [Orders] ([CustomerId], [IdempotencyKey]) WHERE [CustomerId] IS NOT NULL AND [IdempotencyKey] IS NOT NULL');
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261022090137_InitialProduction'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [IX_Orders_IdempotencyKey] ON [Orders] ([IdempotencyKey]) WHERE [CustomerId] IS NULL AND [IdempotencyKey] IS NOT NULL');
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261022090137_InitialProduction'
)
BEGIN
    CREATE UNIQUE INDEX [IX_Orders_OrderCode] ON [Orders] ([OrderCode]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261022090137_InitialProduction'
)
BEGIN
    CREATE INDEX [IX_Orders_VoucherId] ON [Orders] ([VoucherId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261022090137_InitialProduction'
)
BEGIN
    CREATE INDEX [IX_Payments_BookingId] ON [Payments] ([BookingId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261022090137_InitialProduction'
)
BEGIN
    CREATE INDEX [IX_Payments_OrderId] ON [Payments] ([OrderId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261022090137_InitialProduction'
)
BEGIN
    CREATE UNIQUE INDEX [IX_Payments_TransactionCode] ON [Payments] ([TransactionCode]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261022090137_InitialProduction'
)
BEGIN
    CREATE INDEX [IX_ProductImages_ProductId_DisplayOrder] ON [ProductImages] ([ProductId], [DisplayOrder]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261022090137_InitialProduction'
)
BEGIN
    CREATE INDEX [IX_Products_CategoryId] ON [Products] ([CategoryId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261022090137_InitialProduction'
)
BEGIN
    CREATE UNIQUE INDEX [IX_Products_Code] ON [Products] ([Code]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261022090137_InitialProduction'
)
BEGIN
    CREATE INDEX [IX_Refunds_BookingId] ON [Refunds] ([BookingId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261022090137_InitialProduction'
)
BEGIN
    CREATE INDEX [IX_Refunds_OrderId] ON [Refunds] ([OrderId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261022090137_InitialProduction'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [IX_Refunds_TransactionCode] ON [Refunds] ([TransactionCode]) WHERE [TransactionCode] IS NOT NULL');
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261022090137_InitialProduction'
)
BEGIN
    CREATE UNIQUE INDEX [IX_ShoppingCartItems_CustomerId_ProductId] ON [ShoppingCartItems] ([CustomerId], [ProductId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261022090137_InitialProduction'
)
BEGIN
    CREATE INDEX [IX_ShoppingCartItems_ProductId] ON [ShoppingCartItems] ([ProductId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261022090137_InitialProduction'
)
BEGIN
    CREATE UNIQUE INDEX [IX_Vouchers_Code] ON [Vouchers] ([Code]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261022090137_InitialProduction'
)
BEGIN
    CREATE UNIQUE INDEX [IX_WorkshopBookings_BookingCode] ON [WorkshopBookings] ([BookingCode]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261022090137_InitialProduction'
)
BEGIN
    CREATE INDEX [IX_WorkshopBookings_BookingStatus_HoldExpiresAt] ON [WorkshopBookings] ([BookingStatus], [HoldExpiresAt]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261022090137_InitialProduction'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [IX_WorkshopBookings_CustomerId_IdempotencyKey] ON [WorkshopBookings] ([CustomerId], [IdempotencyKey]) WHERE [CustomerId] IS NOT NULL AND [IdempotencyKey] IS NOT NULL');
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261022090137_InitialProduction'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [IX_WorkshopBookings_IdempotencyKey] ON [WorkshopBookings] ([IdempotencyKey]) WHERE [CustomerId] IS NULL AND [IdempotencyKey] IS NOT NULL');
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261022090137_InitialProduction'
)
BEGIN
    CREATE INDEX [IX_WorkshopBookings_SessionId_BookingStatus] ON [WorkshopBookings] ([SessionId], [BookingStatus]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261022090137_InitialProduction'
)
BEGIN
    CREATE INDEX [IX_WorkshopImages_WorkshopId_DisplayOrder] ON [WorkshopImages] ([WorkshopId], [DisplayOrder]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261022090137_InitialProduction'
)
BEGIN
    CREATE UNIQUE INDEX [IX_Workshops_Code] ON [Workshops] ([Code]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261022090137_InitialProduction'
)
BEGIN
    CREATE INDEX [IX_Workshops_InstructorId] ON [Workshops] ([InstructorId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261022090137_InitialProduction'
)
BEGIN
    CREATE UNIQUE INDEX [IX_WorkshopSessions_WorkshopId_SessionDate_StartTime] ON [WorkshopSessions] ([WorkshopId], [SessionDate], [StartTime]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261022090137_InitialProduction'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261022090137_InitialProduction', N'8.0.22');
END;
GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261022092453_AddAuditLogs'
)
BEGIN
    CREATE TABLE [AuditLogs] (
        [Id] uniqueidentifier NOT NULL,
        [EntityType] nvarchar(80) NOT NULL,
        [EntityId] nvarchar(100) NOT NULL,
        [Action] nvarchar(20) NOT NULL,
        [OldValuesJson] nvarchar(max) NOT NULL,
        [NewValuesJson] nvarchar(max) NOT NULL,
        [OccurredAtUtc] datetime2 NOT NULL,
        [ActorId] uniqueidentifier NULL,
        [ActorRole] nvarchar(40) NULL,
        [TraceId] nvarchar(200) NULL,
        CONSTRAINT [PK_AuditLogs] PRIMARY KEY ([Id])
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261022092453_AddAuditLogs'
)
BEGIN
    CREATE INDEX [IX_AuditLogs_EntityType_EntityId] ON [AuditLogs] ([EntityType], [EntityId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261022092453_AddAuditLogs'
)
BEGIN
    CREATE INDEX [IX_AuditLogs_OccurredAtUtc] ON [AuditLogs] ([OccurredAtUtc]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261022092453_AddAuditLogs'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261022092453_AddAuditLogs', N'8.0.22');
END;
GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261022193130_AddIdentityCmsDefaults'
)
BEGIN
    ALTER TABLE [Customers] ADD [IsActive] bit NOT NULL DEFAULT CAST(1 AS bit);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261022193130_AddIdentityCmsDefaults'
)
BEGIN
    ALTER TABLE [Categories] ADD [ImageUrl] nvarchar(max) NOT NULL DEFAULT N'';
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261022193130_AddIdentityCmsDefaults'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261022193130_AddIdentityCmsDefaults', N'8.0.22');
END;
GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261023033757_CompleteCommerceLifecycle'
)
BEGIN
    ALTER TABLE [Products] ADD [ShippingWeightGrams] int NOT NULL DEFAULT 500;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261023033757_CompleteCommerceLifecycle'
)
BEGIN
    ALTER TABLE [Orders] ADD [ShippingQuoteId] uniqueidentifier NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261023033757_CompleteCommerceLifecycle'
)
BEGIN
    CREATE TABLE [CustomOrderRequests] (
        [Id] uniqueidentifier NOT NULL,
        [CustomerId] uniqueidentifier NOT NULL,
        [ProductId] uniqueidentifier NOT NULL,
        [Quantity] int NOT NULL,
        [Description] nvarchar(4000) NOT NULL,
        [ReferenceImageUrl] nvarchar(2000) NOT NULL,
        [RequestedCompletionDate] datetime2 NULL,
        [ProductionStartedAt] datetime2 NULL,
        [CompletedAt] datetime2 NULL,
        [Status] nvarchar(40) NOT NULL,
        [CreatedAt] datetime2 NOT NULL,
        [UpdatedAt] datetime2 NOT NULL,
        [ConcurrencyToken] uniqueidentifier NOT NULL,
        CONSTRAINT [PK_CustomOrderRequests] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_CustomRequest_Quantity] CHECK (Quantity > 0)
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261023033757_CompleteCommerceLifecycle'
)
BEGIN
    CREATE TABLE [GatewayPaymentIntents] (
        [Id] uniqueidentifier NOT NULL,
        [OrderId] uniqueidentifier NOT NULL,
        [OwnerId] uniqueidentifier NOT NULL,
        [ClientKey] nvarchar(100) NOT NULL,
        [ProviderKey] nvarchar(200) NOT NULL,
        [Amount] decimal(18,2) NOT NULL,
        [Currency] nvarchar(3) NOT NULL,
        [ReturnUrlOrigin] nvarchar(2048) NOT NULL,
        [RedirectUrl] nvarchar(2048) NOT NULL,
        [TransactionCode] nvarchar(100) NOT NULL,
        [Status] nvarchar(40) NOT NULL,
        [ReconciliationStatus] nvarchar(80) NOT NULL,
        [EvidenceReference] nvarchar(500) NOT NULL,
        [ProviderResponse] nvarchar(max) NOT NULL,
        [CreatedAtUtc] datetime2 NOT NULL,
        [UpdatedAtUtc] datetime2 NULL,
        CONSTRAINT [PK_GatewayPaymentIntents] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_GatewayIntent_Amount] CHECK (Amount > 0),
        CONSTRAINT [FK_GatewayPaymentIntents_Orders_OrderId] FOREIGN KEY ([OrderId]) REFERENCES [Orders] ([Id]) ON DELETE NO ACTION
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261023033757_CompleteCommerceLifecycle'
)
BEGIN
    CREATE TABLE [GatewayRefundAttempt] (
        [Id] uniqueidentifier NOT NULL,
        [RefundId] uniqueidentifier NOT NULL,
        [PaymentId] uniqueidentifier NOT NULL,
        [Amount] decimal(18,2) NOT NULL,
        [Status] nvarchar(40) NOT NULL,
        [ProviderTransactionCode] nvarchar(100) NOT NULL,
        [CreatedAtUtc] datetime2 NOT NULL,
        CONSTRAINT [PK_GatewayRefundAttempt] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_GatewayRefundAttempt_Payments_PaymentId] FOREIGN KEY ([PaymentId]) REFERENCES [Payments] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_GatewayRefundAttempt_Refunds_RefundId] FOREIGN KEY ([RefundId]) REFERENCES [Refunds] ([Id]) ON DELETE NO ACTION
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261023033757_CompleteCommerceLifecycle'
)
BEGIN
    CREATE TABLE [NotificationOutbox] (
        [Id] uniqueidentifier NOT NULL,
        [EventType] nvarchar(80) NOT NULL,
        [CustomerId] uniqueidentifier NULL,
        [Destination] nvarchar(254) NULL,
        [Channel] int NOT NULL,
        [Subject] nvarchar(200) NOT NULL,
        [PayloadJson] nvarchar(max) NOT NULL,
        [Status] int NOT NULL,
        [Attempts] int NOT NULL,
        [NextAttemptAtUtc] datetime2 NOT NULL,
        [LeasedUntilUtc] datetime2 NULL,
        [LeaseOwner] nvarchar(200) NULL,
        [SentAtUtc] datetime2 NULL,
        [LastError] nvarchar(1000) NULL,
        [CreatedAtUtc] datetime2 NOT NULL,
        CONSTRAINT [PK_NotificationOutbox] PRIMARY KEY ([Id])
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261023033757_CompleteCommerceLifecycle'
)
BEGIN
    CREATE TABLE [PasswordResetToken] (
        [Id] uniqueidentifier NOT NULL,
        [CustomerId] uniqueidentifier NOT NULL,
        [TokenHash] nvarchar(128) NOT NULL,
        [ExpiresAtUtc] datetime2 NOT NULL,
        [CreatedAtUtc] datetime2 NOT NULL,
        [UsedAtUtc] datetime2 NULL,
        CONSTRAINT [PK_PasswordResetToken] PRIMARY KEY ([Id])
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261023033757_CompleteCommerceLifecycle'
)
BEGIN
    CREATE TABLE [RateLimitCounters] (
        [Id] uniqueidentifier NOT NULL,
        [BucketKey] nvarchar(128) NOT NULL,
        [WindowStartUtc] datetime2 NOT NULL,
        [Count] int NOT NULL,
        [UpdatedAtUtc] datetime2 NOT NULL,
        CONSTRAINT [PK_RateLimitCounters] PRIMARY KEY ([Id])
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261023033757_CompleteCommerceLifecycle'
)
BEGIN
    CREATE TABLE [RefreshToken] (
        [Id] uniqueidentifier NOT NULL,
        [CustomerId] uniqueidentifier NOT NULL,
        [TokenHash] nvarchar(128) NOT NULL,
        [FamilyId] uniqueidentifier NOT NULL,
        [ExpiresAtUtc] datetime2 NOT NULL,
        [CreatedAtUtc] datetime2 NOT NULL,
        [RevokedAtUtc] datetime2 NULL,
        [ReplacedByHash] nvarchar(128) NULL,
        [RevocationReason] nvarchar(80) NULL,
        CONSTRAINT [PK_RefreshToken] PRIMARY KEY ([Id])
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261023033757_CompleteCommerceLifecycle'
)
BEGIN
    CREATE TABLE [ShippingQuotes] (
        [Id] uniqueidentifier NOT NULL,
        [CartFingerprint] nvarchar(64) NOT NULL,
        [CustomerId] uniqueidentifier NOT NULL,
        [Origin] nvarchar(500) NOT NULL,
        [Destination] nvarchar(2000) NOT NULL,
        [Weight] decimal(18,3) NOT NULL,
        [Amount] decimal(18,2) NOT NULL,
        [TrackingNumber] nvarchar(200) NULL,
        [ExpiresAt] datetime2 NOT NULL,
        [UsedAt] datetime2 NULL,
        [OrderId] uniqueidentifier NULL,
        CONSTRAINT [PK_ShippingQuotes] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_ShippingQuotes_Amounts] CHECK (Weight > 0 AND Amount >= 0)
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261023033757_CompleteCommerceLifecycle'
)
BEGIN
    CREATE TABLE [CustomQuotations] (
        [Id] uniqueidentifier NOT NULL,
        [CustomOrderRequestId] uniqueidentifier NOT NULL,
        [CurrentRevision] int NOT NULL,
        [Status] nvarchar(40) NOT NULL,
        [ExpiresAt] datetime2 NULL,
        [CreatedAt] datetime2 NOT NULL,
        [UpdatedAt] datetime2 NOT NULL,
        [ConcurrencyToken] uniqueidentifier NOT NULL,
        CONSTRAINT [PK_CustomQuotations] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_CustomQuotations_CustomOrderRequests_CustomOrderRequestId] FOREIGN KEY ([CustomOrderRequestId]) REFERENCES [CustomOrderRequests] ([Id]) ON DELETE NO ACTION
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261023033757_CompleteCommerceLifecycle'
)
BEGIN
    CREATE TABLE [CustomOrderCheckouts] (
        [Id] uniqueidentifier NOT NULL,
        [CustomQuotationId] uniqueidentifier NOT NULL,
        [QuotationVersion] int NOT NULL,
        [CustomerId] uniqueidentifier NOT NULL,
        [OrderId] uniqueidentifier NOT NULL,
        [IdempotencyKey] nvarchar(100) NOT NULL,
        [CreatedAt] datetime2 NOT NULL,
        CONSTRAINT [PK_CustomOrderCheckouts] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_CustomOrderCheckouts_CustomQuotations_CustomQuotationId] FOREIGN KEY ([CustomQuotationId]) REFERENCES [CustomQuotations] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_CustomOrderCheckouts_Orders_OrderId] FOREIGN KEY ([OrderId]) REFERENCES [Orders] ([Id]) ON DELETE NO ACTION
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261023033757_CompleteCommerceLifecycle'
)
BEGIN
    CREATE TABLE [CustomQuotationRevisions] (
        [Id] uniqueidentifier NOT NULL,
        [CustomQuotationId] uniqueidentifier NOT NULL,
        [Version] int NOT NULL,
        [UnitPrice] decimal(18,2) NOT NULL,
        [Quantity] int NOT NULL,
        [TotalAmount] decimal(18,2) NOT NULL,
        [DepositAmount] decimal(18,2) NOT NULL,
        [CompletionDate] datetime2 NOT NULL,
        [ExpiresAt] datetime2 NOT NULL,
        [CreatedBy] uniqueidentifier NOT NULL,
        [AdminNote] nvarchar(2000) NOT NULL,
        [CreatedAt] datetime2 NOT NULL,
        [Status] nvarchar(40) NOT NULL,
        CONSTRAINT [PK_CustomQuotationRevisions] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_CustomQuote_Amounts] CHECK (UnitPrice >= 0 AND TotalAmount >= 0 AND DepositAmount >= 0 AND CAST(DepositAmount AS DECIMAL(18,2)) <= CAST(TotalAmount AS DECIMAL(18,2)) AND Quantity > 0),
        CONSTRAINT [FK_CustomQuotationRevisions_CustomQuotations_CustomQuotationId] FOREIGN KEY ([CustomQuotationId]) REFERENCES [CustomQuotations] ([Id]) ON DELETE CASCADE
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261023033757_CompleteCommerceLifecycle'
)
BEGIN
    EXEC(N'ALTER TABLE [Products] ADD CONSTRAINT [CK_Product_ShippingWeight] CHECK (ShippingWeightGrams > 0)');
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261023033757_CompleteCommerceLifecycle'
)
BEGIN
    CREATE UNIQUE INDEX [IX_CustomOrderCheckouts_CustomerId_IdempotencyKey] ON [CustomOrderCheckouts] ([CustomerId], [IdempotencyKey]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261023033757_CompleteCommerceLifecycle'
)
BEGIN
    CREATE UNIQUE INDEX [IX_CustomOrderCheckouts_CustomQuotationId] ON [CustomOrderCheckouts] ([CustomQuotationId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261023033757_CompleteCommerceLifecycle'
)
BEGIN
    CREATE UNIQUE INDEX [IX_CustomOrderCheckouts_OrderId] ON [CustomOrderCheckouts] ([OrderId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261023033757_CompleteCommerceLifecycle'
)
BEGIN
    CREATE INDEX [IX_CustomOrderRequests_CustomerId_CreatedAt] ON [CustomOrderRequests] ([CustomerId], [CreatedAt]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261023033757_CompleteCommerceLifecycle'
)
BEGIN
    CREATE UNIQUE INDEX [IX_CustomQuotationRevisions_CustomQuotationId_Version] ON [CustomQuotationRevisions] ([CustomQuotationId], [Version]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261023033757_CompleteCommerceLifecycle'
)
BEGIN
    CREATE UNIQUE INDEX [IX_CustomQuotations_CustomOrderRequestId] ON [CustomQuotations] ([CustomOrderRequestId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261023033757_CompleteCommerceLifecycle'
)
BEGIN
    CREATE UNIQUE INDEX [IX_GatewayPaymentIntents_OrderId_ClientKey] ON [GatewayPaymentIntents] ([OrderId], [ClientKey]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261023033757_CompleteCommerceLifecycle'
)
BEGIN
    CREATE INDEX [IX_GatewayPaymentIntents_OrderId_OwnerId] ON [GatewayPaymentIntents] ([OrderId], [OwnerId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261023033757_CompleteCommerceLifecycle'
)
BEGIN
    CREATE UNIQUE INDEX [IX_GatewayPaymentIntents_ProviderKey] ON [GatewayPaymentIntents] ([ProviderKey]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261023033757_CompleteCommerceLifecycle'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [IX_GatewayPaymentIntents_TransactionCode] ON [GatewayPaymentIntents] ([TransactionCode]) WHERE [TransactionCode] <> ''''');
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261023033757_CompleteCommerceLifecycle'
)
BEGIN
    CREATE INDEX [IX_GatewayRefundAttempt_PaymentId] ON [GatewayRefundAttempt] ([PaymentId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261023033757_CompleteCommerceLifecycle'
)
BEGIN
    CREATE UNIQUE INDEX [IX_GatewayRefundAttempt_RefundId] ON [GatewayRefundAttempt] ([RefundId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261023033757_CompleteCommerceLifecycle'
)
BEGIN
    CREATE INDEX [IX_NotificationOutbox_LeasedUntilUtc] ON [NotificationOutbox] ([LeasedUntilUtc]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261023033757_CompleteCommerceLifecycle'
)
BEGIN
    CREATE INDEX [IX_NotificationOutbox_Status_NextAttemptAtUtc] ON [NotificationOutbox] ([Status], [NextAttemptAtUtc]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261023033757_CompleteCommerceLifecycle'
)
BEGIN
    CREATE INDEX [IX_PasswordResetToken_CustomerId_ExpiresAtUtc] ON [PasswordResetToken] ([CustomerId], [ExpiresAtUtc]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261023033757_CompleteCommerceLifecycle'
)
BEGIN
    CREATE UNIQUE INDEX [IX_PasswordResetToken_TokenHash] ON [PasswordResetToken] ([TokenHash]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261023033757_CompleteCommerceLifecycle'
)
BEGIN
    CREATE UNIQUE INDEX [IX_RateLimitCounters_BucketKey_WindowStartUtc] ON [RateLimitCounters] ([BucketKey], [WindowStartUtc]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261023033757_CompleteCommerceLifecycle'
)
BEGIN
    CREATE INDEX [IX_RateLimitCounters_UpdatedAtUtc] ON [RateLimitCounters] ([UpdatedAtUtc]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261023033757_CompleteCommerceLifecycle'
)
BEGIN
    CREATE INDEX [IX_RefreshToken_CustomerId_FamilyId] ON [RefreshToken] ([CustomerId], [FamilyId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261023033757_CompleteCommerceLifecycle'
)
BEGIN
    CREATE UNIQUE INDEX [IX_RefreshToken_TokenHash] ON [RefreshToken] ([TokenHash]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261023033757_CompleteCommerceLifecycle'
)
BEGIN
    CREATE INDEX [IX_ShippingQuotes_CustomerId_Id] ON [ShippingQuotes] ([CustomerId], [Id]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261023033757_CompleteCommerceLifecycle'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [IX_ShippingQuotes_OrderId] ON [ShippingQuotes] ([OrderId]) WHERE [OrderId] IS NOT NULL');
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261023033757_CompleteCommerceLifecycle'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261023033757_CompleteCommerceLifecycle', N'8.0.22');
END;
GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261024142218_AddConfirmedPackagingSpecs'
)
BEGIN
    ALTER TABLE [Products] DROP CONSTRAINT [CK_Product_ShippingWeight];
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261024142218_AddConfirmedPackagingSpecs'
)
BEGIN
    DECLARE @var0 sysname;
    SELECT @var0 = [d].[name]
    FROM [sys].[default_constraints] [d]
    INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
    WHERE ([d].[parent_object_id] = OBJECT_ID(N'[Products]') AND [c].[name] = N'ShippingWeightGrams');
    IF @var0 IS NOT NULL EXEC(N'ALTER TABLE [Products] DROP CONSTRAINT [' + @var0 + '];');
    ALTER TABLE [Products] ALTER COLUMN [ShippingWeightGrams] int NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261024142218_AddConfirmedPackagingSpecs'
)
BEGIN
    ALTER TABLE [Products] ADD [PackageHeightCm] decimal(18,2) NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261024142218_AddConfirmedPackagingSpecs'
)
BEGIN
    ALTER TABLE [Products] ADD [PackageLengthCm] decimal(18,2) NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261024142218_AddConfirmedPackagingSpecs'
)
BEGIN
    ALTER TABLE [Products] ADD [PackageWidthCm] decimal(18,2) NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261024142218_AddConfirmedPackagingSpecs'
)
BEGIN
    ALTER TABLE [Products] ADD [PackagingSpecsConfirmed] bit NOT NULL DEFAULT CAST(0 AS bit);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261024142218_AddConfirmedPackagingSpecs'
)
BEGIN
    EXEC(N'ALTER TABLE [Products] ADD CONSTRAINT [CK_Product_ConfirmedPackaging] CHECK (PackagingSpecsConfirmed = 0 OR (ShippingWeightGrams IS NOT NULL AND ShippingWeightGrams > 0 AND PackageLengthCm IS NOT NULL AND PackageLengthCm > 0 AND PackageWidthCm IS NOT NULL AND PackageWidthCm > 0 AND PackageHeightCm IS NOT NULL AND PackageHeightCm > 0))');
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261024142218_AddConfirmedPackagingSpecs'
)
BEGIN
    EXEC(N'ALTER TABLE [Products] ADD CONSTRAINT [CK_Product_PackageDimensions] CHECK ((PackageLengthCm IS NULL OR PackageLengthCm > 0) AND (PackageWidthCm IS NULL OR PackageWidthCm > 0) AND (PackageHeightCm IS NULL OR PackageHeightCm > 0))');
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261024142218_AddConfirmedPackagingSpecs'
)
BEGIN
    EXEC(N'ALTER TABLE [Products] ADD CONSTRAINT [CK_Product_ShippingWeight] CHECK (ShippingWeightGrams IS NULL OR ShippingWeightGrams > 0)');
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261024142218_AddConfirmedPackagingSpecs'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261024142218_AddConfirmedPackagingSpecs', N'8.0.22');
END;
GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261113043133_AddGhnTwoLevelAddress'
)
BEGIN
    ALTER TABLE [ShippingQuotes] ADD [ProvinceId] int NOT NULL DEFAULT 0;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261113043133_AddGhnTwoLevelAddress'
)
BEGIN
    ALTER TABLE [ShippingQuotes] ADD [ProvinceName] nvarchar(200) NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261113043133_AddGhnTwoLevelAddress'
)
BEGIN
    ALTER TABLE [ShippingQuotes] ADD [WardId] int NOT NULL DEFAULT 0;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261113043133_AddGhnTwoLevelAddress'
)
BEGIN
    ALTER TABLE [ShippingQuotes] ADD [WardName] nvarchar(200) NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261113043133_AddGhnTwoLevelAddress'
)
BEGIN
    ALTER TABLE [Orders] ADD [ShippingProvinceId] int NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261113043133_AddGhnTwoLevelAddress'
)
BEGIN
    ALTER TABLE [Orders] ADD [ShippingProvinceName] nvarchar(200) NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261113043133_AddGhnTwoLevelAddress'
)
BEGIN
    ALTER TABLE [Orders] ADD [ShippingWardId] int NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261113043133_AddGhnTwoLevelAddress'
)
BEGIN
    ALTER TABLE [Orders] ADD [ShippingWardName] nvarchar(200) NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261113043133_AddGhnTwoLevelAddress'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261113043133_AddGhnTwoLevelAddress', N'8.0.22');
END;
GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261113051330_AddCustomRequestDeliveryArea'
)
BEGIN
    ALTER TABLE [CustomOrderRequests] ADD [DeliveryProvinceId] int NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261113051330_AddCustomRequestDeliveryArea'
)
BEGIN
    ALTER TABLE [CustomOrderRequests] ADD [DeliveryProvinceName] nvarchar(200) NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261113051330_AddCustomRequestDeliveryArea'
)
BEGIN
    ALTER TABLE [CustomOrderRequests] ADD [DeliveryWardId] int NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261113051330_AddCustomRequestDeliveryArea'
)
BEGIN
    ALTER TABLE [CustomOrderRequests] ADD [DeliveryWardName] nvarchar(200) NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261113051330_AddCustomRequestDeliveryArea'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261113051330_AddCustomRequestDeliveryArea', N'8.0.22');
END;
GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261113102019_AddCustomBouquetSnapshots'
)
BEGIN
    ALTER TABLE [CustomQuotationRevisions] ADD [BouquetSnapshot] nvarchar(max) NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261113102019_AddCustomBouquetSnapshots'
)
BEGIN
    ALTER TABLE [CustomOrderRequests] ADD [BouquetSnapshot] nvarchar(max) NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261113102019_AddCustomBouquetSnapshots'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261113102019_AddCustomBouquetSnapshots', N'8.0.22');
END;
GO

COMMIT;
GO

