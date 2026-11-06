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
    WHERE [MigrationId] = N'20261106133620_InitialCreate'
)
BEGIN
    IF SCHEMA_ID(N'auth') IS NULL EXEC(N'CREATE SCHEMA [auth];');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261106133620_InitialCreate'
)
BEGIN
    CREATE TABLE [auth].[users] (
        [id] uniqueidentifier NOT NULL,
        [org_id] nvarchar(32) NOT NULL,
        [username] nvarchar(128) NOT NULL,
        [password_hash] nvarchar(256) NOT NULL,
        [name] nvarchar(128) NOT NULL,
        [phone] nvarchar(32) NOT NULL,
        [status] nvarchar(16) NOT NULL,
        [created_at] datetime2 NOT NULL,
        CONSTRAINT [PK_users] PRIMARY KEY ([id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261106133620_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [IX_users_username] ON [auth].[users] ([username]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261106133620_InitialCreate'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261106133620_InitialCreate', N'10.0.14');
END;

COMMIT;
GO

