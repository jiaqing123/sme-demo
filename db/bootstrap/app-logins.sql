SET NOCOUNT ON;
GO

-- Application logins are created on master and then mapped to a database user in
-- the database each one owns. The passwords arrive as sqlcmd variables so no
-- secret is committed in this script or baked into an image.
IF NOT EXISTS (SELECT 1 FROM sys.sql_logins WHERE name = N'sme_api_app')
BEGIN
    CREATE LOGIN [sme_api_app] WITH PASSWORD = N'$(SME_API_DB_PASSWORD)', CHECK_POLICY = ON;
END
ELSE
BEGIN
    ALTER LOGIN [sme_api_app] WITH PASSWORD = N'$(SME_API_DB_PASSWORD)';
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.sql_logins WHERE name = N'sme_auth_app')
BEGIN
    CREATE LOGIN [sme_auth_app] WITH PASSWORD = N'$(SME_AUTH_DB_PASSWORD)', CHECK_POLICY = ON;
END
ELSE
BEGIN
    ALTER LOGIN [sme_auth_app] WITH PASSWORD = N'$(SME_AUTH_DB_PASSWORD)';
END;
GO

USE [sme_api];
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.database_principals
    WHERE name = N'sme_api_app' AND type = 'S'
)
BEGIN
    CREATE USER [sme_api_app] FOR LOGIN [sme_api_app];
END;
GO

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
GO

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
GO

USE [sme_auth];
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.database_principals
    WHERE name = N'sme_auth_app' AND type = 'S'
)
BEGIN
    CREATE USER [sme_auth_app] FOR LOGIN [sme_auth_app];
END;
GO

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
GO

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
GO
