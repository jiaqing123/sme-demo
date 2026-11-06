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
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261106131822_InitialCreate'
)
BEGIN
    IF SCHEMA_ID(N'app') IS NULL EXEC(N'CREATE SCHEMA [app];');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261106131822_InitialCreate'
)
BEGIN
    CREATE TABLE [app].[cards] (
        [Id] uniqueidentifier NOT NULL,
        [UserId] uniqueidentifier NOT NULL,
        [OrgId] nvarchar(32) NOT NULL,
        [Brand] nvarchar(32) NOT NULL,
        [Last4] nchar(4) NOT NULL,
        [ExpiryMonth] tinyint NOT NULL,
        [ExpiryYear] smallint NOT NULL,
        [Status] nvarchar(16) NOT NULL,
        [Currency] nchar(3) NOT NULL,
        [DisplayOrder] smallint NOT NULL,
        [ArtBlobName] nvarchar(128) NOT NULL,
        CONSTRAINT [PK_cards] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261106131822_InitialCreate'
)
BEGIN
    CREATE TABLE [app].[issuer_sync] (
        [CardId] uniqueidentifier NOT NULL,
        [LastSyncedAt] datetime2 NOT NULL,
        [LastStatus] nvarchar(16) NOT NULL,
        [LastError] nvarchar(512) NULL,
        CONSTRAINT [PK_issuer_sync] PRIMARY KEY ([CardId])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261106131822_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_cards_UserId_DisplayOrder] ON [app].[cards] ([UserId], [DisplayOrder]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261106131822_InitialCreate'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261106131822_InitialCreate', N'10.0.14');
END;

COMMIT;
GO

