-- Azure SQL Database application security bootstrap.
--
-- Run this one file once per database connection:
--
--   master   -> create/update the server-level logins sme_api_app and sme_auth_app
--   sme_api  -> create the sme_api_app database user, grant db_datareader + db_datawriter
--   sme_auth -> create the sme_auth_app database user, grant db_datareader + db_datawriter
--
-- Azure SQL Database does not support USE [other_database] or three-part names
-- such as master.sys.sql_logins, so no single connection can safely do all three.
-- The migrator already opens one connection per database, so each batch below
-- dispatches on DB_NAME() and the same file is applied once per database with
-- BOOTSTRAP_SQL. The master batch executes its login statements as dynamic SQL
-- because sys.sql_logins is a master-only catalog view in Azure SQL Database and
-- must not be bound while the same batch is executed against a user database.
--
-- ${...} placeholders are replaced by the migrator from environment variables,
-- so no password is committed here or baked into the image. This file is
-- idempotent: re-running it updates the login password and leaves existing
-- users and role memberships untouched.

SET NOCOUNT ON;
GO

-- Server-level logins exist only in master.
IF DB_NAME() = N'master'
BEGIN
    EXEC (N'
        IF NOT EXISTS (SELECT 1 FROM sys.sql_logins WHERE name = N''sme_api_app'')
        BEGIN
            CREATE LOGIN [sme_api_app]
                WITH PASSWORD = N''${SME_API_DB_PASSWORD}'', CHECK_POLICY = ON;
        END
        ELSE
        BEGIN
            ALTER LOGIN [sme_api_app] WITH PASSWORD = N''${SME_API_DB_PASSWORD}'';
        END;

        IF NOT EXISTS (SELECT 1 FROM sys.sql_logins WHERE name = N''sme_auth_app'')
        BEGIN
            CREATE LOGIN [sme_auth_app]
                WITH PASSWORD = N''${SME_AUTH_DB_PASSWORD}'', CHECK_POLICY = ON;
        END
        ELSE
        BEGIN
            ALTER LOGIN [sme_auth_app] WITH PASSWORD = N''${SME_AUTH_DB_PASSWORD}'';
        END;
    ');
END;
GO

-- API database user: least privilege is db_datareader + db_datawriter only.
IF DB_NAME() = N'sme_api'
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM sys.database_principals
        WHERE name = N'sme_api_app' AND type = 'S'
    )
    BEGIN
        CREATE USER [sme_api_app] FOR LOGIN [sme_api_app];
    END;

    IF NOT EXISTS (
        SELECT 1
        FROM sys.database_role_members AS membership
        JOIN sys.database_principals AS role_principal
            ON role_principal.principal_id = membership.role_principal_id
        JOIN sys.database_principals AS member_principal
            ON member_principal.principal_id = membership.member_principal_id
        WHERE role_principal.name = N'db_datareader' AND member_principal.name = N'sme_api_app'
    )
    BEGIN
        ALTER ROLE [db_datareader] ADD MEMBER [sme_api_app];
    END;

    IF NOT EXISTS (
        SELECT 1
        FROM sys.database_role_members AS membership
        JOIN sys.database_principals AS role_principal
            ON role_principal.principal_id = membership.role_principal_id
        JOIN sys.database_principals AS member_principal
            ON member_principal.principal_id = membership.member_principal_id
        WHERE role_principal.name = N'db_datawriter' AND member_principal.name = N'sme_api_app'
    )
    BEGIN
        ALTER ROLE [db_datawriter] ADD MEMBER [sme_api_app];
    END;
END;
GO

-- Auth database user: same least-privilege shape as the API database.
IF DB_NAME() = N'sme_auth'
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM sys.database_principals
        WHERE name = N'sme_auth_app' AND type = 'S'
    )
    BEGIN
        CREATE USER [sme_auth_app] FOR LOGIN [sme_auth_app];
    END;

    IF NOT EXISTS (
        SELECT 1
        FROM sys.database_role_members AS membership
        JOIN sys.database_principals AS role_principal
            ON role_principal.principal_id = membership.role_principal_id
        JOIN sys.database_principals AS member_principal
            ON member_principal.principal_id = membership.member_principal_id
        WHERE role_principal.name = N'db_datareader' AND member_principal.name = N'sme_auth_app'
    )
    BEGIN
        ALTER ROLE [db_datareader] ADD MEMBER [sme_auth_app];
    END;

    IF NOT EXISTS (
        SELECT 1
        FROM sys.database_role_members AS membership
        JOIN sys.database_principals AS role_principal
            ON role_principal.principal_id = membership.role_principal_id
        JOIN sys.database_principals AS member_principal
            ON member_principal.principal_id = membership.member_principal_id
        WHERE role_principal.name = N'db_datawriter' AND member_principal.name = N'sme_auth_app'
    )
    BEGIN
        ALTER ROLE [db_datawriter] ADD MEMBER [sme_auth_app];
    END;
END;
GO
