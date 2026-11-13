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

